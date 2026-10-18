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

        // Linux: the directory layout is a per-distro decision. Debian/Ubuntu
        // use truetype/<family>/, Fedora/RHEL use <packagename>-fonts/, and what
        // ships at all depends on which font packages were installed. Rather than
        // trying to enumerate every spelling, name the handful that are common
        // and let the directory scan below do the real work.
        yield return "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc";
        yield return "/usr/share/fonts/opentype/noto/NotoSansCJKsc-Regular.otf";
        yield return "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc";
        yield return "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc";
        yield return "/usr/share/fonts/truetype/arphic/uming.ttc";
        yield return "/usr/share/fonts/truetype/arphic/ukai.ttc";
        yield return "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
        yield return "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans.ttf";
        yield return "/usr/share/fonts/liberation-sans-fonts/LiberationSans-Regular.ttf";
        yield return "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";
        yield return "/usr/share/fonts/truetype/freefont/FreeSans.ttf";
        yield return "/usr/share/fonts/truetype/ubuntu/Ubuntu-R.ttf";
    }

    /// <summary>File name fragments that mean "this font can draw CJK".</summary>
    private static readonly string[] CjkMarkers =
        ["cjk", "wqy", "zenhei", "microhei", "droidsansfallback", "arphic",
         "uming", "ukai", "notosanssc", "notosanstc"];

    /// <summary>Plain Latin sans-serif fallbacks, in preference order.</summary>
    private static readonly string[] SansMarkers =
        ["dejavu", "liberation", "notosans", "freesans", "ubuntu", "roboto", "cantarell"];

    private static bool IsFontFile(string path) =>
        path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string path, string[] markers) =>
        markers.Any(m => Path.GetFileName(path).Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Last resort: pick the best font in the system directory.
    ///
    /// Ordered, not arbitrary. Taking "the first .ttf found" would happily return
    /// a symbol font or a Latin-only one, and a Chinese watermark would then
    /// render as empty boxes -- which looks like a broken build rather than a
    /// missing font. So CJK-capable files win, then known sans-serif families,
    /// then anything at all.
    /// </summary>
    private static string? ScanFontDirectory()
    {
        string root = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts")
            : "/usr/share/fonts";

        if (!Directory.Exists(root)) return null;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(IsFontFile)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (files.Count == 0) return null;

        return files.FirstOrDefault(p => Matches(p, CjkMarkers))
            ?? files.FirstOrDefault(p => Matches(p, SansMarkers))
            ?? files[0];
    }

    /// <summary>
    /// What to tell someone whose machine has no fonts at all. Headless boxes and
    /// minimal container images routinely ship without any, and "no usable font
    /// found" on its own sends people looking for a bug in the encoder.
    /// </summary>
    public static string InstallHint => OperatingSystem.IsWindows()
        ? "no fonts found; check %WINDIR%\\Fonts"
        : OperatingSystem.IsMacOS()
            ? "no fonts found; install one under /Library/Fonts or /System/Library/Fonts"
            : "no fonts found; install a font package (Debian/Ubuntu: "
              + "sudo apt install fonts-dejavu-core fonts-noto-cjk; "
              + "Fedora/RHEL: sudo dnf install dejavu-sans-fonts liberation-sans-fonts)";

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
