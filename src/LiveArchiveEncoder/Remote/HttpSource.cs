using System.Text;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.Remote;

/// <summary>
/// HTTP/HTTPS sources.
///
/// v7.9: an input may be an <c>http://</c> or <c>https://</c> URL, which is how
/// the big public archives serve their files (archive.org's
/// <c>/download/&lt;item&gt;/&lt;file&gt;</c> being the canonical example).
/// FFmpeg speaks both protocols natively, so "support" here means: recognise
/// the URL, hand it to FFmpeg unchanged, and give the network read the
/// resilience options the protocols actually have.
///
/// Deliberately <b>initial</b> support. What is <em>not</em> handled:
/// authentication, cookies, playlists/manifests (HLS/DASH), and anything that
/// needs a two-pass read of a file the server will only serve once. What is
/// handled is the ordinary case -- a progressive file over a CDN that honours
/// Range requests -- end to end, including seeking to a time range.
/// </summary>
public static class HttpSource
{
    /// <summary>True for <c>http://...</c> and <c>https://...</c>.</summary>
    public static bool IsUrl(string? path) =>
        path.HasText
        && (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// File name a URL points at, for the default output name.
    ///
    /// <c>https://host/download/item/Rick_Astley...mp4?download=1</c> becomes
    /// <c>Rick_Astley...mp4</c>: the last path segment, query string dropped,
    /// percent-decoded, and sanitised so a URL cannot inject path separators
    /// into the output name.
    /// </summary>
    public static string FileNameFromUrl(string url)
    {
        string body = url;
        int scheme = body.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) body = body[(scheme + 3)..];

        int cut = body.IndexOfAny(['?', '#']);
        if (cut >= 0) body = body[..cut];

        int slash = body.LastIndexOf('/');
        if (slash >= 0) body = body[(slash + 1)..];

        string decoded;
        try { decoded = Uri.UnescapeDataString(body); }
        catch (ArgumentException) { decoded = body; }

        StringBuilder safe = new(decoded.Length);
        foreach (char c in decoded)
        {
            if (c is '/' or '\\' or '\0') safe.Append('_');
            else safe.Append(c);
        }
        string name = safe.ToString().Trim();
        return name.Length > 0 ? name : "remote";
    }

    /// <summary>
    /// FFmpeg options for reading a URL, empty for anything else.
    ///
    /// The <c>-reconnect</c> family belongs to the HTTP/FTP protocols -- unlike
    /// on a file or UNC path, where FFmpeg rejects it outright (see
    /// <see cref="Smb.RemoteReadArgs"/>). Three of them, and only three:
    /// <c>-reconnect 1</c> retry a dropped connection once it comes back,
    /// <c>-reconnect_streamed 1</c> do the same for a non-seekable stream, and
    /// <c>-reconnect_on_network_error 1</c> cover TCP resets mid-transfer.
    /// <c>-reconnect_on_http_error</c> is left out on purpose: a 404 is a wrong
    /// URL, and retrying it five times turns a two-second error into a
    /// two-minute one.
    ///
    /// <c>-rw_timeout</c> is the socket timeout (30 s), so a stalled server
    /// fails the run instead of hanging the pipeline forever.
    /// </summary>
    public static List<string> ReadArgs(string path)
    {
        if (!IsUrl(path)) return [];
        return
        [
            "-reconnect", "1",
            "-reconnect_streamed", "1",
            "-reconnect_on_network_error", "1",
            "-rw_timeout", "30000000",
        ];
    }

    /// <summary>
    /// Same treatment for ffprobe, which touches the URL first. ffprobe
    /// accepts the protocol options too (verified 2026-10-21), so the probe
    /// and the encode fail the same way instead of one hanging.
    /// </summary>
    public static List<string> ProbeArgs(string path)
    {
        if (!IsUrl(path)) return [];
        return ["-rw_timeout", "30000000"];
    }
}
