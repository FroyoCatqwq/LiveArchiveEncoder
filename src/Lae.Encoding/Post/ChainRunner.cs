using Lae.Core;
using Lae.Core.Plugins;
using Lae.Core.Settings;
using Lae.Media;

namespace Lae.Encoding;

/// <summary>
/// Turns a plan into hops: one child per hop, each reading the hop before.
///
/// <para>Only the two shapes of "run something" live here -- an FFmpeg command
/// line and a plugin invocation. The chain's failure policy -- never touch the
/// input, discard a failed hop's part product, stop at the first failure, keep what
/// already finished, treat an interrupt as an interrupt -- belongs to
/// <see cref="PostChainRun"/> and is shared by both. It was written twice on
/// purpose only once: the second copy would have disagreed about promoting.</para>
///
/// <para>A plugin arrives as <see cref="IPluginRunner"/>, an interface declared in
/// <c>Lae.Core</c>. That is what keeps this assembly from referencing the plugin
/// host: the pipeline learns that something can run a hop, not who it is. It also
/// means the whole runner is testable with a fake that never starts a process.</para>
/// </summary>
public static class ChainRunner
{
    public static PostChainOutcome Run(
        PostChainPlan plan,
        string ffmpeg,
        TimeSpan perStepBudget,
        IPluginRunner? plugins,
        Action<string>? warn,
        CancellationToken token)
    {
        if (!plan.IsRunnable)
            throw new InvalidOperationException("the post-encode plan has problems; nothing was started");

        return PostChainRun.Run(Hops(plan, ffmpeg, perStepBudget, plugins, warn), plan.LivePath, warn, token);
    }

    private static List<PostHop> Hops(
        PostChainPlan plan, string ffmpeg, TimeSpan budget, IPluginRunner? plugins, Action<string>? warn)
    {
        var hops = new List<PostHop>(plan.Steps.Count);

        foreach (PostChainStep step in plan.Steps)
        {
            hops.Add(step.Kind == ChainStepKind.Plugin
                ? PluginHop(step, plugins, budget, warn)
                : FfmpegHop(step, ffmpeg, budget));
        }

        return hops;
    }

    private static PostHop FfmpegHop(PostChainStep step, string ffmpeg, TimeSpan budget) => new()
    {
        Number = step.Number,

        // Reads as the subject of the runner's failure sentence:
        // "pass 2 (webm) failed: FFmpeg exited with code 1".
        Label = $"pass {step.Number} ({step.Container})",
        OutputPath = step.OutputPath,

        // The plan chains each step's InputPath to the previous step's
        // OutputPath, and the runner hands over what was actually
        // written. Re-stamping it makes the runner -- not the plan -- the
        // source of truth about what a hop reads, which is the only thing
        // a plugin hop can offer. The command line is unchanged: the two
        // paths are equal by construction.
        Produce = (input, part, token) =>
            RunStep(step with { InputPath = input }, ffmpeg, part, budget, token),
    };

    /// <summary>
    /// A plugin hop. The label names the plugin and its stage, because "step 2
    /// failed" sends the reader to the settings file while "plugin lae-damage
    /// (damage) failed" sends them to the plugin.
    /// </summary>
    private static PostHop PluginHop(
        PostChainStep step, IPluginRunner? runner, TimeSpan budget, Action<string>? warn) => new()
    {
        Number = step.Number,
        Label = $"plugin {step.Plugin} ({StageLabel(step)})",
        OutputPath = step.OutputPath,

        Produce = (input, part, token) =>
        {
            // A plan cannot be runnable without a host: the command refuses a
            // plugin hop it cannot serve before anything starts. This is the
            // programmer's guard, not a user-facing path.
            if (runner is null) return "this build has no plugin host";

            PluginStepOutcome outcome = runner.Run(new PluginCall
            {
                Plugin = step.Plugin ?? "",
                Stage = step.Stage ?? "",
                Parameters = step.Params,
                InputPath = input,
                PartPath = part,
                Budget = budget,
            }, token);

            // Notes reach the user even on success: "this step is not
            // reproducible" is worth knowing before publishing, not after.
            foreach (string note in outcome.Notes) warn?.Invoke(note);

            if (!outcome.Failed) return HopResult.Ok;

            return new HopResult
            {
                Failure = outcome.Failure == PluginFailure.Verification
                    ? ChainFailure.Verification
                    : ChainFailure.Plugin,
                Error = outcome.Error ?? "-",
            };
        },
    };

    private static string StageLabel(PostChainStep step)
        => string.IsNullOrWhiteSpace(step.Stage) ? "default stage" : step.Stage;

    /// <summary>One hop. Returns the reason it cannot be kept, or null on success.</summary>
    private static string? RunStep(
        PostChainStep step, string ffmpeg, string part, TimeSpan budget, CancellationToken token)
    {
        if (step.Kind != ChainStepKind.Ffmpeg)
            return $"step {step.Number} is not an FFmpeg hop; the planner let it through";

        ChildProcess child;
        try
        {
            child = ChildProcess.Start(step.Command(ffmpeg, part), new ChildSpec());
        }
        catch (Exception exc) when (exc is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"could not start FFmpeg: {exc.Message}";
        }

        using (child)
        {
            try
            {
                child.WaitOrCancel(budget,
                    $"pass {step.Number} did not finish within {budget.TotalSeconds:F0}s", token);
            }
            catch (OperationCanceledException)
            {
                // WaitOrCancel already killed it; the caller's catch removes the
                // part product and the interrupt reaches the top as exit 130.
                throw;
            }
            catch (InvalidOperationException exc)
            {
                return exc.Message;
            }

            if (child.ExitCode == 0) return null;

            string diagnostics = child.Diagnostics();
            return $"FFmpeg exited with code {child.ExitCode}"
                 + (diagnostics.Length > 0 ? $"; {diagnostics}" : "");
        }
    }
}
