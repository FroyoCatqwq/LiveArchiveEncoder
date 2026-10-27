using System.Net;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>
/// The optional GitHub download front (gh-proxy.com and its look-alikes).
///
/// <para>Two hosts are involved and they behave differently: a release asset
/// lives on <c>github.com/&lt;owner&gt;/&lt;repo&gt;/releases/download/...</c> and
/// redirects to <c>objects.githubusercontent.com</c>, while a single tracked file
/// lives on <c>raw.githubusercontent.com</c>. A proxy is worth having because
/// both are frequently unreachable from mainland China, and both are handled the
/// same way: prefix the whole URL, so the proxy re-issues the request from its
/// own side. Measured 2026-10-27 against gh-proxy.com with the znedi3 weights
/// (13,574,928 bytes): the prefixed URL answers <c>206</c> with the right
/// <c>Content-Range</c>, so range/resume still work through it.</para>
///
/// <para>Deliberately a rewrite of the URL rather than a proxy setting: the
/// point of the feature is to be used <em>instead of</em> a local VPN, so it has
/// to work on a machine with no proxy configured at all.</para>
/// </summary>
public static class GitHubProxy
{
    /// <summary>The spelling <c>--gh-proxy</c> means when it is given no value.</summary>
    public const string DefaultPrefix = "https://gh-proxy.com/";

    /// <summary>
    /// The literal stored in the settings file for "asked for no front". It has
    /// to be a value rather than an empty string: empty means "never asked",
    /// which is what lets a download probe for a route on its own.
    /// </summary>
    public const string Off = "off";

    /// <summary>Hosts a GitHub front can serve. Anything else is left alone.</summary>
    private static readonly string[] Hosts =
    [
        "github.com",
        "api.github.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
        "codeload.github.com",
        "gist.githubusercontent.com",
    ];

    /// <summary>
    /// Validate and normalise a prefix: one trailing slash, and an absolute
    /// http(s) URL. Returns an error instead of a value for anything else --
    /// a typo'd prefix must not silently become "download straight from
    /// GitHub", which is the case it exists to rescue.
    /// </summary>
    public static (string? Prefix, string? Error) Normalise(string? value)
    {
        if (!value.HasText) return (null, null);
        string text = value.Trim();
        if (string.Equals(text, Off, StringComparison.OrdinalIgnoreCase)) return (null, null);

        // "gh-proxy.com" and "gh-proxy.com/" are what people type.
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (null, $"'{value}' is not an http(s) URL");
        }
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return (null, $"'{value}' must be a bare prefix, without ? or #");

        string prefix = uri.ToString();
        if (!prefix.EndsWith('/')) prefix += "/";
        return (prefix, null);
    }

    /// <summary>True when <paramref name="url"/> points at a host a front can serve.</summary>
    public static bool CanWrap(string? url)
    {
        if (!url.HasText) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        foreach (string host in Hosts)
        {
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// The URL to actually fetch. Unchanged when there is no prefix or the host
    /// is not GitHub's -- a local mirror, gyan.dev and SourceForge all stay
    /// exactly as they were.
    /// </summary>
    public static string Apply(string url, string? prefix) =>
        prefix.HasText && CanWrap(url) ? prefix + url : url;

    /// <summary>The inverse, for messages and for storing what the user chose.</summary>
    public static string Describe(string? prefix) =>
        prefix.HasText ? prefix : "off";

    /// <summary>
    /// Which route a download should take when the caller left it open: null to
    /// fetch straight from GitHub, otherwise a prefix to fetch through.
    ///
    /// <para>Measured 2026-10-27 from a mainland-China line:
    /// <c>raw.githubusercontent.com</c> accepts the connection and then delivers
    /// nothing (curl: timed out after 20 s with 0 bytes), while the same file
    /// through <c>gh-proxy.com</c> comes back <c>206</c> in about 70 seconds. A
    /// blocked host is worse than a slow one -- the request looks alive, so
    /// "just try the download" wedges for the whole stall timeout. Five seconds
    /// of probing decides the route, and a machine that can reach GitHub
    /// directly never touches the front.</para>
    ///
    /// <para><paramref name="url"/> only has to be a GitHub URL that exists; the
    /// request is a one-kilobyte range, not a download.</para>
    /// </summary>
    public static string? Detect(string url, string? proxy, Action<string>? log = null)
    {
        if (!CanWrap(url)) return null;
        if (Reachable(url, proxy)) return null;

        log?.Invoke($"GitHub does not answer directly; using {DefaultPrefix}");
        log?.Invoke("(--gh-proxy remembers that choice, --no-gh-proxy turns it off)");
        return DefaultPrefix;
    }

    /// <summary>
    /// One ranged request, five seconds, no body: enough to tell "reachable" from
    /// "connects and then hangs", which is the failure this guards against.
    /// </summary>
    private static bool Reachable(string url, string? proxy)
    {
        try
        {
            using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) };
            if (proxy.HasText && Uri.TryCreate(proxy, UriKind.Absolute, out Uri? proxyUri))
            {
                handler.Proxy = new WebProxy(proxyUri);
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveArchiveEncoder");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
            using HttpResponseMessage response =
                client.Send(request, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exc) when (exc is HttpRequestException or IOException
                                        or TaskCanceledException or OperationCanceledException
                                        or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
