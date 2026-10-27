using System.Diagnostics;
using System.Text;

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

        // Read both pipes concurrently so neither can fill its buffer and deadlock.
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

        // Wait in slices rather than one long call: neither a cancel nor the
        // deadline can be observed while blocked inside WaitForExit(ms).
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int budgetMs = timeoutSeconds * 1000;
        while (!proc.WaitForExit(250))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Kill(proc);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (clock.ElapsedMilliseconds >= budgetMs)
            {
                Kill(proc);
                throw new TimeoutException($"{Path.GetFileName(args[0])} did not finish within {timeoutSeconds}s");
            }
        }

        return new CaptureResult
        {
            ExitCode = proc.ExitCode,
            Stdout = stdoutTask.GetAwaiter().GetResult(),
            Stderr = stderrTask.GetAwaiter().GetResult(),
        };
    }

    public static CaptureResult Run(string exe, params string[] args) => Run([exe, .. args]);

    /// <summary>Kill a child and everything it spawned, then give it a moment to die.</summary>
    private static void Kill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { proc.WaitForExit(5000); } catch { /* nothing left to wait for */ }
    }
}
