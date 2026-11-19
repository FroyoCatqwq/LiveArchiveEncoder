using System.Globalization;

namespace LiveArchiveEncoder.UI;

/// <summary>
/// v7.9.9.13: value-level parsing helpers extracted from the argument switch.
/// Keeping them beside (rather than inside) the 1,700-line switch makes the
/// invariant-culture rules reviewable on their own. No public API changes.
/// </summary>
public static partial class CliParser
{
    private static bool TryDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    // v7.9.9.11: int options used CurrentCulture while every float and --emit-args
    // value is invariant. One command could parse differently per machine.
    private static bool TryInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// A <c>--gh-proxy</c> value: "" for off, the normalised prefix otherwise.
    /// An unparseable URL is passed through unchanged so that <c>Cli.Run</c> can
    /// refuse it with a message instead of the download failing 40 MB in.
    /// </summary>
    private static string NormaliseFront(string value, out string? error)
    {
        error = null;
        string text = value.Trim();
        if (text is "" or "-" or "off" or "none" or "no") return "";
        if (text is "on" or "yes" or "default") return FFmpeg.GitHubProxy.DefaultPrefix;
        (string? prefix, error) = FFmpeg.GitHubProxy.Normalise(text);
        return prefix ?? text;
    }

    /// <summary>
    /// Decide whether the token after a bare <c>--gh-proxy</c> is a value or the
    /// positional input. Only URLs, the on/off words and bare hostnames qualify;
    /// an ordinary filename is left for the positional pass.
    /// </summary>
    private static bool LooksLikeFront(string value)
    {
        string text = value.Trim();
        if (text.Length == 0 || text.StartsWith('-')) return false;
        if (text is "off" or "none" or "no" or "on" or "yes" or "default" or "-") return true;
        if (text.Contains("://", StringComparison.Ordinal) || text.EndsWith('/')) return true;
        if (File.Exists(text) || text.Contains('/') || text.Contains('\\')) return false;
        if (!text.Contains('.')) return false;
        string ext = Path.GetExtension(text).TrimStart('.').ToLowerInvariant();
        // A media/data filename has a short extension; a bare host has a TLD.
        return ext is "com" or "net" or "org" or "io" or "dev" or "cn"
            or "app" or "xyz" or "site" or "top" or "me" or "cc";
    }
}
