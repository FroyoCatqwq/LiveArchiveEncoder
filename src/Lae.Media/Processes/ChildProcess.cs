using System.Diagnostics;

namespace Lae.Media;

/// <summary>A child process whose pipes belong to the caller.</summary>
public sealed record ChildSpec
{
    /// <summary>Give the caller the child's standard output (the raw frame pipe).</summary>
    public bool ReadOutput { get; init; }

    /// <summary>Give the caller the child's standard input (the frames it encodes).</summary>
    public bool WriteInput { get; init; }

    /// <summary>
    /// Where the child starts. Null means inherit the program's own directory.
    ///
    /// <para>A plugin gets a throwaway directory so the scratch files it leaves
    /// behind are ours to delete rather than the user's project; FFmpeg hops leave
    /// it null, because everything they touch is named on the command line.</para>
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Called for each line the child writes to standard error, as it arrives.
    ///
    /// <para>Separate from <see cref="DiagnosticLines"/>, which keeps a tail for the
    /// failure message. This is for showing the lines while there is still time to
    /// care: a plugin that runs for two minutes should say what it is doing, and a
    /// plugin's warnings are not errors. Called from the reader's thread.</para>
    /// </summary>
    public Action<string>? OnErrorLine { get; init; }

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

    /// <summary>What the child answered, once it has. Guarded by <see cref="_exited"/>.</summary>
    private int _exitCode = StillRunning;

    private volatile bool _exited;

    /// <summary>What <see cref="ExitCode"/> says while the child is still running.</summary>
    private const int StillRunning = -1;

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

    /// <summary>
    /// Whether the child has finished, without throwing if it just did.
    ///
    /// <para><c>WaitForExit(0)</c> asks the operating system once and caches the
    /// answer. The old version called <c>Refresh()</c> on every read, and the pump
    /// reads this twice per slot -- sixty refreshes a second at 30 fps, each one
    /// a full re-read of the process table for an answer that changes twice per
    /// run.</para>
    /// </summary>
    public bool HasExited
    {
        get
        {
            if (_exited) return true;

            try
            {
                if (!_process.WaitForExit(0)) return false;
            }
            catch (Exception exc) when (exc is ObjectDisposedException or InvalidOperationException)
            {
                // Disposed under us -- .NET answers "no process is associated with
                // this object" with InvalidOperationException, not the disposed one.
                // The only thing that disposes is Dispose, and that kills the child
                // first, so "gone" is the honest answer; a teardown path that logs
                // state should not throw.
                return true;
            }

            Remember();
            return true;
        }
    }

    /// <summary>
    /// The child's exit code, or -1 while it is still running.
    ///
    /// <para>-1 rather than an exception because of who asks: the pump's per-slot
    /// check and the chain's failure path both want "did it fail?" and neither
    /// wants to wrap the question in a try. <c>Process.ExitCode</c> throws
    /// <c>InvalidOperationException</c> when the child is alive, so the old
    /// property could only be read safely after a short-circuit at the call site
    /// -- a rule nothing enforced.</para>
    /// </summary>
    public int ExitCode => HasExited ? _exitCode : StillRunning;

    /// <summary>Read the code once, and keep it: a disposed <c>Process</c> forgets.</summary>
    private void Remember()
    {
        _exited = true;

        try
        {
            _exitCode = _process.ExitCode;
        }
        catch (Exception exc) when (exc is InvalidOperationException or ObjectDisposedException)
        {
            // No process handle to ask (disposed under us). -1 is the honest
            // answer: not "success", not a fabricated code.
            _exitCode = StillRunning;
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

        if (spec.WorkingDirectory is { Length: > 0 } folder) start.WorkingDirectory = folder;

        Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"could not start {command[0]}");

        Stream? output = spec.ReadOutput ? process.StandardOutput.BaseStream : null;
        Stream? input = spec.WriteInput ? process.StandardInput.BaseStream : null;

        return new ChildProcess(
            process,
            new DiagnosticTap(process.StandardError.BaseStream, spec.DiagnosticLines, spec.OnErrorLine),
            output, input);
    }

    /// <summary>Kill the child and everything it started. Best effort by design.</summary>
    public void KillTree()
    {
        try
        {
            if (!HasExited) _process.Kill(entireProcessTree: true);
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

        // It is gone: take the code now, while the handle is still ours to ask.
        Remember();
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
    /// Keeps the tail of a child's standard error so a failure can quote the last
    /// thing FFmpeg said. The reading itself is <see cref="PipeLines"/>; all this
    /// adds is the bounded ring and the thread-safe read of it.
    /// </summary>
    private sealed class DiagnosticTap : IDisposable
    {
        private readonly Queue<string> _lines = new();
        private readonly Lock _gate = new();
        private readonly PipeLines _pipe;
        private readonly int _capacity;
        private readonly Action<string>? _sink;

        internal DiagnosticTap(Stream errors, int capacity, Action<string>? sink)
        {
            _capacity = Math.Max(1, capacity);
            _sink = sink;
            _pipe = PipeLines.Start(errors, Keep);
        }

        internal string TailText(int count)
        {
            List<string> lines;
            lock (_gate) lines = [.. _lines];

            if (count <= 0 || lines.Count == 0) return "";
            return string.Join(" | ", lines.Count <= count ? lines : lines.Skip(lines.Count - count));
        }

        private void Keep(string line)
        {
            if (line.Length == 0) return;

            string trimmed = line.Trim();
            if (trimmed.Length == 0) return;

            lock (_gate)
            {
                _lines.Enqueue(trimmed);
                while (_lines.Count > _capacity) _lines.Dequeue();
            }

            // Outside the lock: the sink writes to a terminal or an event stream,
            // and holding the ring's lock while someone else's code runs is how a
            // slow consumer becomes a stalled child.
            _sink?.Invoke(trimmed);
        }

        public void Dispose() => _pipe.Dispose();
    }
}
