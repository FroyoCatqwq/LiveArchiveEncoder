using System.Diagnostics.CodeAnalysis;
using LiveArchiveEncoder.Diagnostics;

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
    // v7.9.9.3: Monitor (a plain object) is RE-ENTRANT, System.Threading.Lock
    // is not. Request() must call Cancel() inside the gate -- see below -- and
    // a cancellation callback can come back in here without deadlocking.
    private static readonly object Gate = new();
    private static CancellationTokenSource _source = new();
    private static bool _reported;

    /// <summary>
    /// Token of the current operation, for helpers that start their own child
    /// process (the FFmpeg installer in particular). Read under the gate.
    /// </summary>
    public static CancellationToken CurrentToken
    {
        get { lock (Gate) return _source.Token; }
    }

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
        lock (Gate)
        {
            if (_reported) return;
            _reported = true;

            Console.Error.WriteLine("\nInterrupted.");
            AppLog.Warn("interrupt.requested operation={Operation}", AppLog.CurrentOperation);
            // v7.9.9.3: cancel INSIDE the gate. Doing it outside left a window
            // where BeginOperation could swap in a fresh CTS; this Cancel then
            // hit the old source and the new encode never saw the Ctrl+C.
            // Gate is a re-entrant Monitor, so a callback reaching back in here
            // is safe.
            try { _source.Cancel(); }
            catch (ObjectDisposedException) { /* a late reader disposed it */ }
            catch (AggregateException exc)
            {
                // v7.9.9.5: Cancel() runs every registered callback on this
                // thread and aggregates their exceptions. One misbehaving
                // callback used to escape the CancelKeyPress handler as an
                // unhandled exception -- with e.Cancel already set, nothing
                // could then terminate the process.
                AppLog.Warn("interrupt.callbackerror count={Count}", exc.InnerExceptions.Count);
            }
        }
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
            // v7.9.9: Interrupt.BeginOperation is exactly the boundary of one
            // encode, so it also advances the log's operation correlation id.
            AppLog.BeginOperation();
            return _source.Token;
        }
    }
}
