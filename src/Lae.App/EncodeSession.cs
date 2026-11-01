using Lae.App.Ui;
using Lae.Core;
using Lae.Core.History;
using Lae.Core.Localization;
using Lae.Core.Settings;
using Lae.Core.Toolchain;
using Lae.Encoding;
using Lae.Media.Toolchain;

namespace Lae.App;

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
///
/// <para>The type is <c>partial</c> across four files, and the split is by what
/// a reader is looking for rather than by size: this one is the run itself,
/// <c>Request</c> turns a command line into a request, <c>Chain</c> owns the hops
/// after the recording, and <c>Report</c> owns everything printed. They were one
/// 830-line file, which is longer than anyone reads before deciding a change is
/// safe.</para>
/// </summary>
public static partial class EncodeSession
{
    public static int Run(
        string input,
        EncodeOverrides overrides,
        SettingsDocument settings,
        EncodeDependencies dependencies,
        ITerminal terminal,
        bool dryRun,
        CancellationToken token,
        IEventSink? events = null)
    {
        IMessages messages = dependencies.Messages;

        // One sink for the whole command, because `--json-events` is one stream
        // for the whole command: preflight warnings, capture progress and the
        // chain's notes all belong to the same run.
        //
        // A caller may hand its own sink in (the JSON renderer will be the first
        // to); otherwise this call owns the terminal one and disposes it. Only
        // what was created here is disposed here -- a supplied sink belongs to
        // whoever supplied it.
        using var owned = events is null ? new TerminalEventSink(terminal, messages) : null;
        IEventSink sink = events ?? owned!;

        // --recompress-only is a different job from a capture: no encoder to choose,
        // no span to pace, no video plan. Branching before BuildRequest keeps the
        // two from sharing state they should not.
        if (overrides.RecompressOnly)
            return RunChainOnly(input, overrides, settings, dependencies, terminal, sink, dryRun, token);

        // The request comes first, because it is the only step that can be wrong
        // without touching the machine. Reporting "no such encoder" before "there
        // is no FFmpeg here" is the difference between a user fixing their typing
        // and a user reinstalling their toolchain.
        (EncodeRequest? request, string? requestError) = BuildRequest(
            input, overrides, settings, messages, dependencies.ConfigRoot, File.Exists);
        if (request is null)
        {
            terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = requestError }));
            return ExitCodes.Usage;
        }

        // The toolchain comes next: "no FFmpeg" is a setup problem with its own
        // command, not an encode failure, and it must not be reported as one.
        ToolchainProbeResult tools = dependencies.ProbeToolchain(
            ToolchainDirectory(settings, dependencies.ConfigRoot));
        if (tools.Ffmpeg is not { } ffmpeg || tools.Ffprobe is null)
        {
            foreach (string line in tools.Missing())
                terminal.ErrorLine(messages.Format("encode.error.missingTool", new { tool = line }));
            terminal.ErrorLine(messages["encode.error.runSetup"]);
            return ExitCodes.Failed;
        }

        (SourceInfo? source, string? probeError) = dependencies.ProbeSource(tools.Ffprobe, input, token);
        if (source is null)
        {
            terminal.ErrorLine(messages.Format("encode.error.cannotRead", new { path = input, detail = probeError }));
            return ExitCodes.Failed;
        }

        // The font folder is only resolvable here: it is the one beside FFmpeg,
        // which is exactly what the host knows and Lae.Core does not. A path that
        // does not exist is not an error -- libass falls back to the system -- so
        // the check is "is it there", not "must be".
        if (FindFontsDirectory(settings.Defaults.Subtitles.FontDir, ffmpeg) is { } fonts)
        {
            request = request with { FontsDirectory = fonts };
        }

        // An embedded track cannot be burned where it is: the filter wants a file.
        // The host unwraps it here, the plan then sees an ordinary subtitle file,
        // and the copy is deleted whether the run succeeded or not.
        TrackPreparation track = PrepareTrack(request, source, ffmpeg, dependencies, dryRun, token);
        if (track.Request is null)
        {
            terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = track.Message }));
            return track.Failure;
        }

        if (track.Note is { } note) sink.Warn(note);

        try
        {
            return Execute(track.Request, source, settings, ffmpeg, tools.Ffprobe, overrides, dependencies, terminal, sink, dryRun, token);
        }
        finally
        {
            if (track.Temporary is { } temporary) DiscardTemporary(temporary, terminal, messages);
        }
    }

    /// <summary>What preparing an embedded track ended in: a request, or a reason and a code.</summary>
    private sealed record TrackPreparation(
        EncodeRequest? Request, string? Temporary, string? Note, int Failure, string? Message)
    {
        public static TrackPreparation Done(EncodeRequest request) => new(request, null, null, ExitCodes.Ok, null);

        public static TrackPreparation Refused(int failure, string message) => new(null, null, null, failure, message);
    }

    /// <summary>
    /// Turns "burn track 3" into "burn this file".
    ///
    /// <para>Three things can go wrong and each of them is the user's to fix: a
    /// stream index that is not a subtitle track, a bitmap track that has no text
    /// to lay out, and an unwrap that FFmpeg refuses. All three come back as a
    /// sentence before anything is encoded -- the whole reason this happens before
    /// the plan rather than inside it.</para>
    ///
    /// <para>A dry run is told not to unwrap. It promises two command lines and no
    /// processes, and an extraction is a process that writes a file; the plan it
    /// prints names the path that <em>would</em> be used, which is exactly what a
    /// plan is for.</para>
    /// </summary>
    private static TrackPreparation PrepareTrack(
        EncodeRequest request, SourceInfo source, string ffmpeg,
        EncodeDependencies dependencies, bool dryRun, CancellationToken token)
    {
        if (request.Encode.Subtitles.Stream is not { } index) return TrackPreparation.Done(request);

        if (source.CheckPick(index, "subtitle", "--subtitle-track", dependencies.Messages) is { } pickProblem)
            return TrackPreparation.Refused(ExitCodes.Usage, pickProblem);

        string? codec = source.Streams.First(s => s.Index == index).Codec;
        if (SubtitleTrack.Problem(codec, index, dependencies.Messages) is { } codecProblem)
            return TrackPreparation.Refused(ExitCodes.Usage, codecProblem);

        // Beside the output, because the source may be a read-only mount or a
        // share: the folder that is about to be written to is the one folder this
        // run knows is writable.
        string output = request.DestinationPath
            ?? EncodeRequest.DeriveDestination(request.SourcePath, "subs");
        string temporary = SubtitleTrack.TemporaryPath(output, index, Guid.NewGuid().ToString("N")[..8]);

        if (!dryRun
            && dependencies.ExtractSubtitle(ffmpeg, request.SourcePath, index, temporary, token) is { } failure)
        {
            return TrackPreparation.Refused(ExitCodes.Failed, failure);
        }

        EncodeRequest prepared = request with
        {
            Encode = request.Encode with
            {
                Subtitles = request.Encode.Subtitles with
                {
                    Mode = "burn",
                    Stream = null,
                    Files = [temporary],
                },
            },
        };

        return new TrackPreparation(
            prepared, dryRun ? null : temporary,
            SubtitleTrack.Note(request.SourcePath, index, codec, dependencies.Messages), ExitCodes.Ok, null);
    }

    private static void DiscardTemporary(string path, ITerminal terminal, IMessages messages)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException)
        {
            // The run is over either way; leaving a note beats throwing out of a
            // finally block, which would replace the real result with a cleanup
            // failure nobody asked about.
            terminal.ErrorLine(
                messages.Format("encode.note.discardFailed", new { path, detail = exc.Message }));
        }
    }

    /// <summary>
    /// Everything after the request is known good: plan, report, run, and the
    /// hops that follow. Split from <see cref="Run"/> so the unwrapped subtitle
    /// track has one place to be deleted, whatever the outcome.
    /// </summary>
    private static int Execute(
        EncodeRequest request,
        SourceInfo source,
        SettingsDocument settings,
        string ffmpeg,
        string ffprobe,
        EncodeOverrides overrides,
        EncodeDependencies dependencies,
        ITerminal terminal,
        IEventSink sink,
        bool dryRun,
        CancellationToken token)
    {
        IMessages messages = dependencies.Messages;
        (List<EncoderInfo> encoders, string? listError) = dependencies.ListEncoders(ffmpeg, token);
        if (listError is not null)
        {
            terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = listError }));
            return ExitCodes.Failed;
        }

        EncoderInfo? encoder = EncoderCatalog.Find(encoders, request.Encode.Encoder);
        if (encoder is null)
        {
            terminal.ErrorLine(
                messages.Format("encode.error.noEncoder", new { name = request.Encode.Encoder }));
            terminal.ErrorLine(messages["encode.error.encoderSuggestions"]);
            return ExitCodes.Usage;
        }

        EncodePlan plan = EncodePlan.Create(request, source, encoder, messages);

        // Notes are commentary, not the answer, so they go to stderr: a dry run
        // piped to a file has to contain the two command lines and nothing else.
        foreach (string note in plan.Notes) sink.Warn(note);

        if (!plan.IsRunnable)
        {
            foreach (string problem in plan.Problems)
                terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = problem }));
            return ExitCodes.Usage;
        }

        PrintPlan(plan, source, ffmpeg, terminal, messages);

        // The post chain is planned and checked before anything is spawned. A
        // fragment with "-i" in it, or a codec that cannot be written to the
        // container asked for, has to cost a sentence now -- not after a
        // full-length capture that then ends in a refusal.
        PostChainPlan post = PostChainPlan.Create(request.Encode.Chain, plan.DestinationPath, messages);

        foreach (string note in post.Notes) sink.Warn(note);

        if (!post.IsRunnable)
        {
            foreach (string problem in post.Problems)
                terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = problem }));
            return ExitCodes.Usage;
        }

        if (post.Any) PrintChain(post, ffmpeg, terminal, messages);

        if (dryRun) return ExitCodes.Ok;

        // Three frames of the real thing before the real thing.
        //
        // A live capture cannot be retried cheaply: by the time the encoder
        // refuses a codec the clock has started and the broadcast has moved on.
        // The trial turns that into one sentence and one second, and it is also
        // where `--hwaccel auto` gets decided -- the plan comes back with the
        // method this machine actually managed, or with none.
        EncodePlan execution = plan;
        try
        {
            sink.Stage(Phases.Preflight);
            PreflightResult trial = Preflight.Run(plan, ffmpeg, dependencies.Runner.Run, token);
            execution = trial.Plan;

            foreach (string note in trial.Notes) sink.Warn(note);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exc)
        {
            // Nothing was recorded, so there is nothing to remember: a history
            // row is a fact about an encode, and this one never happened.
            terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = exc.Message }));
            return ExitCodes.Failed;
        }

        // The clock starts here rather than at the top of Run: the record is about
        // the encode, and a request that took a second to validate should not be
        // part of how long the encode took.
        DateTimeOffset started = DateTimeOffset.UtcNow;
        long? sourceBytes = SourceSize(request.SourcePath);

        LiveRun run;
        sink.Stage(Phases.Capture);

        try
        {
            run = dependencies.Runner.Run(execution, ffmpeg, sink.Progress, sink.Warn, token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is not a failure and the history must not call it one.
            // Both paths rethrow: the record is a side note, the exit code and
            // the cleanup belong to whoever caught this.
            Stopped(RunOutcome.Cancelled, "stopped by the user");
            throw;
        }
        catch (Exception exc)
        {
            Stopped(RunOutcome.Failed, exc.Message);
            throw;
        }

        // Replaces the progress line, and is the one line a human reads before the
        // report below. `artifacts` and `elapsed` are for the L2 renderer: a
        // machine wants the file and the clock, not the sentence.
        sink.Done(
            messages.Format("encode.result.framesWritten", new
            {
                seconds = Number(run.Pump.Seconds, "F1").PadLeft(7),
                frames = run.Pump.Frames,
            })
            + (run.Pump.Repeated > 0
                ? messages.Format("encode.result.framesRepeated", new { repeated = run.Pump.Repeated })
                : ""),
            [run.OutputPath],
            DateTimeOffset.UtcNow - started);

        PrintResult(run, terminal, messages);

        // Recorded against the capture, not against the chain below: the recording
        // is the thing this row is about, and a failed hop is reported on its own
        // line with its own exit code.
        Remember(
            dependencies.History,
            BuildRecord(request, execution, run, overrides.Preset, RunOutcome.Ok, null, started, sourceBytes),
            terminal);

        return post.Any
            ? RunChain(post, ffmpeg, ffprobe, settings, overrides.Plugins, plan.SpanSeconds, dependencies, terminal, sink, token)
            : ExitCodes.Ok;

        void Stopped(string outcome, string message) => Remember(
            dependencies.History,
            BuildRecord(request, execution, null, overrides.Preset, outcome, message, started, sourceBytes),
            terminal);
    }

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
    ///
    /// <para>Two settings can name a build and the resolution rule lives in
    /// <see cref="ToolchainSource"/>: an explicit path wins, then the managed
    /// install of the configured distribution -- but only when that install is
    /// really there. Pointing the search at a folder that does not exist would hide
    /// the very problem <c>lae setup</c> exists to name.</para>
    /// </summary>
    public static string? ToolchainDirectory(SettingsDocument settings, string? configRoot)
        => ToolchainSource.Folder(
            settings.Toolchain, configRoot, path => File.Exists(path) || Directory.Exists(path));

    /// <summary>
    /// The phases of a run, as the event stream spells them.
    ///
    /// <para>Deliberately short. The moments after the recording reuse
    /// <see cref="Lae.Core.Plugins.PluginStages"/> instead of restating them here:
    /// <c>between-passes</c> is the same moment whether a hop or a plugin runs in
    /// it, and two spellings for one moment is how the plugin host and the report
    /// drift apart.</para>
    /// </summary>
    internal static class Phases
    {
        internal const string Preflight = "preflight";
        internal const string Capture = "capture";
    }
}
