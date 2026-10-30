using Lae.Core;
using Lae.Encoding;
using Lae.Media.Ffmpeg;
using Lae.Media.Toolchain;

namespace Lae.App;

/// <summary>The one thing the session asks of a runner: run this plan.</summary>
public interface IEncodeRunner
{
    LiveRun Run(
        EncodePlan plan, string ffmpeg,
        Action<long, long?>? progress, Action<string>? warn, CancellationToken token);
}

/// <summary>The real runner: two FFmpeg children and a paced pipe between them.</summary>
public sealed class LiveEncodeRunner : IEncodeRunner
{
    public LiveRun Run(
        EncodePlan plan, string ffmpeg,
        Action<long, long?>? progress, Action<string>? warn, CancellationToken token)
        => LiveEncode.Run(plan, ffmpeg, progress, warn, token);
}

/// <summary>
/// The outside world, as the encode session sees it. Separated out so the
/// session can be driven by a test with no FFmpeg, no PATH and no child
/// processes: the argument mapping, the exit codes and the wording are what
/// break, and none of them need a video to check.
/// </summary>
public sealed record EncodeDependencies
{
    public required Func<string?, ToolchainProbeResult> ProbeToolchain { get; init; }

    public required Func<string, string, (SourceInfo? Info, string? Error)> ProbeSource { get; init; }

    public required Func<string, (List<EncoderInfo> Encoders, string? Error)> ListEncoders { get; init; }

    /// <summary>
    /// Unwraps one embedded subtitle track into a file, or explains why it could
    /// not. A delegate because the session is tested without FFmpeg and the
    /// interesting part -- refusing a bitmap track, deleting the file afterwards --
    /// is on this side of the call.
    /// </summary>
    public required Func<string, string, int, string, string?> ExtractSubtitle { get; init; }

    public required IEncodeRunner Runner { get; init; }

    /// <summary>
    /// Runs the post-encode chain. A delegate rather than a call to
    /// <see cref="RecompressRun"/> because the session is exercised without
    /// FFmpeg, and the exit-code policy around a failed hop is exactly what needs
    /// a test.
    /// </summary>
    public required Func<RecompressPlan, string, TimeSpan, Action<string>?, CancellationToken, RecompressOutcome>
        RunRecompress { get; init; }

    public static EncodeDependencies Default { get; } = new()
    {
        ProbeToolchain = ToolchainProbe.Probe,
        ProbeSource = (ffprobe, path) => FfmpegTools.ProbeSource(ffprobe, path),
        ListEncoders = ffmpeg => FfmpegTools.ListEncoders(ffmpeg),
        ExtractSubtitle = (ffmpeg, source, index, destination)
            => FfmpegTools.ExtractSubtitle(ffmpeg, source, index, destination),
        Runner = new LiveEncodeRunner(),
        RunRecompress = RecompressRun.Run,
    };
}
