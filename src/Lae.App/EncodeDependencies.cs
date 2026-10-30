using Lae.Core;
using Lae.Core.History;
using Lae.Core.Localization;
using Lae.Encoding;
using Lae.Media.Ffmpeg;
using Lae.Media.Localization;
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

    /// <summary>
    /// Where a finished run is remembered. A no-op by default so a test that does
    /// not care about history never creates a database -- and neither does a dry
    /// run, which promises to write no files at all.
    /// </summary>
    public IRunHistory History { get; init; } = NullRunHistory.Instance;

    /// <summary>
    /// The language this run speaks.
    ///
    /// <para>Here rather than as another parameter of <see cref="EncodeSession.Run"/>
    /// because it is the same kind of thing as the rest of this record: something
    /// the host supplies and the session only calls. <c>required</c> on purpose --
    /// a default would let one forgotten wiring step print key names at a user,
    /// which is the one failure mode of a keyed resource file that nobody notices
    /// in a test.</para>
    /// </summary>
    public required IMessages Messages { get; init; }

    public static EncodeDependencies Default { get; } = new()
    {
        ProbeToolchain = ToolchainProbe.Probe,
        ProbeSource = (ffprobe, path) => FfmpegTools.ProbeSource(ffprobe, path),
        ListEncoders = ffmpeg => FfmpegTools.ListEncoders(ffmpeg),
        ExtractSubtitle = (ffmpeg, source, index, destination)
            => FfmpegTools.ExtractSubtitle(ffmpeg, source, index, destination),
        Runner = new LiveEncodeRunner(),
        RunRecompress = RecompressRun.Run,
        Messages = I18NextMessages.Create(LanguageChoice.English),
    };
}
