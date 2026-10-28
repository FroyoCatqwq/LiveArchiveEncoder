using System.Diagnostics.CodeAnalysis;

namespace LiveArchiveEncoder.Core;

/// <summary>
/// Process-wide Ctrl+C handling.
///
/// The important part is <see cref="BeginOperation"/>: a cancellation must not
/// outlive the operation that consumed it. The interactive shell stays alive
/// after an interrupt, so if the token stayed cancelled, every following encode
/// in the same session would throw <see cref="OperationCanceledException"/>
/// before doing any work.
/// </summary>
public static class Interrupt
{
    // v7.9.8.6: the pair (_source, _reported) is read by the console handler thread
    // and written by the main thread. Unprotected, a Ctrl+C landing between
    // BeginOperation's check and its reassignment was swallowed: Request saw the
    // already-cancelled old source and returned, so the new operation ran on
    // thinking nobody had asked it to stop.
    private static readonly Lock Gate = new();
    private static CancellationTokenSource _source = new();
    private static bool _reported;

    /// <summary>
    /// Installs the Ctrl+C handler. The default kill is swallowed on purpose:
    /// the encoder terminates its FFmpeg children in <c>finally</c> blocks, and
    /// letting the OS kill the process first would leave orphans behind.
    /// </summary>
    public static void Attach()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Request();
        };
    }

    /// <summary>Raises a cancellation and reports it once per operation.</summary>
    public static void Request()
    {
        CancellationTokenSource? target = null;
        lock (Gate)
        {
            if (!_reported)
            {
                _reported = true;
                target = _source;
            }
        }
        if (target is null) return;

        // Outside the gate: Cancel runs the registered callbacks synchronously, and
        // one of them reaching back in here would deadlock on a non-reentrant Lock.
        Console.Error.WriteLine("\nInterrupted.");
        target.Cancel();
    }

    /// <summary>
    /// Starts a new operation. Any pending cancellation from a previous one is
    /// cleared first, so a second encode after Ctrl+C behaves like the first.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000",
        Justification = "The previous source is intentionally not disposed: a late " +
                        "reader may still hold its token, and disposing would turn a " +
                        "benign no-op into ObjectDisposedException.")]
    public static CancellationToken BeginOperation()
    {
        lock (Gate)
        {
            if (_reported || _source.IsCancellationRequested)
            {
                _source = new CancellationTokenSource();
                _reported = false;
            }
            return _source.Token;
        }
    }
}
