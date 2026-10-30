using Lae.Core;
using Lae.Media;

namespace Lae.Encoding;

/// <summary>What the post-encode chain produced.</summary>
public sealed record RecompressOutcome
{
    /// <summary>The file to hand on: the last hop that was written, or the live product.</summary>
    public required string FinalPath { get; init; }

    /// <summary>Every hop that was written, in order.</summary>
    public required IReadOnlyList<string> Written { get; init; }

    /// <summary>1-based number of the hop that failed, or 0.</summary>
    public int FailedStep { get; init; }

    /// <summary>Why it stopped, or null when the chain finished.</summary>
    public string? Error { get; init; }

    public bool Failed => Error is not null;
}

/// <summary>
/// Runs the hops <see cref="RecompressPlan"/> worked out: one FFmpeg child per
/// hop, each reading the hop before.
///
/// <para>Failure policy, and it is the whole point of doing this after the live
/// pass rather than during it: <b>the recording is never touched</b>. A hop that
/// exits badly removes its own part product, reports which hop it was, and stops
/// the chain; whatever already finished stays on disk. A second pass that cannot
/// run is not a reason to throw away a twenty-minute capture.</para>
///
/// <para>Each hop writes <c>name.part.ext</c> and is renamed only once it has
/// succeeded, the same rule every other write path follows -- FFmpeg's <c>-y</c>
/// truncates its destination as it opens it, so writing straight to the real name
/// would destroy the good copy the moment a pass failed halfway.</para>
/// </summary>
public static class RecompressRun
{
    public static RecompressOutcome Run(
        RecompressPlan plan,
        string ffmpeg,
        TimeSpan perStepBudget,
        Action<string>? warn,
        CancellationToken token)
    {
        if (!plan.IsRunnable)
            throw new InvalidOperationException("the recompress plan has problems; nothing was started");

        var written = new List<string>();
        string current = plan.LivePath;

        foreach (RecompressStep step in plan.Steps)
        {
            token.ThrowIfCancellationRequested();

            string part = PartFile.PathFor(step.OutputPath);
            string? folder = Path.GetDirectoryName(step.OutputPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            // Same rule as the live pass: a part product left by a run that was
            // killed from outside is ours and is disposable. FFmpeg also gets -y,
            // so a leftover can never turn into "already exists".
            if (PartFile.Discard(part) is { } leftover) warn?.Invoke(leftover);

            string? error;
            try
            {
                error = RunStep(step, ffmpeg, part, perStepBudget, token);
            }
            catch (OperationCanceledException)
            {
                // An interrupt is not a hop failure. Its part product goes, and
                // the cancel travels on so the program can answer with 130.
                PartFile.Discard(part);
                throw;
            }

            if (error is not null)
            {
                PartFile.Discard(part);
                return new RecompressOutcome
                {
                    FinalPath = current,
                    Written = written,
                    FailedStep = step.Number,
                    Error = $"pass {step.Number} ({step.Container}) failed: {error}",
                };
            }

            try
            {
                PartFile.Promote(part, step.OutputPath);
            }
            catch (Exception exc) when (exc is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                PartFile.Discard(part);
                return new RecompressOutcome
                {
                    FinalPath = current,
                    Written = written,
                    FailedStep = step.Number,
                    Error = $"pass {step.Number} ({step.Container}) left nothing to keep: {exc.Message}",
                };
            }

            written.Add(step.OutputPath);
            current = step.OutputPath;
        }

        return new RecompressOutcome { FinalPath = current, Written = written };
    }

    /// <summary>One hop. Returns the reason it cannot be kept, or null on success.</summary>
    private static string? RunStep(
        RecompressStep step, string ffmpeg, string part, TimeSpan budget, CancellationToken token)
    {
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
