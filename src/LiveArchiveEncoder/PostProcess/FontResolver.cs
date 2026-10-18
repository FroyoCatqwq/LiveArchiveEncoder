using System.Runtime.InteropServices;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>
/// Finds the font file <c>drawtext</c> should use.
///
/// Fonts live in the program's own <c>fonts</c> directory, a sibling of the
/// managed <c>ffmpeg</c> directory:
///
/// <code>
/// %LOCALAPPDATA%\LiveArchiveEncoder\fonts\      (Windows)
/// ~/.local/share/LiveArchiveEncoder/fonts/      (Linux)
/// </code>
///
/// System fonts are deliberately not consulted at encode time. Two reasons.
/// First, "whichever font is installed" differs on every machine: a minimal
/// container image often has no <c>/usr/share/fonts</c> at all, and then drawtext
/// fails with an error that looks like a bug in the encoder. Second -- worse --
/// picking whatever Latin font comes first succeeds and produces a watermark of
/// empty boxes when the text is Chinese, so the encode reports success and the
/// output is wrong.
///
/// So the user puts fonts in the directory and the program picks a CJK-capable
/// one. On Windows the directory is seeded from the system fonts the first time
/// it is empty, because those are the fonts people mean; on Linux you copy one
/// across. From then on both platforms use their own copy.
/// </summary>
public static class FontResolver
{
    /// <summary>Environment override, for CI boxes and scripted runs.</summary>
    public const string EnvironmentVariable = "LAE_WATERMARK_FONT";

    /// <summary>Directory name next to the managed <c>ffmpeg</c> directory.</summary>
    public const string DirectoryName = "fonts";

    /// <summary>Where the bundled fonts live.</summary>
    public static string FontDirectory
    {
        get
        {
            // Same rule as Toolchain.Root: fall back to the executable's own
            // directory when there is no user profile (service accounts, CI).
            string? appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = string.IsNullOrEmpty(appData)
                ? AppContext.BaseDirectory
                : Path.Combine(appData, "LiveArchiveEncoder");
            return Path.Combine(dir, DirectoryName);
        }
    }

    /// <summary>
    /// First usable font, or null when the user has not put one there yet.
    /// </summary>
    public static string? Find()
    {
        string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        string dir = FontDirectory;
        string? found = PickFrom(dir);
        if (found is not null) return found;

        // First run on a Windows box: the directory is empty and the user has no
        // reason to know it exists. Seed it once from the system fonts, then keep
        // using that copy -- after this the two platforms behave the same way and
        // neither depends on what the OS happens to have installed.
        if (SeedFromSystemFonts(dir)) return PickFrom(dir);

        return null;
    }

    /// <summary>File name fragments that mean "this font can draw CJK".</summary>
    private static readonly string[] CjkMarkers =
        ["cjk", "wqy", "zenhei", "microhei", "droidsansfallback", "arphic",
         "uming", "ukai", "notosanssc", "notosanstc",
         // Chinese Windows fonts, by the names they are usually installed as.
         "msyh", "simhei", "simsun", "simkai", "simfang", "yahei", "heiti",
         "songti", "kaiti", "fangsong"];

    /// <summary>Plain Latin sans-serif fallbacks, in preference order.</summary>
    private static readonly string[] SansMarkers =
        ["dejavu", "liberation", "notosans", "freesans", "ubuntu", "roboto", "cantarell"];

    /// <summary>
    /// CJK first, then a known sans-serif family, then anything. Order matters:
    /// "the first file found" would happily return Liberation Mono, and a Chinese
    /// watermark would render as boxes while the encode still reported success.
    /// </summary>
    public static string? PickFrom(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
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

    private static bool IsFontFile(string path) =>
        path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string path, string[] markers) =>
        markers.Any(m => Path.GetFileName(path).Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Copy the Chinese Windows fonts into the bundled directory. False on
    /// anything that is not Windows, and on Windows when nothing could be copied
    /// -- the caller then tells the user where to put one.
    /// </summary>
    private static bool SeedFromSystemFonts(string target)
    {
        if (!OperatingSystem.IsWindows()) return false;

        string system = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (system.Length == 0) system = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        if (!Directory.Exists(system)) return false;

        // Microsoft YaHei, SimHei, SimSun -- the three that get asked for.
        string[] names = ["msyh.ttc", "simhei.ttf", "simsun.ttc"];

        int copied = 0;
        try
        {
            Directory.CreateDirectory(target);
            foreach (string name in names)
            {
                string source = Path.Combine(system, name);
                if (!File.Exists(source)) continue;

                string destination = Path.Combine(target, name);
                if (File.Exists(destination)) { copied++; continue; }

                File.Copy(source, destination, overwrite: true);
                copied++;
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // Read-only or missing just means "no font yet"; the caller turns
            // that into a message rather than a crash.
        }
        return copied > 0;
    }

    /// <summary>
    /// What to tell someone whose <c>fonts</c> directory is empty. Naming the
    /// directory is the important part -- "no usable font found" on its own sends
    /// people looking for a bug in the encoder.
    /// </summary>
    public static string InstallHint =>
        $"put a .ttf/.otf/.ttc file in {FontDirectory} "
        + "(or pass --watermark-font, or set "
        + (OperatingSystem.IsWindows() ? "%" : "$")
        + $"{EnvironmentVariable}). "
        + (OperatingSystem.IsWindows()
            ? "Microsoft YaHei is copied there automatically when the directory is empty."
            : "On Linux copy one across -- from Windows (msyh.ttc / simhei.ttf), or from "
              + "the distro package (Debian/Ubuntu: sudo apt install fonts-noto-cjk; "
              + "Fedora/RHEL: sudo dnf install google-noto-sans-cjk-fonts).");

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
