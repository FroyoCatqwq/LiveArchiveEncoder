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
    /// Stop reading and wait briefly for the loop to notice. Two seconds is
    /// longer than a pending read of a pipe that still has a writer, and short
    /// enough that a shutdown the caller already considers done is not held up;
    /// a read still in flight after that is cancelled by the token and unwinds
    /// through the same filter.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _stop.Cancel(); } catch (ObjectDisposedException) { /* already down */ }
        try { _reader.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { /* shutdown race */ }
        _stop.Dispose();
    }
}
