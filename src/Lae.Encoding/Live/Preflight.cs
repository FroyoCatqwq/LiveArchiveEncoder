using Lae.Core;
using Lae.Media;

namespace Lae.Encoding;

/// <summary>
/// Whatever can run one plan. Matches <c>IEncodeRunner.Run</c> in the layer
/// above, which is the point: the preflight has to drive exactly the same code
/// path as the real run, or it proves nothing about the real run.
/// </summary>
public delegate LiveRun PlanRunner(
    EncodePlan plan, string ffmpeg,
    Action<long, long?>? progress, Action<string>? warn, CancellationToken token);

/// <summary>The plan to run, and what choosing it cost.</summary>
public sealed record PreflightResult
{
    public required EncodePlan Plan { get; init; }

    /// <summary>Sentences worth printing: a fallback happened, or a trial was skipped.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>True when a trial actually ran.</summary>
    public required bool Trialed { get; init; }
}

/// <summary>
/// Runs the real command shape over three frames before committing to the
/// recording, and turns "this machine cannot do it" into one sentence.
///
/// <para>v7 had this and v8 briefly lost it. Losing it means the first sign of a
/// bad combination is a live capture that has already started: the decoder
/// refuses a filter this build does not have, or the muxer refuses the codec,
/// and the pump reports "no frames came out of the decoder" -- true, and useless,
/// because the sentence the user needs is the three lines FFmpeg printed at the
/// start of the run. Three frames cost about a second; a broken hour-long
/// capture costs the hour.</para>
///
/// <para>It is also the only honest way to answer <c>--hwaccel auto</c>. Whether
/// a hardware decoder works is a property of the machine, not of the plan, so it
/// cannot be decided by reading configuration -- it is decided by trying, which
/// is exactly what this does, and then by falling back to software decoding in
/// the plan rather than in the middle of a recording.</para>
/// </summary>
public static class Preflight
{
    /// <summary>
    /// Trials <paramref name="plan"/> and returns the plan that should actually
    /// run. Throws when nothing would run -- the trial's own reason, not a
    /// summary of it.
    /// </summary>
    public static PreflightResult Run(
        EncodePlan plan, string ffmpeg, PlanRunner run, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(run);

        // A run this short *is* a trial. Re-running it to prove it can run would
        // double the process count to save nothing.
        if (plan.TargetFrames <= EncodePlan.TrialFrames)
        {
            return new PreflightResult
            {
                Plan = plan,
                Trialed = false,
                Notes = [$"the run is {plan.TargetFrames} frame(s); it is its own trial"],
            };
        }

        Exception? first = Try(plan, ffmpeg, run, token);
        if (first is null) return new PreflightResult { Plan = plan, Trialed = true };

        // A cancelled trial is the user's doing, not a verdict on the plan.
        if (first is OperationCanceledException) throw first;

        string method = plan.Encode.Hwaccel;
        bool hardware = !string.IsNullOrWhiteSpace(method)
                     && !string.Equals(method, "off", StringComparison.OrdinalIgnoreCase);

        if (!hardware)
            throw new InvalidOperationException(Explain(first), first);

        // The hardware path failed. Software decoding is the fallback that v7
        // shipped, and it is right: the user asked for acceleration, not for a
        // guarantee, and a recording in software beats an error message.
        EncodePlan software = plan.WithHwaccel("off");
        Exception? second = Try(software, ffmpeg, run, token);
        if (second is OperationCanceledException) throw second;

        if (second is not null)
        {
            // Both failed, so the hardware was never the problem. Report the
            // software failure: it is the same command the user would get after
            // "just turn hwaccel off", and it is the one worth reading.
            throw new InvalidOperationException(Explain(second), second);
        }

        string note = $"hardware decoding ({method}) failed the trial; decoding in software; {first.Message}";

        return new PreflightResult
        {
            Plan = software,
            Trialed = true,
            Notes = [note],
        };
    }

    /// <summary>
    /// Where a trial writes. A dot-file beside the output, so it lands on the
    /// same filesystem (a trial into /tmp would prove the wrong thing about a
    /// share) and so a leftover from a killed process is obviously ours.
    /// </summary>
    public static string ProbePath(EncodePlan plan)
    {
        string full = Path.GetFullPath(plan.DestinationPath);
        string folder = Path.GetDirectoryName(full) ?? ".";
        return Path.Combine(folder, $".lae-preflight-{Guid.NewGuid():N}.{plan.Container}");
    }

    private static Exception? Try(EncodePlan plan, string ffmpeg, PlanRunner run, CancellationToken token)
    {
        string probe = ProbePath(plan);

        try
        {
            // No progress callback: three frames are not a progress bar, and a
            // caller that drew one would see it flash.
            run(plan.ForTrial(probe), ffmpeg, null, null, token);
            return null;
        }
        catch (Exception exc)
        {
            return exc;
        }
        finally
        {
            // The trial's output is not a deliverable. Its part file is removed
            // by the run itself on failure, but a successful trial promotes it,
            // so both names have to go.
            Discard(probe);
        }
    }

    private static void Discard(string path)
    {
        foreach (string candidate in (string[])[path, PartFile.PathFor(path)])
        {
            try
            {
                if (File.Exists(candidate)) File.Delete(candidate);
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                // A leftover trial file is litter, not a failure. Saying so would
                // bury the real reason the trial stopped.
            }
        }
    }

    /// <summary>
    /// The trial's reason, prefixed so a user can tell it apart from a failure
    /// halfway through a recording.
    /// </summary>
    private static string Explain(Exception exc)
        => "the three-frame trial failed, so nothing was recorded: " + exc.Message;
}
