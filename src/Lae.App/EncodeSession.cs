using Lae.App.Ui;
using Lae.Core;
using Lae.Core.History;
using Lae.Core.Localization;
using Lae.Core.Settings;
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
        CancellationToken token)
    {
        // --recompress-only is a different job from a capture: no encoder to choose,
        // no span to pace, no video plan. Branching before BuildRequest keeps the
        // two from sharing state they should not.
        if (overrides.RecompressOnly)
            return RunChainOnly(input, overrides, settings, dependencies, terminal, dryRun, token);

        IMessages messages = dependencies.Messages;

        // The request comes first, because it is the only step that can be wrong
        // without touching the machine. Reporting "no such encoder" before "there
        // is no FFmpeg here" is the difference between a user fixing their typing
        // and a user reinstalling their toolchain.
        (EncodeRequest? request, string? requestError) = BuildRequest(input, overrides, settings, messages);
        if (request is null)
        {
            terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = requestError }));
            return ExitCodes.Usage;
        }

        // The toolchain comes next: "no FFmpeg" is a setup problem with its own
        // command, not an encode failure, and it must not be reported as one.
        ToolchainProbeResult tools = dependencies.ProbeToolchain(ToolchainDirectory(settings));
        if (tools.Ffmpeg is not { } ffmpeg || tools.Ffprobe is null)
        {
            foreach (string line in tools.Missing())
                terminal.ErrorLine(messages.Format("encode.error.missingTool", new { tool = line }));
            terminal.ErrorLine(messages["encode.error.runSetup"]);
            return ExitCodes.Failed;
        }

        (SourceInfo? source, string? probeError) = dependencies.ProbeSource(tools.Ffprobe, input);
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
        TrackPreparation track = PrepareTrack(request, source, ffmpeg, dependencies, dryRun);
        if (track.Request is null)
        {
            terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = track.Message }));
            return track.Failure;
        }

        if (track.Note is { } note)
            terminal.ErrorLine(messages.Format("encode.note.prefixed", new { text = note }));

        try
        {
            return Execute(track.Request, source, ffmpeg, overrides.Preset, dependencies, terminal, dryRun, token);
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
        EncodeDependencies dependencies, bool dryRun)
    {
        if (request.Encode.Subtitles.Stream is not { } index) return TrackPreparation.Done(request);

        if (source.CheckPick(index, "subtitle", "--subtitle-track") is { } pickProblem)
            return TrackPreparation.Refused(ExitCodes.Usage, pickProblem);

        string? codec = source.Streams.First(s => s.Index == index).Codec;
        if (SubtitleTrack.Problem(codec, index) is { } codecProblem)
            return TrackPreparation.Refused(ExitCodes.Usage, codecProblem);

        // Beside the output, because the source may be a read-only mount or a
        // share: the folder that is about to be written to is the one folder this
        // run knows is writable.
        string output = request.DestinationPath
            ?? EncodeRequest.DeriveDestination(request.SourcePath, "subs");
        string temporary = SubtitleTrack.TemporaryPath(output, index, Guid.NewGuid().ToString("N")[..8]);

        if (!dryRun
            && dependencies.ExtractSubtitle(ffmpeg, request.SourcePath, index, temporary) is { } failure)
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
            SubtitleTrack.Note(request.SourcePath, index, codec), ExitCodes.Ok, null);
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
        string ffmpeg,
        string? presetId,
        EncodeDependencies dependencies,
        ITerminal terminal,
        bool dryRun,
        CancellationToken token)
    {
        IMessages messages = dependencies.Messages;
        (List<EncoderInfo> encoders, string? listError) = dependencies.ListEncoders(ffmpeg);
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

        EncodePlan plan = EncodePlan.Create(request, source, encoder);

        // Notes are commentary, not the answer, so they go to stderr: a dry run
        // piped to a file has to contain the two command lines and nothing else.
        foreach (string note in plan.Notes)
            terminal.ErrorLine(messages.Format("encode.note.prefixed", new { text = note }));

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
        RecompressPlan post = RecompressPlan.Create(request.Encode.Recompress, plan.DestinationPath);

        foreach (string note in post.Notes)
            terminal.ErrorLine(messages.Format("encode.note.prefixed", new { text = note }));

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
            PreflightResult trial = Preflight.Run(plan, ffmpeg, dependencies.Runner.Run, token);
            execution = trial.Plan;

            foreach (string note in trial.Notes)
                terminal.ErrorLine(messages.Format("encode.note.prefixed", new { text = note }));
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
        using (var progress = new LiveProgress(terminal, messages))
        {
            try
            {
                run = dependencies.Runner.Run(
                    execution, ffmpeg,
                    (done, total) => progress.Report(done, total),
                    note => terminal.ErrorLine(messages.Format("encode.note.prefixed", new { text = note })),
                    token);
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

            progress.Finish(
                messages.Format("encode.result.framesWritten", new
                {
                    seconds = Number(run.Pump.Seconds, "F1").PadLeft(7),
                    frames = run.Pump.Frames,
                })
                + (run.Pump.Repeated > 0
                    ? messages.Format("encode.result.framesRepeated", new { repeated = run.Pump.Repeated })
                    : ""));
        }

        PrintResult(run, terminal, messages);

        // Recorded against the capture, not against the chain below: the recording
        // is the thing this row is about, and a failed hop is reported on its own
        // line with its own exit code.
        Remember(
            dependencies.History,
            BuildRecord(request, execution, run, presetId, RunOutcome.Ok, null, started, sourceBytes),
            terminal);

        return post.Any
            ? RunChain(post, ffmpeg, plan.SpanSeconds, dependencies, terminal, token)
            : ExitCodes.Ok;

        void Stopped(string outcome, string message) => Remember(
            dependencies.History,
            BuildRecord(request, execution, null, presetId, outcome, message, started, sourceBytes),
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
