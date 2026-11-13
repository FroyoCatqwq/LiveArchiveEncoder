using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Localization;

namespace LiveArchiveEncoder.Core;

public enum DiscImageKind
{
    /// <summary>Not a disc image at all -- an ordinary input file.</summary>
    None,
    /// <summary>DVD-Video: VIDEO_TS/ with IFO and VOB files. FFmpeg reads it directly.</summary>
    DvdVideo,
    /// <summary>Video CD: MPEGAV/*.DAT. Extracted to a staging directory first.</summary>
    Vcd,
}

/// <summary>
/// A disc image resolved to something FFmpeg can open. DVD-Video is handed over
/// as the ISO itself because FFmpeg's <c>dvdvideo</c> demuxer reads MicroUDF,
/// follows the IFO and stitches VTS_zz_1..9.VOB together; VCD has no such
/// demuxer, so its <c>MPEGAV/*.DAT</c> payload is unpacked into a temporary
/// directory that lives as long as this object.
/// </summary>
public sealed class DiscImage : IDisposable
{
    public DiscImageKind Kind { get; init; }
    public string MediaPath { get; init; } = "";
    public string? StageDirectory { get; init; }

    public bool IsImage => Kind != DiscImageKind.None;

    public string Describe => Kind switch
    {
        DiscImageKind.DvdVideo => L.T("disc.kind.dvd"),
        DiscImageKind.Vcd => L.T("disc.kind.vcd"),
        _ => "",
    };

    public void Dispose()
    {
        if (StageDirectory is null) return;
        try { System.IO.Directory.Delete(StageDirectory, recursive: true); }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a finished encode.
        }
    }
}

/// <summary>
/// Recognises the two disc image layouts this tool supports and refuses the
/// rest. The rule is structural, not "does ffprobe open it": a data DVD or a
/// Blu-ray ISO may contain playable files yet is not a DVD-Video or VCD disc,
/// and silently encoding whatever is inside would misrepresent it.
/// </summary>
public static class DiscImages
{
    private static readonly FrozenSet<string> Extensions =
        new[] { ".iso", ".img" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool LooksLikeImage(string? path) =>
        !string.IsNullOrEmpty(path) && Extensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Returns (null, null) when the path is not a disc image, (image, null)
    /// when it is a supported one, and (null, error) when it is an image this
    /// tool will not touch.
    /// </summary>
    public static (DiscImage? Image, string? Error) Resolve(string path)
    {
        if (!LooksLikeImage(path)) return (null, null);
        try
        {
            return ResolveCore(path);
        }
        // The contract is "an image, or a reason". A path the OS refuses (too
        // long, malformed, a reserved device name) used to escape from
        // Path.GetFullPath and crash the CLI with a stack trace, because only
        // the VCD branch was guarded.
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException)
        {
            return (null, L.T("disc.unreadable", Path.GetFileName(path)));
        }
    }

    private static (DiscImage? Image, string? Error) ResolveCore(string path)
    {
        string? sevenZip = SevenZip.Find();
        if (sevenZip is null) return (null, L.T("disc.no7zip"));

        (int code, string listing) = Run(sevenZip, ["l", "-ba", "--", path]);
        if (code != 0)
        {
            // Say WHY. A blocked 7z.exe, a locked image and a timed-out share
            // are three different problems; the old code collapsed them into
            // one sentence and threw the only diagnostic away.
            return (null, $"{L.T("disc.unreadable", Path.GetFileName(path))} "
                + $"[7z {code}] {Tail(listing)}");
        }

        string upper = listing.ToUpperInvariant();
        bool dvd = upper.Contains("VIDEO_TS") && upper.Contains(".IFO");
        bool vcd = upper.Contains("MPEGAV") && upper.Contains(".DAT");

        if (dvd)
        {
            // FFmpeg's dvdvideo demuxer handles UDF + IFO + VOB chaining itself.
            return (new DiscImage
            {
                Kind = DiscImageKind.DvdVideo,
                MediaPath = Path.GetFullPath(path),
            }, null);
        }

        if (!vcd) return (null, L.T("disc.notsupported", Path.GetFileName(path)));

        string stage = Path.Combine(Path.GetTempPath(),
            "lae_disc_" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(stage);
            (int extractCode, string extractLog) =
                Run(sevenZip, ["e", "-y", "-o" + stage, "--", path, "MPEGAV/*.DAT"]);
            // 7-Zip answers 1 for "done, with warnings" and >=2 for a real
            // failure. The old code only asked whether *a* file appeared, so a
            // partial extraction was encoded as if it were complete.
            if (extractCode >= 2)
            {
                TryDelete(stage);
                return (null, L.T("disc.extractfailed", Path.GetFileName(path),
                    $"7z {extractCode}: {Tail(extractLog)}"));
            }
            string[] payload = System.IO.Directory.GetFiles(stage, "*",
                SearchOption.TopDirectoryOnly);
            Array.Sort(payload, StringComparer.OrdinalIgnoreCase);
            string? first = payload.FirstOrDefault(p =>
                p.EndsWith(".dat", StringComparison.OrdinalIgnoreCase));
            if (first is null)
            {
                TryDelete(stage);
                return (null, L.T("disc.extractfailed",
                    Path.GetFileName(path), Tail(extractLog)));
            }
            return (new DiscImage
            {
                Kind = DiscImageKind.Vcd,
                MediaPath = first,
                StageDirectory = stage,
            }, null);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException)
        {
            TryDelete(stage);
            return (null, L.T("disc.extractfailed", Path.GetFileName(path), exc.Message));
        }
    }

    private static void TryDelete(string directory)
    {
        try { System.IO.Directory.Delete(directory, recursive: true); }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { }
    }

    private static string Tail(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[^300..];
    }

    /// <summary>
    /// 7-Zip over an ISO is fast (it only reads the directory records here), but
    /// a damaged or network image can hang, so the wait is bounded.
    /// </summary>
    private static (int Code, string Output) Run(string exe, string[] args)
    {
        var info = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) info.ArgumentList.Add(a);

        try
        {
            using Process? proc = Process.Start(info);
            if (proc is null) return (-1, "could not start 7-Zip");
            Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errTask = proc.StandardError.ReadToEndAsync();
            // Poll instead of one long wait: a damaged image on a share can
            // hang for the whole timeout, and Ctrl+C has to be able to stop it.
            CancellationToken token = Core.Interrupt.CurrentToken;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!proc.WaitForExit(250))
            {
                if (token.IsCancellationRequested || clock.Elapsed > TimeSpan.FromSeconds(180))
                {
                    try { proc.Kill(entireProcessTree: true); } catch (Exception) { /* gone */ }
                    return (-1, token.IsCancellationRequested
                        ? "cancelled"
                        : "7-Zip timed out after 180 s");
                }
            }
            string text = outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult();
            return (proc.ExitCode, text);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or System.ComponentModel.Win32Exception)
        {
            return (-1, exc.Message);
        }
    }
}
