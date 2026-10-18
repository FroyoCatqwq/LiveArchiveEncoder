using System.Runtime.InteropServices;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>
/// Finds a font file <c>drawtext</c> can actually use.
///
/// This is not cosmetic. FFmpeg's drawtext needs libfreetype plus a real font
/// file, and the default (no <c>fontfile</c>) resolves to something that does not
/// exist on most Linux boxes -- the pass fails with "no font found". Worse,
/// picking Arial is fine for ASCII and silently wrong for Chinese: it has no CJK
/// glyphs, so the watermark renders as blanks or boxes. The candidate list
/// therefore prefers a font that covers the user's script.
/// </summary>
public static class FontResolver
{
    /// <summary>Environment override, for CI boxes with no fonts directory.</summary>
    public const string EnvironmentVariable = "LAE_WATERMARK_FONT";

    /// <summary>
    /// First existing candidate, or null when nothing on this machine can be
    /// found (in which case drawtext is left to its own devices and may fail --
    /// callers report that rather than pretending it worked).
    /// </summary>
    public static string? Find()
    {
        string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        foreach (string candidate in Candidates())
            if (File.Exists(candidate)) return candidate;

        return ScanFontDirectory();
    }

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (fonts.Length == 0) fonts = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

            // CJK-capable first: a Chinese watermark is the common case here.
            yield return Path.Combine(fonts, "msyh.ttc");     // Microsoft YaHei
            yield return Path.Combine(fonts, "simhei.ttf");   // SimHei
            yield return Path.Combine(fonts, "simsun.ttc");   // SimSun
            yield return Path.Combine(fonts, "arial.ttf");
            yield return Path.Combine(fonts, "tahoma.ttf");
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts/STHeiti Medium.ttc";
            yield return "/System/Library/Fonts/PingFang.ttc";
            yield return "/System/Library/Fonts/Supplemental/Arial.ttf";
            yield return "/Library/Fonts/Arial.ttf";
            yield break;
        }

        // Linux: distribution layout varies, so list the common CJK fonts and
        // then the Latin fallbacks every distro ships.
        yield return "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc";
        yield return "/usr/share/fonts/opentype/noto/NotoSansCJKsc-Regular.otf";
        yield return "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc";
        yield return "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc";
        yield return "/usr/share/fonts/truetype/arphic/uming.ttc";
        yield return "/usr/share/fonts/truetype/arphic/ukai.ttc";
        yield return "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
        yield return "/usr/share/fonts/truetype/freefont/FreeSans.ttf";
        yield return "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";
        yield return "/usr/share/fonts/truetype/ubuntu/Ubuntu-R.ttf";
    }

    /// <summary>
    /// Last resort: take the first scalable font in the system directory.
    /// Better an arbitrary font that renders than no watermark at all.
    /// </summary>
    private static string? ScanFontDirectory()
    {
        string root = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts")
            : "/usr/share/fonts";

        if (!Directory.Exists(root)) return null;

        try
        {
            return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// FFmpeg's filter parser reads a backslash as an escape, so Windows paths
    /// must be written with forward slashes -- which FFmpeg accepts on every
    /// platform. Colons are then escaped so the drive letter does not split the
    /// option.
    /// </summary>
    public static string ToFilterPath(string path)
    {
        string normalised = path.Replace('\\', '/');
        return PostArgs.EscapeFilterValue(normalised);
    }
}
