using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>
/// Where the fonts libass (the <c>subtitles</c> filter) looks.
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
/// container image often has no <c>/usr/share/fonts</c> at all, and then the
/// subtitle filter fails with an error that looks like a bug in the encoder.
/// Second -- worse -- an ASS file that asks for "Microsoft YaHei" by name
/// resolves to whatever Latin font comes first when the real one is missing,
/// so the encode reports success and the subtitles render as empty boxes.
///
/// So the user puts fonts in the directory and libass finds them through
/// <c>fontsdir</c>. On Windows the directory can be seeded from the system
/// fonts by hand (msyh.ttc / simhei.ttf); on Linux you copy one across. From
/// then on both platforms use their own copy and neither depends on what the
/// OS happens to have installed.
/// </summary>
public static class FontResolver
{
    /// <summary>
    /// Environment override for the directory. Useful where the fonts live
    /// somewhere a service account cannot see, and it is what lets the unit
    /// tests exercise the lookup without touching the real profile directory.
    /// </summary>
    public const string DirectoryEnvironmentVariable = "LAE_FONT_DIR";

    /// <summary>
    /// v7.9.1: the pre-retirement name, still honoured so a setup written for
    /// the watermark era keeps working.
    /// </summary>
    public const string LegacyDirectoryEnvironmentVariable = "LAE_WATERMARK_FONT_DIR";

    /// <summary>Directory name next to the managed <c>ffmpeg</c> directory.</summary>
    public const string DirectoryName = "fonts";

    /// <summary>Where the bundled fonts live.</summary>
    public static string FontDirectory
    {
        get
        {
            string? overrideDir = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
            if (!overrideDir.HasText)
                overrideDir = Environment.GetEnvironmentVariable(LegacyDirectoryEnvironmentVariable);
            if (overrideDir.HasText) return overrideDir;

            // Same rule as Toolchain.Root: fall back to the executable's own
            // directory when there is no user profile (service accounts, CI).
            string? appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = string.IsNullOrEmpty(appData)
                ? AppContext.BaseDirectory
                : Path.Combine(appData, "LiveArchiveEncoder");
            return Path.Combine(dir, DirectoryName);
        }
    }
}
