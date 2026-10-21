// ============================================================================
// RetiredFontPicking.cs -- archived 2026-10-21 (v7.9.1)
// ============================================================================
//
// WHAT THIS WAS
//   The drawtext-era half of FontResolver: automatic CJK-capable font picking
//   (Find / PickFrom / SeedFromSystemFonts), the drawtext filter-path escaper
//   (ToFilterPath) and the "no usable font" install hint (InstallHint).
//
// WHY IT WAS RETIRED
//   v7.9.1 removed the static text watermark feature (see RetiredWatermark.cs).
//   The watermark was the only consumer of these members: SubtitleTrack.cs
//   (burn mode) uses libass via the subtitles filter, which resolves fonts
//   from `fontsdir=<FontResolver.FontDirectory>` and needs no picked file.
//   With the watermark gone these members had no callers left in compiled
//   code, and the project convention is to archive zero-reference code rather
//   than carry it.
//
// HOW TO GET IT BACK
//   Un-archive: move the members below back into
//   src/LiveArchiveEncoder/PostProcess/FontResolver.cs. They depend on the
//   members still declared there (DirectoryName, FontDirectory,
//   DirectoryEnvironmentVariable, LegacyDirectoryEnvironmentVariable) plus the
//   two file-level env var names copied to the top of this class, and restore
//   their tests (they used to live in FontResolverTests). The retired watermark
//   code in RetiredWatermark.cs calls Find / Resolve / ToFilterPath /
//   InstallHint, so reviving the watermark means reviving this file with it.
//
//   If a future subtitle feature needs a picked font (e.g. warning when the
//   fonts directory is empty, or a --subtitle-font option), take PickFrom and
//   the marker tables -- the CJK-first ordering is the part worth keeping,
//   and it is covered by the reasoning in the comments below.
//
//   NOTE: not compiled. archive/ is excluded from every csproj, so this file
//   is reference material only -- it is not kept green by the build.
// ============================================================================

using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>Archived copy of the drawtext-era FontResolver members.</summary>
public static class RetiredFontPicking
{
    /// <summary>File-level env override names, formerly FontResolver members.</summary>
    public const string EnvironmentVariable = "LAE_FONT";

    /// <summary>Pre-retirement name of <see cref="EnvironmentVariable"/>.</summary>
    public const string LegacyEnvironmentVariable = "LAE_WATERMARK_FONT";

    /// <summary>
    /// Resolve a font the user named to a path libass (or drawtext) can open.
    ///
    /// v7.9: this exists because of a real bug report. Typing a bare file name
    /// -- a font sitting in the bundled <c>fonts</c> directory, named without a
    /// path -- used to be handed to FFmpeg verbatim, and FFmpeg resolves
    /// relative paths against <em>its</em> working directory, not against the
    /// fonts directory this program documents. The encode then failed with "no
    /// such file or directory" even though the font was sitting right there,
    /// and the only way to make it work was the absolute path -- which nobody
    /// should have to type.
    ///
    /// Accepted forms, in order:
    ///   1. a path that exists as typed (absolute, or relative to the working
    ///      directory -- ordinary path semantics, so an explicit path always
    ///      wins);
    ///   2. a bare file name looked up in FontResolver.FontDirectory, which is
    ///      what the docs tell people to populate;
    ///   3. anything else comes back unchanged, so the caller's error message
    ///      still shows exactly what was asked for.
    /// </summary>
    public static string Resolve(string? font)
    {
        if (string.IsNullOrWhiteSpace(font)) return "";
        if (File.Exists(font)) return Path.GetFullPath(font);

        if (!Path.IsPathRooted(font))
        {
            string candidate = Path.Combine(FontResolver.FontDirectory, font);
            if (File.Exists(candidate)) return candidate;
        }

        return font;
    }

    /// <summary>
    /// First usable font, or null when the user has not put one there yet.
    /// </summary>
    public static string? Find()
    {
        string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!env.HasText) env = Environment.GetEnvironmentVariable(LegacyEnvironmentVariable);
        if (env.HasText && File.Exists(env)) return env;

        string dir = FontResolver.FontDirectory;
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
    [
        "cjk", "wqy", "zenhei", "microhei", "droidsansfallback", "arphic",
        "uming", "ukai", "notosanssc", "notosanstc",
        // Chinese Windows fonts, by the names they are usually installed as.
        "msyh", "simhei", "simsun", "simkai", "simfang", "yahei", "heiti",
        "songti", "kaiti", "fangsong",
    ];

    /// <summary>Plain Latin sans-serif fallbacks, in preference order.</summary>
    private static readonly string[] SansMarkers =
        ["dejavu", "liberation", "notosans", "freesans", "ubuntu", "roboto", "cantarell"];

    /// <summary>
    /// CJK first, then a known sans-serif family, then anything. Order matters:
    /// "the first file found" would happily return Liberation Mono, and Chinese
    /// text would render as boxes while the encode still reported success.
    /// </summary>
    public static string? PickFrom(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        List<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(IsFontFile)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
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
        $"put a .ttf/.otf/.ttc file in {FontResolver.FontDirectory} "
        + "(or set "
        + (OperatingSystem.IsWindows() ? "%" : "$")
        + $"{FontResolver.EnvironmentVariable}). "
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
    ///
    /// Note: SubtitleTrack.EscapeFilterPath does the same job for the subtitles
    /// filter with single quoting added (measured 2026-10-21: libass needs the
    /// path quoted, drawtext did not).
    /// </summary>
    public static string ToFilterPath(string path)
    {
        string normalised = path.Replace('\\', '/');
        return PostArgs.EscapeFilterValue(normalised);
    }
}
