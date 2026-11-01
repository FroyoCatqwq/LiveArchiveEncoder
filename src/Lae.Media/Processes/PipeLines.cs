namespace Lae.Media;

/// <summary>
/// Reads newline-delimited text from a pipe until the pipe ends, handing every
/// line to a callback on a task of its own.
///
/// <para>Both taps in this assembly want exactly this and used to do it twice:
/// one keeps the last few diagnostics, the other keeps the latest progress
/// report, and each had its own copy of the read loop, the same exception filter
/// and the same shutdown dance. The copies had already drifted -- the reader's
/// ownership was spelled differently in each -- which is how two copies of a
/// three-line loop become a bug. One implementation means one place where a
/// cancelled read is swallowed and one place where the reader is closed.</para>
///
/// <para>Draining is not optional. A child whose pipe is not read blocks forever
/// once the buffer fills, and a blocked encoder looks exactly like a slow one:
/// no message, no CPU, nothing to grep for.</para>
/// </summary>
internal sealed class PipeLines : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private bool _disposed;

    private PipeLines(Stream stream, Action<string> onLine)
        => _reader = Task.Run(() => Drain(stream, onLine));

    /// <summary>
    /// Start reading. Lines arrive on a pool thread, in order, with their line
    /// endings removed; blank lines are passed through and the caller decides.
    /// </summary>
    public static PipeLines Start(Stream stream, Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onLine);
        return new PipeLines(stream, onLine);
    }

    private async Task Drain(Stream stream, Action<string> onLine)
    {
        // Owned here, closed here. Leaving it to the child's own disposal means
        // the reader outlives the run that used it, which on Windows is one more
        // handle on a pipe whose writer is already gone.
        using var reader = new StreamReader(stream);

        try
        {
            while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                onLine(line);
        }
        catch (Exception exc) when (exc is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The run is over and the pipe went away; nothing to report.
        }
    }

    /// <summary>
    /// Stop reading, and return without waiting for the loop to notice.
    ///
    /// <para>The old version blocked for up to two seconds here. Nothing needed
    /// that wait -- a caller has already read whatever diagnostics it wanted, and
    /// the pipe is closed by the child's own death a moment later -- while it is
    /// the textbook sync-over-async shape: harmless in a console program, a
    /// deadlock in any host with a synchronization context. A reader that is a
    /// pool task ending when its pipe closes does not need to be joined.</para>
    ///
    /// <para>The token source is disposed by the reader instead, on the way out.
    /// Disposing it here would race the loop's next token access and turn a clean
    /// shutdown into an <c>ObjectDisposedException</c> swallowed by the same filter
    /// that is supposed to swallow real pipe errors.</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _stop.Cancel(); } catch (ObjectDisposedException) { /* already down */ }

        _ = _reader.ContinueWith(
            _ => _stop.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
