using System.IO.Compression;
using System.Text;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>
/// v7.9.8.13: one font pack (<c>.zip</c> / <c>.7z</c> / <c>.rar</c>) into the
/// program's own fonts directory. 7-Zip is the extractor because it is the one
/// tool that reads all three, including Rar5; the FFmpeg installer already uses
/// it for the same reason.
///
/// <para>The fonts are <em>copied</em> into <see cref="FontResolver.FontDirectory"/>,
/// which is the directory libass is pointed at. Nothing here changes the user's
/// settings file -- a font pack is a per-run command line choice.</para>
/// </summary>
public static class FontPack
{
    public static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar"];

    /// <summary>
    /// v7.9.8.13.1: kept as a property so callers/tests still have a Windows
    /// install URL. The authoritative per-platform asset list is
    /// <see cref="SevenZip"/>; both platforms now use ip7z/7zip releases.
    /// </summary>
    public static string InstallerUrl => SevenZip.UrlFor(DistPlatform.Windows);

    public static bool IsArchive(string path) =>
        ArchiveExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Where a 7-Zip we installed ourselves lives (user-writable).</summary>
    public static string Portable7ZipDir => SevenZip.DirectoryPath;

    /// <summary>A 7-Zip already present, including one installed by an earlier run.</summary>
    public static string? ExistingSevenZip() => SevenZip.Find();

    /// <summary>
    /// Find 7-Zip, or fetch the official build for this platform. The lookup,
    /// download and unpack now live in <see cref="SevenZip"/>, so the FFmpeg
    /// installer and the font pack share one implementation on Windows AND Linux.
    /// </summary>
    public static (string? Exe, string? Error) EnsureSevenZip(
        string? ghProxy = null, Action<string>? log = null) =>
        SevenZip.Ensure(ghProxy, null, log);

    /// <summary>Staging directory plus the fonts found in it. Dispose removes it.</summary>
    public sealed class Unpacked : IDisposable
    {
        public required string Directory { get; init; }
        public required IReadOnlyList<string> Fonts { get; init; }
        public string? Error { get; init; }

        public void Dispose() => TryDelete(Directory);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
        }
        catch (Exception)
        {
            // Best effort: a cleanup failure must never mask the real error.
        }
    }

    /// <summary>
    /// Unpack with 7-Zip. A <c>.zip</c> falls back to the built-in reader when
    /// 7-Zip cannot be installed (Linux without p7zip), so the common case still
    /// works; <c>.7z</c> and <c>.rar</c> genuinely need the external tool.
    /// </summary>
    public static Unpacked Unpack(string archive, string? ghProxy = null, Action<string>? log = null)
    {
        string temp = Path.Combine(Path.GetTempPath(), "lae-fontpack-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            if (!File.Exists(archive)) return Fail($"font pack not found: {archive}");
            if (!IsArchive(archive)) return Fail($"unsupported font pack (use .zip, .7z or .rar): {archive}");

            (string? sevenZip, string? zipError) = EnsureSevenZip(ghProxy, log);
            if (sevenZip is not null)
            {
                log?.Invoke($"Unpacking {Path.GetFileName(archive)} with 7-Zip...");
                CaptureResult run = ProcessRunner.Run(
                    [sevenZip, "x", "-y", "-bso0", "-bsp0", $"-o{temp}", archive], 600);
                if (run.ExitCode != 0)
                    return Fail($"7-Zip exited with {run.ExitCode}: {(run.Stderr + run.Stdout).Trim()}");
            }
            else if (Path.GetExtension(archive).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke("7-Zip unavailable; reading the .zip with the built-in reader.");
                ZipFile.ExtractToDirectory(archive, temp, overwriteFiles: true);
            }
            else return Fail(zipError ?? "7-Zip is required");

            // A hostile archive may contain ../ paths. Only accept files that
            // really landed under the staging directory.
            string root = Path.GetFullPath(temp);
            List<string> fonts = [.. Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories)
                .Where(f => SubtitleFonts.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Where(f => Path.GetFullPath(f).StartsWith(
                    root + Path.DirectorySeparatorChar, StringComparison.Ordinal))];
            return new Unpacked { Directory = temp, Fonts = fonts };
        }
        catch (Exception exc)
        {
            // v7.9.8.13.2: ProcessRunner may throw TimeoutException /
            // InvalidOperationException / Win32Exception and SevenZip.Ensure
            // may fail downloading. When that happens no Unpacked is created,
            // so the caller's using cannot clean up -- do it here.
            TryDelete(temp);
            return Fail(exc.Message);
        }

        Unpacked Fail(string message) =>
            new() { Directory = temp, Fonts = [], Error = message };
    }

    /// <summary>
    /// Letters and digits only, lower-cased: <c>FOT-Matisse ProN B</c> (an ASS
    /// style) and <c>FOT-MatisseProN-B.otf</c> (a packed file) both become
    /// <c>fotmatissepronb</c>. That is how real release font packs are named.
    /// </summary>
    public static string Normalize(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Fonts from the pack that an ASS/SSA style actually asks for.</summary>
    public static List<string> Recommended(
        IEnumerable<string> fontFiles, IEnumerable<string> requiredFamilies)
    {
        List<string> wanted = [.. requiredFamilies
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(Normalize)
            .Where(n => n.Length >= 3)
            .Distinct(StringComparer.Ordinal)];
        if (wanted.Count == 0) return [];

        var picked = new List<string>();
        foreach (string file in fontFiles)
        {
            string key = Normalize(Path.GetFileNameWithoutExtension(file));
            if (key.Length < 3) continue;
            if (wanted.Any(w => w == key || key.Contains(w, StringComparison.Ordinal)
                                     || w.Contains(key, StringComparison.Ordinal)))
                picked.Add(file);
        }
        return picked;
    }

    /// <summary>Copy the chosen fonts into the directory libass reads.</summary>
    public static (int Copied, int Skipped, string Directory) Install(IEnumerable<string> files) =>
        SubtitleFonts.Import(files);
}
