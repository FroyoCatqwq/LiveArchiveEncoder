using Lae.App.Ui;
using Lae.Core;
using Lae.Core.Presets;
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

    /// <summary>Bitrate ceiling in kbps; the ladder descends when it is reached.</summary>
    public double? LimitKbps { get; init; }

    /// <summary>Bitrate the ladder climbs back from.</summary>
    public double? RecoverKbps { get; init; }

    /// <summary>Ladder rungs, when the command line named them.</summary>
    public IReadOnlyList<double>? FpsLevels { get; init; }

    /// <summary>Per-rung thresholds that came with the rungs.</summary>
    public IReadOnlyList<FpsThreshold>? FpsThresholds { get; init; }

    /// <summary>WIDTHxHEIGHT as typed.</summary>
    public string? Size { get; init; }

    /// <summary>Id of a stored preset to lay over the defaults before anything else.</summary>
    public string? Preset { get; init; }

    /// <summary>Sidecar subtitle files to burn, in order. Empty means none.</summary>
    public IReadOnlyList<string>? SubtitleFiles { get; init; }

    /// <summary>Shape the burned text is laid out in: "source", "4:3" or "16:9".</summary>
    public string? SubtitleBox { get; init; }

    /// <summary>Font family forced over whatever the subtitle asks for.</summary>
    public string? SubtitleFont { get; init; }

    /// <summary>Extra directory libass may look in.</summary>
    public string? FontsDir { get; init; }

    /// <summary>
    /// One FFmpeg fragment per post-encode hop, in order. Empty means no chain:
    /// the chain is never taken from the settings, only from the command line.
    /// </summary>
    public IReadOnlyList<string>? RecompressArgs { get; init; }

    /// <summary>Container per hop, paired by position with <see cref="RecompressArgs"/>.</summary>
    public IReadOnlyList<string>? RecompressContainers { get; init; }
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
        Runner = new LiveEncodeRunner(),
        RunRecompress = RecompressRun.Run,
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
        // The request comes first, because it is the only step that can be wrong
        // without touching the machine. Reporting "no such encoder" before "there
        // is no FFmpeg here" is the difference between a user fixing their typing
        // and a user reinstalling their toolchain.
        (EncodeRequest? request, string? requestError) = BuildRequest(input, overrides, settings);
        if (request is null)
        {
            terminal.ErrorLine($"ERROR: {requestError}");
            return ExitCodes.Usage;
        }

        // The toolchain comes next: "no FFmpeg" is a setup problem with its own
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

        // The font folder is only resolvable here: it is the one beside FFmpeg,
        // which is exactly what the host knows and Lae.Core does not. A path that
        // does not exist is not an error -- libass falls back to the system -- so
        // the check is "is it there", not "must be".
        if (FindFontsDirectory(settings.Defaults.Subtitles.FontDir, tools.Ffmpeg) is { } fonts)
        {
            request = request with { FontsDirectory = fonts };
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

        // The post chain is planned and checked before anything is spawned. A
        // fragment with "-i" in it, or a codec that cannot be written to the
        // container asked for, has to cost a sentence now -- not after a
        // full-length capture that then ends in a refusal.
        RecompressPlan post = RecompressPlan.Create(request.Encode.Recompress, plan.DestinationPath);

        foreach (string note in post.Notes) terminal.ErrorLine($"note: {note}");

        if (!post.IsRunnable)
        {
            foreach (string problem in post.Problems) terminal.ErrorLine($"ERROR: {problem}");
            return ExitCodes.Usage;
        }

        if (post.Any) PrintChain(post, tools.Ffmpeg, terminal);

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

        return post.Any
            ? RunChain(post, tools.Ffmpeg, plan.SpanSeconds, dependencies, terminal, token)
            : ExitCodes.Ok;
    }

    /// <summary>
    /// The hops after the recording: what a site would have done to the upload.
    ///
    /// <para>A failed hop keeps everything that was already written and leaves
    /// the recording untouched, but the exit code is still 1: the user asked for
    /// a chain and did not get one. Saying "done" over a half-finished chain is
    /// how a script ends up publishing the wrong product.</para>
    /// </summary>
    private static int RunChain(
        RecompressPlan plan, string ffmpeg, double span,
        EncodeDependencies dependencies, ITerminal terminal, CancellationToken token)
    {
        // A hop is a plain transcode, not a realtime one, so the budget is not
        // derived from the clock the pump kept. Generous on purpose: a VP9 or
        // AV1 hop on a small machine is slower than the capture that produced
        // its input, and killing it at the capture's own duration would be a
        // timeout dressed up as a failure.
        TimeSpan budget = TimeSpan.FromSeconds(Math.Max(300.0, span * 4.0 + 120.0));

        RecompressOutcome outcome = dependencies.RunRecompress(
            plan, ffmpeg, budget,
            note => terminal.ErrorLine($"note: {note}"),
            token);

        foreach (string path in outcome.Written)
            terminal.Line($"recompress {Path.GetFileName(path)}");

        if (!outcome.Failed) return ExitCodes.Ok;

        terminal.ErrorLine($"ERROR: {outcome.Error}");
        terminal.ErrorLine($"the recording is untouched: {plan.LivePath}");
        return ExitCodes.Failed;
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
        // Order: stored defaults, then the preset, then the command line. Each
        // layer may only overwrite what it actually spells, which is what makes
        // "my preset, but at a smaller size" a one-word change.
        EncodeDefaults encode = settings.Defaults;

        if (overrides.Preset is { Length: > 0 } presetId)
        {
            if (PresetCatalog.Find(settings, presetId) is not { } preset)
                return (null, $"there is no preset named '{presetId}'; run 'lae preset list'");

            encode = PresetCatalog.Apply(preset, encode);
        }

        encode = encode with
        {
            Recompress = new RecompressChain(),
            Subtitles = new SubtitleDefaults(),
        };

        // ...and the same rule for subtitles: the file the last interactive
        // session had open is not this run's business. Only the command line can
        // turn burning on, which also means a scripted run cannot start drawing
        // captions because of something in a settings file.
        if (overrides.SubtitleFiles is { Count: > 0 } subtitleFiles)
        {
            encode = encode with
            {
                Subtitles = new SubtitleDefaults
                {
                    Mode = "burn",
                    Files = [.. subtitleFiles.Where(f => !string.IsNullOrWhiteSpace(f))],
                    BoxAspect = overrides.SubtitleBox ?? "source",
                    FallbackFont = overrides.SubtitleFont,
                    FontDir = overrides.FontsDir,
                },
            };
        }

        // The chain is armed by naming a hop and by nothing else. Each
        // --recompress is one more transcode, in the order written;
        // --recompress-container is paired by position, and a hop without one
        // derives its container from the codec it names.
        if (overrides.RecompressArgs is { Count: > 0 } passes)
        {
            string[] containers = overrides.RecompressContainers is { Count: > 0 } given ? [.. given] : [];

            if (containers.Length > passes.Count)
            {
                return (null, $"{containers.Length} containers were given for {passes.Count} passes; "
                             + "there is nothing for the extra one to belong to");
            }

            encode = encode with
            {
                Recompress = new RecompressChain
                {
                    Enabled = true,
                    Passes =
                    [
                        .. passes.Select((args, index) =>
                            new RecompressPass(args, index < containers.Length ? containers[index] : "")),
                    ],
                },
            };
        }

        // The encoder moves first, and the quality number moves with it. A stored
        // 8 means a 2004 stream's -q:v 8; handed to libx264 unchanged it becomes
        // -crf 8, which is a different picture by a factor of two and a half in
        // bitrate. That is v7.9.8.2's bug, and switching the encoder from the
        // command line is the same door it came through -- the preset path
        // converts, so this one has to as well.
        //
        // Only when the user did not restate the number: --quality 20 alongside
        // --encoder libx264 is already a CRF, and converting it would turn 20
        // into 25.
        if (overrides.Encoder is { Length: > 0 } named)
        {
            EncodeDefaults previous = encode;
            encode = encode with { Encoder = named };

            if (overrides.Quality is null)
            {
                encode = encode with
                {
                    Quality = EncoderMath.ConvertQuality(previous.Encoder, named, previous.Quality),
                };
            }
        }
        else if (!encode.Encoder.HasText)
        {
            encode = encode with { Encoder = "mpeg4" };
        }

        if (overrides.Container is { } container) encode = encode with { Container = container };
        if (overrides.Quality is { } quality) encode = encode with { Quality = quality };
        if (overrides.LimitKbps is { } limit) encode = encode with { LimitKbps = limit };
        if (overrides.RecoverKbps is { } recover) encode = encode with { RecoverKbps = recover };

        // The ladder is replaced whole, never merged: rungs and their thresholds
        // are one list keyed by frame rate, and taking the new rungs while
        // keeping the old thresholds would put a user's 150 kbps drop point on
        // whichever rung happened to survive.
        if (overrides.FpsLevels is { Count: > 0 } levels)
        {
            encode = encode with
            {
                FpsLevels = [.. levels],
                FpsThresholds = overrides.FpsThresholds is { } thresholds ? [.. thresholds] : [],
            };
        }

        if (overrides.Size is { } size)
        {
            if (!TryParseSize(size, out int width, out int height))
                return (null, $"--size wants WIDTHxHEIGHT, got '{size}'");

            encode = encode with { Width = width, Height = height };
        }

        // No --out means "beside the source", but the extension is the plan's to
        // decide: it is the container the chosen encoder actually writes, which
        // is not known until the encoder has been looked up in this build. The
        // CLI used to guess "avi" here, and `--encoder libx264` then wrote an
        // MP4 into clip.avi.
        string? destination = string.IsNullOrWhiteSpace(overrides.Output) ? null : overrides.Output;

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

    /// <summary>The plan, in the terms the user asked the question in.</summary>
    private static void PrintPlan(EncodePlan plan, SourceInfo source, string ffmpeg, ITerminal terminal)
    {
        terminal.Line($"source     {source.Width}x{source.Height} {source.PixelFormat}"
                    + $", {source.DurationSeconds:G6}s"
                    + (source.HasAudio ? $", audio {source.AudioRate}Hz x{source.AudioChannels}" : ", no audio"));
        terminal.Line($"output     {plan.Width}x{plan.Height} {plan.TransportPixelFormat}"
                    + $", {plan.Container} ({plan.Muxer}), {plan.Fps:G6} fps");
        terminal.Line($"span       {plan.SpanSeconds:G6}s -> {plan.TargetFrames} frames");

        // The quality number is the one setting whose meaning changes with the
        // encoder -- the same 8 is "quite good" in the mpeg family and "nearly
        // lossless" in x264 -- so a dry run names the flag and the scale rather
        // than a bare number the reader has to interpret.
        string quality = EncoderMath.ScaleOf(plan.Encode.Encoder) switch
        {
            EncoderMath.QualityScale.Crf => $"crf {plan.Encode.Quality}",
            EncoderMath.QualityScale.Bitrate => "no quality knob; the ceiling is the only dial",
            _ => $"q:v {plan.Encode.Quality}",
        };
        terminal.Line($"quality    {quality}");

        // The ladder is the one part of the plan the user cannot see in the
        // command lines -- it changes what goes down the pipe, not what FFmpeg is
        // told -- so a dry run has to name it or the two commands are not the
        // whole story.
        if (plan.BuildLadder() is { } ladder)
        {
            terminal.Line($"ladder     {string.Join(" > ", ladder.Rungs.Select(r => r.Fps.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)))} fps"
                        + $", down at {plan.Encode.LimitKbps:G4} kbps"
                        + $", up at {LadderPolicy.RecoverKbps(plan.Encode):G4} kbps"
                        + $", over {plan.Encode.WindowSeconds:G4}s");
        }
        else
        {
            terminal.Line("ladder     off (single rung or no bitrate ceiling)");
        }

        terminal.Line($"frame      {plan.FrameSize} bytes on the pipe");
        terminal.Line();
        terminal.Line("decoder:");
        terminal.Line("  " + Quote(plan.DecodeCommand(ffmpeg)));
        terminal.Line("encoder:");
        terminal.Line("  " + Quote(plan.EncodeCommand(ffmpeg)));
    }

    /// <summary>
    /// The post-encode hops, one command line each. A dry run exists to be
    /// pasted into a shell, so the chain has to be in it: a plan that shows the
    /// capture and hides three transcodes is not the plan.
    /// </summary>
    private static void PrintChain(RecompressPlan plan, string ffmpeg, ITerminal terminal)
    {
        terminal.Line();
        terminal.Line("recompress:");
        foreach (RecompressStep step in plan.Steps)
            terminal.Line($"  {Quote(step.Command(ffmpeg, step.OutputPath))}");
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

        // Only worth a line when it actually happened: a run that stayed on the
        // top rung is the normal case, and saying so on every encode is noise.
        if (run.Pump.FinalRung > 0 || run.Pump.Dropped > 0)
        {
            terminal.Line($"motion     ended at {run.Pump.MotionFps:G4} fps"
                        + (run.Pump.FinalRung > 0 ? $" (rung {run.Pump.FinalRung})" : "")
                        + $", {run.Pump.Dropped} decoded frames dropped");
        }
    }

    /// <summary>
    /// Renders a command line the way it would be typed. A dry run exists to be
    /// pasted into a shell and tried, so arguments with spaces get quoted.
    /// </summary>
    private static string Quote(IReadOnlyList<string> command)
        => string.Join(' ', command.Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));

    /// <summary>
    /// Where the burn filter should look for fonts: the folder the user named, or
    /// the one beside FFmpeg, which is where a distribution install puts the
    /// family its presets ask for. Null when neither exists, so libass falls back
    /// to the system's own library rather than to a path that is not there.
    /// </summary>
    public static string? FindFontsDirectory(string? configured, string? ffmpeg)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;
        if (string.IsNullOrWhiteSpace(ffmpeg)) return null;

        string? folder = Path.GetDirectoryName(ffmpeg);
        if (folder is null) return null;

        string candidate = Path.Combine(folder, "fonts");
        return Directory.Exists(candidate) ? candidate : null;
    }

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
