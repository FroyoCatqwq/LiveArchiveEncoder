using System.Diagnostics;
using System.Globalization;
using Lae.Core;

namespace Lae.Media.Ffmpeg;

/// <summary>
/// The two FFmpeg queries v8 needs so far: what is in this source, and which
/// encoders does this build have.
///
/// <para>Thin on purpose. The parsing lives in <see cref="FfprobeJson"/> and
/// <see cref="EncoderCatalog"/> inside <c>Lae.Core</c>, where it can be tested
/// against literals; this file only starts a process, enforces a deadline and
/// hands the text over. v7 mixed the two, so a parsing bug could only be found
/// on a machine with that FFmpeg build and a file with that shape.</para>
/// </summary>
public static class FfmpegTools
{
    /// <summary>
    /// Probe deadline. The 30-second default is not enough for the sources this
    /// tool meets: a 179 MB VOB on a share took 2m15s to probe (measured
    /// 2026-10-21), because MPEG-PS carries no index and the duration only falls
    /// out of reading the whole programme stream.
    /// </summary>
    public const int ProbeTimeoutSeconds = 300;

    /// <summary>Listing encoders is a banner and a few hundred lines.</summary>
    public const int ListTimeoutSeconds = 60;

    /// <summary>What the source is made of. Null plus a reason when it is not readable.</summary>
    public static (SourceInfo? Info, string? Error) ProbeSource(
        string ffprobe, string path, CancellationToken cancellationToken = default)
    {
        (string output, string error, int exitCode) = Run(
            ffprobe,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", path],
            ProbeTimeoutSeconds, cancellationToken);

        if (exitCode != 0)
        {
            return (null, error.Trim() is { Length: > 0 } reason ? reason : "ffprobe could not read the source");
        }

        SourceInfo? info = FfprobeJson.TryParse(output, out string? parseError);
        return (info, parseError);
    }

    /// <summary>
    /// The video encoders this FFmpeg build offers.
    ///
    /// <para>An empty list with an error is not "no encoders": it means the query
    /// itself failed, and a caller that treats the two the same would tell the
    /// user their FFmpeg is useless when it is merely unreachable.</para>
    /// </summary>
    public static (List<EncoderInfo> Encoders, string? Error) ListEncoders(
        string ffmpeg, CancellationToken cancellationToken = default)
    {
        (string output, string error, int exitCode) = Run(
            ffmpeg, ["-hide_banner", "-encoders"], ListTimeoutSeconds, cancellationToken);

        if (exitCode != 0)
        {
            return ([], error.Trim() is { Length: > 0 } reason ? reason : "ffmpeg could not list its encoders");
        }

        List<EncoderInfo> encoders = EncoderCatalog.Parse(output);
        return encoders.Count > 0
            ? (encoders, null)
            : ([], "ffmpeg reported no video encoders");
    }

    /// <summary>
    /// Runs a tool to completion and returns its streams.
    ///
    /// <para>Both pipes are drained concurrently with the wait. Reading one and
    /// then the other deadlocks as soon as the tool fills the second buffer,
    /// which for <c>ffmpeg -encoders</c> is a real possibility on a build with
    /// many encoders, and for ffprobe on a source with many streams.</para>
    /// </summary>
    private static (string Output, string Error, int ExitCode) Run(
        string executable, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string arg in args) start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        if (!process.Start()) return ("", $"cannot start {executable}", -1);

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            catch (System.ComponentModel.Win32Exception) { /* already gone */ }

            string why = cancellationToken.IsCancellationRequested
                ? "cancelled"
                : string.Create(CultureInfo.InvariantCulture, $"did not answer within {timeoutSeconds}s");
            return ("", $"{Path.GetFileName(executable)} {why}", -1);
        }

        // A completed process has closed both pipes, so these finish; the wait is
        // only a guard against a grandchild holding one open.
        Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(5));

        return (stdout.IsCompletedSuccessfully ? stdout.Result : "",
                stderr.IsCompletedSuccessfully ? stderr.Result : "",
                process.ExitCode);
    }
}
