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
    public static CaptureResult Run(IReadOnlyList<string> args, int timeoutSeconds = 30,
                                    ProcessPriorityClass? priority = null)
    {
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

        if (!proc.WaitForExit(timeoutSeconds * 1000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            proc.WaitForExit(5000);
            throw new TimeoutException($"{Path.GetFileName(args[0])} did not finish within {timeoutSeconds}s");
        }

        return new CaptureResult
        {
            ExitCode = proc.ExitCode,
            Stdout = stdoutTask.GetAwaiter().GetResult(),
            Stderr = stderrTask.GetAwaiter().GetResult(),
        };
    }

    public static CaptureResult Run(string exe, params string[] args) => Run([exe, .. args]);
}
