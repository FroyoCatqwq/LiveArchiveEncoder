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

        (string folder, string? moved) = ProbeFolder(plan);
        var notes = new List<string>(2);
        if (moved is not null) notes.Add(moved);

        Exception? first = Try(plan, ffmpeg, run, folder, token);
        if (first is null)
        {
            return new PreflightResult { Plan = plan, Trialed = true, Notes = notes };
        }

        // A cancelled trial is the user's doing, not a verdict on the plan.
        // Rethrown with its own stack: `throw first` would reset the trace to
        // this line and hide where the wait actually was.
        if (first is OperationCanceledException) Rethrow(first);

        string method = plan.Encode.Hwaccel;
        bool hardware = !string.IsNullOrWhiteSpace(method)
                     && !string.Equals(method, "off", StringComparison.OrdinalIgnoreCase);

        if (!hardware)
            throw new InvalidOperationException(Explain(first), first);

        // The hardware path failed. Software decoding is the fallback that v7
        // shipped, and it is right: the user asked for acceleration, not for a
        // guarantee, and a recording in software beats an error message.
        EncodePlan software = plan.WithHwaccel("off");
        Exception? second = Try(software, ffmpeg, run, folder, token);
        if (second is OperationCanceledException) Rethrow(second);

        if (second is not null)
        {
            // Both failed, so the hardware was never the problem. Report the
            // software failure: it is the same command the user would get after
            // "just turn hwaccel off", and it is the one worth reading.
            throw new InvalidOperationException(Explain(second), second);
        }

        notes.Add($"hardware decoding ({method}) failed the trial; decoding in software; {first.Message}");

        return new PreflightResult
        {
            Plan = software,
            Trialed = true,
            Notes = notes,
        };
    }

    /// <summary>
    /// Where a trial writes. A dot-file beside the output, so it lands on the
    /// same filesystem (a trial into a temporary folder would prove the wrong
    /// thing about a share) and so a leftover from a killed process is obviously
    /// ours.
    /// </summary>
    public static string ProbePath(EncodePlan plan, string? folder = null)
        => Path.Combine(
            folder ?? OutputFolder(plan),
            $".lae-preflight-{Guid.NewGuid():N}.{plan.Container}");

    /// <summary>
    /// The folder a trial should write in, and the sentence to print when that is
    /// not the output folder.
    ///
    /// <para>The output folder is the right place: a trial there proves the muxer
    /// can write on the filesystem the recording will live on, which a trial
    /// somewhere in the temporary folder does not. But not every landing place
    /// accepts a name the program invents -- an archival mount that only allows
    /// the exact output name, a share with an extension whitelist -- and there
    /// the trial fails for a reason that says nothing about the encode. So the
    /// output folder is asked first, with the same shape of name the trial will
    /// really use, and only when that is refused does the trial move, saying
    /// where it went and what that no longer proves.</para>
    /// </summary>
    /// <para>Both predicates are parameters so the decision can be tested without
    /// arranging a folder that really refuses writes, which no portable test can
    /// do -- Windows cannot express it with attributes and POSIX cannot express
    /// it for root. The defaults are the real ones.</para>
    /// </summary>
    public static (string Folder, string? Note) ProbeFolder(
        EncodePlan plan,
        Func<string, bool>? folderExists = null,
        Func<string, EncodePlan, bool>? accepts = null)
    {
        Func<string, bool> exists = folderExists ?? Directory.Exists;
        Func<string, EncodePlan, bool> take = accepts ?? Accepts;
        string intended = OutputFolder(plan);

        // A folder that is not there is not a permission problem. Let the trial
        // fail in it, so the sentence FFmpeg prints names the path it could not
        // open; moving the trial elsewhere would hide that.
        if (!exists(intended) || take(intended, plan)) return (intended, null);

        string fallback = Path.GetTempPath();
        return (fallback,
            $"the output folder refused a trial file, so the three-frame trial ran in {fallback}; "
            + "the muxer was not proven against the destination filesystem");
    }

    private static string OutputFolder(EncodePlan plan)
        => Path.GetDirectoryName(Path.GetFullPath(plan.DestinationPath)) ?? ".";

    /// <summary>
    /// Whether the folder will take a name of the shape the trial creates. The
    /// part file is the one asked about, because that is what the run opens
    /// first, and it is the name a whitelist will answer about.
    /// </summary>
    private static bool Accepts(string folder, EncodePlan plan)
    {
        string probe = PartFile.PathFor(ProbePath(plan, folder));

        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // Opening it is the whole question.
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or NotSupportedException or ArgumentException)
        {
            return false;
        }

        try
        {
            File.Delete(probe);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // Leftover litter, not a verdict: the folder did accept the name.
        }

        return true;
    }

    private static Exception? Try(EncodePlan plan, string ffmpeg, PlanRunner run, string folder,
        CancellationToken token)
    {
        string probe = ProbePath(plan, folder);

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

    /// <summary>Re-throw a caught exception without losing the place it came from.</summary>
    private static void Rethrow(Exception exc)
        => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exc);
}

