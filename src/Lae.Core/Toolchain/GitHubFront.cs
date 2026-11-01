namespace Lae.Core.Toolchain;

/// <summary>
/// The optional GitHub download front (<c>gh-proxy.com</c> and its look-alikes).
///
/// <para>A release asset lives on <c>github.com/&lt;owner&gt;/&lt;repo&gt;/releases/download/…</c>
/// and redirects to <c>objects.githubusercontent.com</c>. Both are frequently
/// unreachable from mainland China, and both are handled the same way: prefix the
/// whole URL so the front re-issues the request from its own side. Measured
/// 2026-10-27 against gh-proxy.com: the prefixed URL answers <c>206</c> with a
/// correct <c>Content-Range</c>, so the same request works either way.</para>
///
/// <para>A rewrite rather than a proxy setting, on purpose: the point of the
/// front is to be used <em>instead of</em> a local VPN, so it has to work on a
/// machine that has no proxy configured at all.</para>
/// </summary>
public static class GitHubFront
{
    /// <summary>What <c>--front</c> with no value means.</summary>
    public const string DefaultPrefix = "https://gh-proxy.com/";

    /// <summary>
    /// The stored value meaning "asked for no front". A value rather than an empty
    /// string, because empty means "never asked" -- which is what lets an install
    /// probe the route on its own the first time.
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
    /// Validate and normalise a prefix: an absolute http(s) URL with one trailing
    /// slash. Returns an error instead of a value for anything else -- a typo'd
    /// prefix must not silently become "download straight from GitHub", which is
    /// the case it exists to rescue.
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
        return (prefix.EndsWith('/') ? prefix : prefix + "/", null);
    }

    /// <summary>True when the address points at a host a front can serve.</summary>
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
    /// The address to fetch. Unchanged when there is no prefix or the host is not
    /// GitHub's -- a mirror of one's own stays exactly as it was.
    /// </summary>
    public static string Apply(string url, string? prefix)
        => prefix.HasText && CanWrap(url) ? prefix + url : url;
}
