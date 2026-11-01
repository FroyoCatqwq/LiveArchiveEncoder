using Lae.App.Ui;
using Lae.Core;
using Lae.Core.Localization;
using Lae.Core.Plugins;
using Lae.Core.Settings;
using Lae.Encoding;
using Lae.Media.Toolchain;

namespace Lae.App;

/// <summary>
/// The hops after the recording: what a site would have done to the upload.
///
/// <para>Both entry points live here because they run the same plan through the
/// same runner and differ only in where the input came from. Keeping them side by
/// side is what stops the exit-code policy from drifting: a failed hop is exit 1
/// either way, the recording is never touched, and a plugin failure is 4 or 5
/// whichever door it came through.</para>
/// </summary>
public static partial class EncodeSession
{
    /// <summary>
    /// Runs the chain against a file that is already on disk.
    ///
    /// <para>The reason it exists: tuning a chain is iterative, and paying for the
    /// capture every time makes people stop tuning. It is the same plan and the
    /// same runner as a normal run's tail -- only the capture is missing.</para>
    ///
    /// <para>The length is probed anyway, because a hop's deadline is derived from
    /// it. A source that will not probe is not a reason to refuse: the chain gets
    /// the floor value and FFmpeg's own verdict is the report.</para>
    /// </summary>
    private static int RunChainOnly(
        string input,
        EncodeOverrides overrides,
        SettingsDocument settings,
        EncodeDependencies dependencies,
        ITerminal terminal,
        IEventSink sink,
        bool dryRun,
        CancellationToken token)
    {
        IMessages messages = dependencies.Messages;

        (ChainSettings? chain, string? chainError) = BuildChainOnly(overrides, settings, messages);
        if (chain is null)
        {
            terminal.ErrorLine(chainError is null
                ? messages["encode.error.recompressOnlyNeedsChain"]
                : messages.Format("encode.error.generic", new { detail = chainError }));
            return ExitCodes.Usage;
        }

        ToolchainProbeResult tools = dependencies.ProbeToolchain(
            ToolchainDirectory(settings, dependencies.ConfigRoot));
        if (tools.Ffmpeg is not { } ffmpeg)
        {
            foreach (string line in tools.Missing())
                terminal.ErrorLine(messages.Format("encode.error.missingTool", new { tool = line }));
            terminal.ErrorLine(messages["encode.error.runSetup"]);
            return ExitCodes.Failed;
        }

        // A path that is not there is the user's typo, and it costs a sentence
        // rather than an FFmpeg diagnostic about a file it could not open.
        if (!File.Exists(input))
        {
            terminal.ErrorLine(messages.Format("encode.error.noFile", new { path = input }));
            return ExitCodes.Usage;
        }

        PostChainPlan plan = PostChainPlan.Create(chain, input, messages);

        foreach (string note in plan.Notes) sink.Warn(note);

        if (!plan.IsRunnable)
        {
            foreach (string problem in plan.Problems)
                terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = problem }));
            return ExitCodes.Usage;
        }

        (IPluginRunner? plugins, string? pluginProblem) = PluginsFor(
            plan, tools.Ffprobe, settings, overrides.Plugins, dependencies, sink);

        if (pluginProblem is not null)
        {
            terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = pluginProblem }));
            return ExitCodes.PluginError;
        }

        PrintChain(plan, ffmpeg, terminal, messages);
        if (dryRun) return ExitCodes.Ok;

        double span = tools.Ffprobe is { } ffprobe
            ? dependencies.ProbeSource(ffprobe, input, token).Info?.DurationSeconds ?? 0
            : 0;

        sink.Stage(PluginStages.BetweenPasses);

        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        PostChainOutcome outcome = dependencies.RunPostChain(
            plan, ffmpeg,
            TimeSpan.FromSeconds(Math.Max(300.0, span * 4.0 + 120.0)),
            plugins,
            sink.Warn,
            token);

        int code = ReportOutcome(outcome, plan, terminal, messages);

        // The chain-only path reported through the same sink as a capture, so it
        // owes the same closing event. Without it a consumer of --json-events waits
        // for a run that has already finished -- and this is the path the plugin
        // tests use, so the first thing the stream was asked to describe was the one
        // that never said it was done.
        sink.Done(
            outcome.Failed ? outcome.Error ?? "-" : Path.GetFileName(outcome.FinalPath),
            outcome.Written,
            System.Diagnostics.Stopwatch.GetElapsedTime(started));

        return code;
    }

    /// <summary>
    /// The hops after the recording: what a site would have done to the upload.
    ///
    /// <para>A failed hop keeps everything that was already written and leaves
    /// the recording untouched, but the exit code is still non-zero: the user
    /// asked for a chain and did not get one. Saying "done" over a half-finished
    /// chain is how a script ends up publishing the wrong product.</para>
    /// </summary>
    private static int RunChain(
        PostChainPlan plan,
        string ffmpeg,
        string ffprobe,
        SettingsDocument settings,
        IReadOnlyDictionary<string, string>? explicitPlugins,
        double span,
        EncodeDependencies dependencies,
        ITerminal terminal,
        IEventSink sink,
        CancellationToken token)
    {
        // A hop is a plain transcode, not a realtime one, so the budget is not
        // derived from the clock the pump kept. Generous on purpose: a VP9 or
        // AV1 hop on a small machine is slower than the capture that produced
        // its input, and killing it at the capture's own duration would be a
        // timeout dressed up as a failure.
        TimeSpan budget = TimeSpan.FromSeconds(Math.Max(300.0, span * 4.0 + 120.0));
        IMessages messages = dependencies.Messages;

        (IPluginRunner? plugins, string? pluginProblem) = PluginsFor(
            plan, ffprobe, settings, explicitPlugins, dependencies, sink);

        if (pluginProblem is not null)
        {
            terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = pluginProblem }));
            return ExitCodes.PluginError;
        }

        sink.Stage(PluginStages.BetweenPasses);

        PostChainOutcome outcome = dependencies.RunPostChain(
            plan, ffmpeg, budget, plugins, sink.Warn, token);

        return ReportOutcome(outcome, plan, terminal, messages, withRecording: true);
    }

    /// <summary>
    /// The plugin host for this run, or the reason there cannot be one.
    ///
    /// <para>Only built when the chain actually asks for a plugin: creating a host
    /// is harmless, but asking "is there an ffprobe" on a chain of pure FFmpeg hops
    /// would put a requirement on runs that have no plugin in them.</para>
    /// </summary>
    private static (IPluginRunner? Runner, string? Problem) PluginsFor(
        PostChainPlan plan,
        string? ffprobe,
        SettingsDocument settings,
        IReadOnlyDictionary<string, string>? explicitPlugins,
        EncodeDependencies dependencies,
        IEventSink sink)
    {
        if (!plan.Steps.Any(step => step.Kind == ChainStepKind.Plugin)) return (null, null);

        IMessages messages = dependencies.Messages;

        IPluginRunner? runner = dependencies.CreatePlugins(new PluginSetup
        {
            Ffprobe = ffprobe,
            Settings = settings,
            ConfigRoot = dependencies.ConfigRoot,
            Messages = messages,
            ProbeSource = dependencies.ProbeSource,
            Warn = sink.Warn,
            Explicit = explicitPlugins,
        });

        // The host refuses to exist without an ffprobe, because two of the three
        // product checks need to read the file back. Running a plugin unverified
        // would be worse than not running it: the user would be told a product is
        // good on the strength of an exit code.
        return runner is null ? (null, messages["plugin.error.noVerifier"]) : (runner, null);
    }

    /// <summary>
    /// What the chain produced, hop by hop, and what to exit with.
    ///
    /// <para>Shared by both entry points because the policy is the same in both:
    /// the hops that succeeded stay, the recording is untouched, and the code says
    /// what kind of thing went wrong. A failed FFmpeg hop is 1 (this machine could
    /// not do it); a plugin that refused is 4; a plugin that returned success with
    /// an unusable product is 5. The last one is worth its own number because it is
    /// the only failure where the program was lied to.</para>
    /// </summary>
    private static int ReportOutcome(
        PostChainOutcome outcome, PostChainPlan plan, ITerminal terminal, IMessages messages,
        bool withRecording = false)
    {
        foreach (string path in outcome.Written)
            terminal.Line(messages.Format("encode.chain.written", new { name = Path.GetFileName(path) }));

        if (!outcome.Failed) return ExitCodes.Ok;

        terminal.ErrorLine(messages.Format("encode.error.generic", new { detail = outcome.Error }));

        // In a normal run the recording is the thing worth keeping, and saying so
        // is what stops someone deleting it while cleaning up a failed chain. In
        // --recompress-only there is no recording of this run's making.
        if (withRecording)
            terminal.ErrorLine(messages.Format("encode.chain.recordingUntouched", new { path = plan.LivePath }));

        return outcome.FailedKind switch
        {
            ChainFailure.Verification => ExitCodes.VerificationFailed,
            ChainFailure.Plugin => ExitCodes.PluginError,
            _ => ExitCodes.Failed,
        };
    }
}
