using Lae.App.Ui;
using Lae.Core;
using Lae.Core.Settings;
using Lae.Encoding;
using Lae.Media.Ffmpeg;
using Lae.Media.Toolchain;

namespace Lae.App;

/// <summary>What the command line asked for, already parsed and free of System.CommandLine.</summary>
public sealed record EncodeOverrides
{
    public string? Output { get; init; }

    public string? Encoder { get; init; }

    public string? Container { get; init; }

    public double? From { get; init; }

    public double? To { get; init; }

    public int? Quality { get; init; }

    /// <summary>WIDTHxHEIGHT as typed.</summary>
    public string? Size { get; init; }
}

/// <summary>
/// The four things <c>encode</c> asks of the outside world. Separated out so the
/// session can be driven by a test with no FFmpeg, no PATH and no child
/// processes: the argument mapping, the exit codes and the wording are what
/// break, and none of them need a video to check.
/// </summary>
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

/// <summary>The outside world, as the encode session sees it.</summary>
public sealed record EncodeDependencies
{
    public required Func<string?, ToolchainProbeResult> ProbeToolchain { get; init; }

    public required Func<string, string, (SourceInfo? Info, string? Error)> ProbeSource { get; init; }

    public required Func<string, (List<EncoderInfo> Encoders, string? Error)> ListEncoders { get; init; }

    public required IEncodeRunner Runner { get; init; }

    public static EncodeDependencies Default { get; } = new()
    {
        ProbeToolchain = ToolchainProbe.Probe,
        ProbeSource = (ffprobe, path) => FfmpegTools.ProbeSource(ffprobe, path),
        ListEncoders = ffmpeg => FfmpegTools.ListEncoders(ffmpeg),
        Runner = new LiveEncodeRunner(),
    };
}

/// <summary>
/// One <c>lae encode</c>: toolchain, source, plan, run.
///
/// <para>Split out of the command because the command was doing six things at
/// once and only the first of them is about the command line. What is left in
/// <c>EncodeCommand</c> is the shape of the arguments; every decision, every
/// message and every exit code lives here, where a test can reach it.</para>
///
/// <para>Exit codes are the contract: 0 done, 2 the request cannot be honoured
/// as written (bad size, unknown encoder, an unwired feature), 1 this machine or
/// this source could not do it. A user scripting the tool needs those three to
/// mean different things, so they are decided in one place and tested.</para>
/// </summary>
public static class EncodeSession
{
    public static int Run(
        string input,
        EncodeOverrides overrides,
        SettingsDocument settings,
        EncodeDependencies dependencies,
        ITerminal terminal,
        bool dryRun,
        CancellationToken token)
    {
        // The toolchain comes first: "no FFmpeg" is a setup problem with its own
        // command, not an encode failure, and it must not be reported as one.
        ToolchainProbeResult tools = dependencies.ProbeToolchain(ToolchainDirectory(settings));
        if (tools.Ffmpeg is null || tools.Ffprobe is null)
        {
            foreach (string line in tools.Missing()) terminal.ErrorLine($"ERROR: missing {line}");
            terminal.ErrorLine("Run 'lae setup' for what to do about it.");
            return ExitCodes.Failed;
        }

        (SourceInfo? source, string? probeError) = dependencies.ProbeSource(tools.Ffprobe, input);
        if (source is null)
        {
            terminal.ErrorLine($"ERROR: cannot read '{input}': {probeError}");
            return ExitCodes.Failed;
        }

        (EncodeRequest? request, string? requestError) = BuildRequest(input, overrides, settings);
        if (request is null)
        {
            terminal.ErrorLine($"ERROR: {requestError}");
            return ExitCodes.Usage;
        }

        (List<EncoderInfo> encoders, string? listError) = dependencies.ListEncoders(tools.Ffmpeg);
        if (listError is not null)
        {
            terminal.ErrorLine($"ERROR: {listError}");
            return ExitCodes.Failed;
        }

        EncoderInfo? encoder = EncoderCatalog.Find(encoders, request.Encode.Encoder);
        if (encoder is null)
        {
            terminal.ErrorLine($"ERROR: this FFmpeg build has no encoder named '{request.Encode.Encoder}'.");
            terminal.ErrorLine("Try one of: mpeg4, mpeg1video, flv, libx264.");
            return ExitCodes.Usage;
        }

        EncodePlan plan = EncodePlan.Create(request, source, encoder);

        // Notes are commentary, not the answer, so they go to stderr: a dry run
        // piped to a file has to contain the two command lines and nothing else.
        foreach (string note in plan.Notes) terminal.ErrorLine($"note: {note}");

        if (!plan.IsRunnable)
        {
            foreach (string problem in plan.Problems) terminal.ErrorLine($"ERROR: {problem}");
            return ExitCodes.Usage;
        }

        PrintPlan(plan, source, tools.Ffmpeg, terminal);
        if (dryRun) return ExitCodes.Ok;

        using var progress = new LiveProgress(terminal);
        LiveRun run = dependencies.Runner.Run(
            plan, tools.Ffmpeg,
            (done, total) => progress.Report(done, total),
            note => terminal.ErrorLine($"note: {note}"),
            token);

        progress.Finish(
            $"[{run.Pump.Seconds,7:F1}s] {run.Pump.Frames} frames written"
            + (run.Pump.Repeated > 0 ? $" ({run.Pump.Repeated} repeated)" : ""));

        PrintResult(run, terminal);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Lays the command line over the stored defaults.
    ///
    /// <para>Only the fields this command actually spells are touched, and the
    /// result is a <c>with</c> expression, so a field the command does not know
    /// about survives untouched. Two things are deliberately <em>not</em> taken
    /// from the settings: the recompress chain and the subtitle block. A CLI run
    /// re-encodes its own output or burns subtitles only when it says so on the
    /// command line -- that rule is older than the settings file and it is what
    /// keeps a scripted run predictable.</para>
    /// </summary>
    public static (EncodeRequest? Request, string? Error) BuildRequest(
        string input, EncodeOverrides overrides, SettingsDocument settings)
    {
        EncodeDefaults encode = settings.Defaults with
        {
            Encoder = overrides.Encoder ?? settings.Defaults.Encoder ?? "mpeg4",
            Recompress = new RecompressChain(),
            Subtitles = new SubtitleDefaults(),
        };

        if (overrides.Container is { } container) encode = encode with { Container = container };
        if (overrides.Quality is { } quality) encode = encode with { Quality = quality };

        if (overrides.Size is { } size)
        {
            if (!TryParseSize(size, out int width, out int height))
                return (null, $"--size wants WIDTHxHEIGHT, got '{size}'");

            encode = encode with { Width = width, Height = height };
        }

        string destination = overrides.Output ?? DeriveDestination(input, encode.Container ?? "avi");

        var request = new EncodeRequest
        {
            SourcePath = input,
            DestinationPath = destination,
            Encode = encode,
            StartSeconds = overrides.From ?? 0,
            EndSeconds = overrides.To,
        };

        return (request, null);
    }

    /// <summary>
    /// WIDTHxHEIGHT, with either case of the separator and nothing else. The
    /// parse is strict because the alternative -- FFmpeg's own guess at a
    /// half-understood size -- produces a file at a size nobody asked for.
    /// </summary>
    public static bool TryParseSize(string text, out int width, out int height)
    {
        width = 0;
        height = 0;

        string[] parts = text.Split('x', 'X');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height)) return false;

        return width > 0 && height > 0;
    }

    /// <summary>
    /// The default output name: same folder, same stem, the new extension. It is
    /// never the input path itself, which the request validation would refuse.
    /// </summary>
    public static string DeriveDestination(string input, string container)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(input);
        return Path.Combine(folder, $"{stem}.{container}");
    }

    /// <summary>The plan, in the terms the user asked the question in.</summary>
    private static void PrintPlan(EncodePlan plan, SourceInfo source, string ffmpeg, ITerminal terminal)
    {
        terminal.Line($"source     {source.Width}x{source.Height} {source.PixelFormat}"
                    + $", {source.DurationSeconds:G6}s"
                    + (source.HasAudio ? $", audio {source.AudioRate}Hz x{source.AudioChannels}" : ", no audio"));
        terminal.Line($"output     {plan.Width}x{plan.Height} {plan.TransportPixelFormat}"
                    + $", {plan.Container} ({plan.Muxer}), {plan.Fps:G6} fps");
        terminal.Line($"span       {plan.SpanSeconds:G6}s -> {plan.TargetFrames} frames");
        terminal.Line($"frame      {plan.FrameSize} bytes on the pipe");
        terminal.Line();
        terminal.Line("decoder:");
        terminal.Line("  " + Quote(plan.DecodeCommand(ffmpeg)));
        terminal.Line("encoder:");
        terminal.Line("  " + Quote(plan.EncodeCommand(ffmpeg)));
    }

    /// <summary>
    /// What came out, in the terms the archive cares about: how long it ran
    /// against the span it covered, and what bitrate the result actually is.
    /// </summary>
    private static void PrintResult(LiveRun run, ITerminal terminal)
    {
        string size = run.OutputBytes >= 1024 * 1024
            ? $"{run.OutputBytes / 1024.0 / 1024.0:F2} MiB"
            : $"{run.OutputBytes / 1024.0:F0} KiB";

        terminal.Line();
        terminal.Line($"written    {run.OutputPath}");
        terminal.Line($"size       {size}, {run.AverageKbps:F0} kbps over {run.SpanSeconds:G4}s");
        terminal.Line($"pacing     {run.Pump.Frames} slots in {run.Pump.Seconds:F2}s "
                    + $"({run.Pump.EffectiveFps:F2} fps), "
                    + $"fresh {run.Pump.Fresh}, repeated {run.Pump.Repeated}");
    }

    /// <summary>
    /// Renders a command line the way it would be typed. A dry run exists to be
    /// pasted into a shell and tried, so arguments with spaces get quoted.
    /// </summary>
    private static string Quote(IReadOnlyList<string> command)
        => string.Join(' ', command.Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));

    /// <summary>
    /// The folder an encode would look in, or null for "whatever is on PATH".
    /// Only an explicit path can name a folder; a distribution id such as
    /// <c>btbn</c> points at a managed install, which does not exist until the
    /// toolchain command runs.
    /// </summary>
    public static string? ToolchainDirectory(SettingsDocument settings)
    {
        string? explicitPath = settings.Toolchain.FfmpegPath;
        if (string.IsNullOrWhiteSpace(explicitPath)) return null;
        if (File.Exists(explicitPath)) return Path.GetDirectoryName(explicitPath);
        return Directory.Exists(explicitPath) ? explicitPath : null;
    }
}
