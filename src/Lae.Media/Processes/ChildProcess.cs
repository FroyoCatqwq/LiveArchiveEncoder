using System.Diagnostics;

namespace Lae.Media;

/// <summary>A child process whose pipes belong to the caller.</summary>
public sealed record ChildSpec
{
    /// <summary>Give the caller the child's standard output (the raw frame pipe).</summary>
    public bool ReadOutput { get; init; }

    /// <summary>Give the caller the child's standard input (the frames it encodes).</summary>
    public bool WriteInput { get; init; }

    /// <summary>How many stderr lines to keep for the failure message.</summary>
    public int DiagnosticLines { get; init; } = 64;
}

/// <summary>
/// A child process with the three things a pipeline actually needs: a pipe to
/// talk through, a drained stderr so the child never blocks on a full pipe, and a
/// kill that takes the whole tree down.
///
/// <para>Draining stderr is not optional. FFmpeg writes diagnostics there, and a
/// child whose stderr buffer fills stops making progress -- the encode hangs with
/// no message and no CPU use, which is the worst kind of failure to debug. The
/// lines are kept in a bounded ring because the caller only ever shows the tail:
/// what matters on failure is the last sentence FFmpeg said, not the first.</para>
/// </summary>
public sealed class ChildProcess : IDisposable
{
    private readonly Process _process;
    private readonly DiagnosticTap _errors;
    private bool _disposed;

    private ChildProcess(Process process, DiagnosticTap errors, Stream? output, Stream? input)
    {
        _process = process;
        _errors = errors;
        Output = output;
        Input = input;
    }

    /// <summary>The child's standard output, or null when it was not requested.</summary>
    public Stream? Output { get; }

    /// <summary>The child's standard input, or null when it was not requested.</summary>
    public Stream? Input { get; }

    /// <summary>The last few lines the child wrote to standard error.</summary>
    public string Diagnostics(int lines = 8) => _errors.TailText(lines);

    /// <summary>Whether the child has finished, without throwing if it just did.</summary>
    public bool HasExited
    {
        get
        {
            _process.Refresh();
            return _process.HasExited;
        }
    }

    public int ExitCode
    {
        get
        {
            _process.Refresh();
            return _process.ExitCode;
        }
    }

    /// <summary>
    /// Start a child. The command list is the program followed by its arguments,
    /// already rendered -- no shell, no quoting rules, no injection surface.
    /// </summary>
    public static ChildProcess Start(IReadOnlyList<string> command, ChildSpec spec)
    {
        if (command.Count == 0) throw new ArgumentException("empty command", nameof(command));

        var start = new ProcessStartInfo
        {
            FileName = command[0],
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = spec.ReadOutput,
            RedirectStandardInput = spec.WriteInput,
            RedirectStandardError = true,
        };

        for (int i = 1; i < command.Count; i++) start.ArgumentList.Add(command[i]);

        Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"could not start {command[0]}");

        Stream? output = spec.ReadOutput ? process.StandardOutput.BaseStream : null;
        Stream? input = spec.WriteInput ? process.StandardInput.BaseStream : null;

        return new ChildProcess(process, new DiagnosticTap(process.StandardError, spec.DiagnosticLines), output, input);
    }

    /// <summary>Kill the child and everything it started. Best effort by design.</summary>
    public void KillTree()
    {
        try
        {
            _process.Refresh();
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception exc) when (exc is InvalidOperationException or System.ComponentModel.Win32Exception
                                        or NotSupportedException)
        {
            // Already gone, or this platform will not let us ask. Either way the
            // caller is on a failure path and has a better message to show.
        }
    }

    /// <summary>
    /// Wait for the child in short slices so a cancel is noticed while it is
    /// still running. Returns normally when the child exited; throws when it
    /// outstayed the timeout, killing it first.
    /// </summary>
    public void WaitOrCancel(TimeSpan timeout, string timeoutMessage, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (!_process.WaitForExit(250))
        {
            if (token.IsCancellationRequested)
            {
                KillTree();
                token.ThrowIfCancellationRequested();
            }

            if (clock.Elapsed < timeout) continue;

            KillTree();
            _process.WaitForExit(5000);
            throw new InvalidOperationException(timeoutMessage);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        KillTree();
        _errors.Dispose();
        try { _process.Dispose(); } catch (InvalidOperationException) { /* already released */ }
    }

    /// <summary>
    /// Drains a child's standard error into a bounded ring, on its own thread,
    /// so neither the child nor the caller ever blocks on it.
    /// </summary>
    private sealed class DiagnosticTap : IDisposable
    {
        private readonly Queue<string> _lines = new();
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _reader;
        private readonly int _capacity;

        internal DiagnosticTap(StreamReader reader, int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _reader = Task.Run(() => Drain(reader));
        }

        internal string TailText(int count)
        {
            List<string> lines;
            lock (_gate) lines = [.. _lines];

            if (count <= 0 || lines.Count == 0) return "";
            return string.Join(" | ", lines.Count <= count ? lines : lines.Skip(lines.Count - count));
        }

        private async Task Drain(StreamReader reader)
        {
            try
            {
                while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                {
                    if (line.Length == 0) continue;
                    lock (_gate)
                    {
                        _lines.Enqueue(line.Trim());
                        while (_lines.Count > _capacity) _lines.Dequeue();
                    }
                }
            }
            catch (Exception exc) when (exc is OperationCanceledException or ObjectDisposedException or IOException)
            {
                // The run is over and the pipe went away; nothing to report.
            }
        }

        public void Dispose()
        {
            try { _stop.Cancel(); } catch (ObjectDisposedException) { /* already down */ }
            try { _reader.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { /* shutdown race */ }
            _stop.Dispose();
        }
    }
}
