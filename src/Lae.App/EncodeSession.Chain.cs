using Lae.App.Ui;
using Lae.Core;
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
/// either way, and the recording is never touched.</para>
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
        bool dryRun,
        CancellationToken token)
    {
        (RecompressChain? chain, string? chainError) = BuildChain(overrides);
        if (chain is null)
        {
            terminal.ErrorLine($"ERROR: {chainError ?? "--recompress-only needs at least one --recompress"}");
            return ExitCodes.Usage;
        }

        ToolchainProbeResult tools = dependencies.ProbeToolchain(ToolchainDirectory(settings));
        if (tools.Ffmpeg is not { } ffmpeg)
        {
            foreach (string line in tools.Missing()) terminal.ErrorLine($"ERROR: missing {line}");
            terminal.ErrorLine("Run 'lae setup' for what to do about it.");
            return ExitCodes.Failed;
        }

        // A path that is not there is the user's typo, and it costs a sentence
        // rather than an FFmpeg diagnostic about a file it could not open.
        if (!File.Exists(input))
        {
            terminal.ErrorLine($"ERROR: there is no file at '{input}'");
            return ExitCodes.Usage;
        }

        RecompressPlan plan = RecompressPlan.Create(chain, input);

        foreach (string note in plan.Notes) terminal.ErrorLine($"note: {note}");

        if (!plan.IsRunnable)
        {
            foreach (string problem in plan.Problems) terminal.ErrorLine($"ERROR: {problem}");
            return ExitCodes.Usage;
        }

        PrintChain(plan, ffmpeg, terminal);
        if (dryRun) return ExitCodes.Ok;

        double span = tools.Ffprobe is { } ffprobe
            ? dependencies.ProbeSource(ffprobe, input).Info?.DurationSeconds ?? 0
            : 0;

        RecompressOutcome outcome = dependencies.RunRecompress(
            plan, ffmpeg,
            TimeSpan.FromSeconds(Math.Max(300.0, span * 4.0 + 120.0)),
            note => terminal.ErrorLine($"note: {note}"),
            token);

        foreach (string path in outcome.Written)
            terminal.Line($"recompress {Path.GetFileName(path)}");

        if (!outcome.Failed) return ExitCodes.Ok;

        terminal.ErrorLine($"ERROR: {outcome.Error}");
        return ExitCodes.Failed;
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
}
