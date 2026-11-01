using Lae.Core;
using Lae.Core.Settings;
using Lae.Media;

namespace Lae.Encoding;

/// <summary>
/// Runs the hops <see cref="PostChainPlan"/> worked out: one child per hop, each
/// reading the hop before.
///
/// <para>Only the FFmpeg half lives here. The chain's failure policy -- never
/// touch the input, discard a failed hop's part product, stop at the first
/// failure, keep what already finished, treat an interrupt as an interrupt --
/// belongs to <see cref="PostChainRun"/> and is shared with every other kind of
/// hop. It was written twice on purpose only once: the second copy would have
/// been the plugin's, and the two would have disagreed about promoting.</para>
///
/// <para>What is left is the part that really is about FFmpeg: a plan's step
/// becomes a command line, and the child is given a deadline derived from the
/// recording's length.</para>
///
/// <para>A plugin hop cannot arrive here today: the planner refuses one before
/// anything is started. When the host exists, this is where the second branch
/// goes -- and it must not become a second copy of the loop.</para>
/// </summary>
public static class ChainRunner
{
    public static PostChainOutcome Run(
        PostChainPlan plan,
        string ffmpeg,
        TimeSpan perStepBudget,
        Action<string>? warn,
        CancellationToken token)
    {
        if (!plan.IsRunnable)
            throw new InvalidOperationException("the post-encode plan has problems; nothing was started");

        return PostChainRun.Run(Hops(plan, ffmpeg, perStepBudget), plan.LivePath, warn, token);
    }

    private static List<PostHop> Hops(PostChainPlan plan, string ffmpeg, TimeSpan budget)
    {
        var hops = new List<PostHop>(plan.Steps.Count);

        foreach (PostChainStep step in plan.Steps)
        {
            hops.Add(new PostHop
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
            });
        }

        return hops;
    }

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
