using System.Diagnostics;
using System.Text;
using LiveArchiveEncoder.Diagnostics;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>Result of a short-lived FFmpeg/ffprobe invocation.</summary>
public sealed class CaptureResult
{
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";
}

/// <summary>
/// Runs FFmpeg/ffprobe with an explicit argument vector. Arguments are never
/// concatenated into a shell string, so paths with spaces and quotes are safe.
/// </summary>
public static class ProcessRunner
{
    /// <param name="priority">
    /// Optional OS scheduling class. The split pipeline runs its audio encode with
    /// <see cref="ProcessPriorityClass.BelowNormal"/> so the parallel audio process
    /// cannot steal time from the realtime-paced capture loop.
    /// </param>
    /// <param name="cancellationToken">
    /// Kills the child when the run is abandoned. Without it a cancelled encode
    /// left its FFmpeg working on a file nobody was going to keep -- and holding
    /// the staging directory open, which made the cleanup that follows fail too.
    /// </param>
    public static CaptureResult Run(IReadOnlyList<string> args, int timeoutSeconds = 30,
                                    ProcessPriorityClass? priority = null,
                                    CancellationToken cancellationToken = default)
    {
        if (args.Count == 0) throw new ArgumentException("no executable to run", nameof(args));

        // v7.9.9: the exact child argv is the single most useful thing to have
        // when a run cannot be reproduced. AppLog redacts credentials.
        AppLog.Info("child.spawn exe={Exe} argc={Argc} argv={Argv}",
            args[0], args.Count - 1, string.Join(' ', args.Skip(1)));

        var startInfo = new ProcessStartInfo
        {
            FileName = args[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        for (int i = 1; i < args.Count; i++) startInfo.ArgumentList.Add(args[i]);

        using var proc = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {args[0]}");

        if (priority is not null)
        {
            try { proc.PriorityClass = priority.Value; } catch { /* not permitted; harmless */ }
        }

        // v7.9.9.5: every short-lived probe/preflight/download child obeys the
        // process-wide Ctrl+C token, not just the long-running encode. Linking it
        // here is what makes all ~20 callers cancellable without threading a
        // token through each of them: an interrupted run no longer leaves an
        // ffprobe or an aria2c working on a temporary directory for its full
        // 30..300 s timeout while the user waits for the prompt to come back.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, Core.Interrupt.CurrentToken);
        CancellationToken token = linked.Token;

        // Read both pipes concurrently so neither can fill its buffer and deadlock.
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(token);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(token);

        // Wait in slices rather than one long call: neither a cancel nor the
        // deadline can be observed while blocked inside WaitForExit(ms).
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long budgetMs = (long)timeoutSeconds * 1000;
        while (!proc.WaitForExit(250))
        {
            // v7.9.8.10: drain both pipes BEFORE the process is disposed. These
            // two tasks used to be abandoned on timeout/cancel, so they became
            // unobserved exceptions and FFmpeg's last words ("No space left on
            // device", "Could not write header") were lost -- the user saw only
            // "did not finish within 30s" with no idea why.
            if (token.IsCancellationRequested)
            {
                Kill(proc);
                string why = Drain(stdoutTask, stderrTask);
                throw new OperationCanceledException(
                    $"{Path.GetFileName(args[0])} was cancelled" + Suffix(why), token);
            }
            if (clock.ElapsedMilliseconds >= budgetMs)
            {
                Kill(proc);
                string why = Drain(stdoutTask, stderrTask);
                throw new TimeoutException(
                    $"{Path.GetFileName(args[0])} did not finish within {timeoutSeconds}s" + Suffix(why));
            }
        }

        // v7.9.9.5: the child has exited, but a grandchild that inherited the
        // pipe handles can keep ReadToEndAsync from ever seeing EOF. Waiting
        // without a bound here hung the process after the child was gone -- the
        // very deadlock the sliced wait above exists to avoid.
        string stdout = AwaitText(stdoutTask, proc);
        string stderr = AwaitText(stderrTask, proc);
        AppLog.Info("child.exit exe={Exe} code={ExitCode} elapsedMs={ElapsedMs} stderr={Stderr}",
            args[0], proc.ExitCode, clock.ElapsedMilliseconds,
            stderr.Length > 400 ? stderr[..400] : stderr);
        return new CaptureResult
        {
            ExitCode = proc.ExitCode,
            Stdout = stdout,
            Stderr = stderr,
        };
    }

    public static CaptureResult Run(string exe, params string[] args) => Run([exe, .. args]);

    /// <summary>
    /// Collect whatever the killed child already wrote. A read from a killed
    /// process can fault (the pipe closes under it), so this never throws.
    /// </summary>
    private static string Drain(Task<string> stdout, Task<string> stderr)
    {
        try { Task.WaitAll([stdout, stderr], 2000); } catch { /* killed child */ }
        string text = "";
        foreach (Task<string> task in new[] { stdout, stderr })
        {
            if (!task.IsCompletedSuccessfully) { Observe(task); continue; }
            string chunk = task.Result.Trim();
            if (chunk.Length > 0) text = chunk;         // stderr is read last: it wins
        }
        return text.Length > 400 ? text[..400] : text;
    }

    /// <summary>
    /// Await a pipe reader with a bound. A process that has exited normally
    /// always closes its own pipes; when it does not, the only safe answer is to
    /// stop waiting, observe the abandoned task and take the diagnostics we got.
    /// </summary>
    private static string AwaitText(Task<string> task, Process proc)
    {
        if (task.IsCompletedSuccessfully) return task.Result;
        try
        {
            return task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (Exception exc) when (exc is TimeoutException or OperationCanceledException
                                        or IOException or ObjectDisposedException)
        {
            Observe(task);
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return "";
        }
    }

    /// <summary>Keep an abandoned reader from becoming an unobserved task exception.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static string Suffix(string diagnostic) =>
        diagnostic.Length > 0 ? $"\n  last output: {diagnostic}" : "";

    /// <summary>Kill a child and everything it spawned, then give it a moment to die.</summary>
    private static void Kill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { proc.WaitForExit(5000); } catch { /* nothing left to wait for */ }
    }
}
