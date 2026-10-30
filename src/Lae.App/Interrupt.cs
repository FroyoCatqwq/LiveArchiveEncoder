namespace Lae.App;

/// <summary>
/// Ctrl+C means "stop this run cleanly", not "kill the process".
///
/// <para>Without taking the keypress over, the runtime terminates the process
/// where it stands: both FFmpeg children keep running, the part file stays
/// behind, and on a network target the encoder holds the share open for as long
/// as it feels like. Cancelling a token instead lets the encode kill its children
/// and remove its leftovers before the program exits with 130.</para>
///
/// <para>A cancellation source is single-use, and that matters here: <c>lae repl</c>
/// runs many commands in one process. The first version held one source for the
/// life of the process, so pressing Ctrl+C once made every later command in the
/// prompt start already-cancelled, and the next press was counted as the second
/// one and handed straight to the runtime -- the session could be stopped but
/// never used again. <see cref="Reset"/> is what gives the prompt a fresh one,
/// and the press counter goes back to zero with it.</para>
/// </summary>
public static class Interrupt
{
    private static readonly Lock _gate = new();
    private static CancellationTokenSource _source = new();
    private static int _installed;
    private static int _presses;

    /// <summary>Cancelled on the first Ctrl+C of the current scope.</summary>
    public static CancellationToken Token
    {
        get { lock (_gate) return _source.Token; }
    }

    /// <summary>Whether a Ctrl+C has already been seen in the current scope.</summary>
    public static bool Requested
    {
        get { lock (_gate) return _source.IsCancellationRequested; }
    }

    /// <summary>
    /// Start a new scope: a fresh, uncancelled source and a press counter that
    /// starts over. A no-op when nothing was cancelled, so a command that ran to
    /// completion does not throw away a source somebody may still hold.
    /// </summary>
    public static void Reset()
    {
        lock (_gate)
        {
            if (!_source.IsCancellationRequested) return;
            _source = new CancellationTokenSource();
            _presses = 0;
        }
    }

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        Console.CancelKeyPress += (_, args) =>
        {
            // The first press is a request to stop cleanly: kill the children,
            // remove the part file, exit 130. Every press after that means the
            // request was not honoured -- a read that cannot be cancelled, a
            // child that will not die -- and the second press is the way out, so
            // it is passed through to the runtime instead of being swallowed
            // again. A program whose only exit is closing the terminal is a
            // program people learn not to trust.
            CancellationTokenSource source;
            lock (_gate)
            {
                if (_presses++ > 0)
                {
                    args.Cancel = false;
                    return;
                }

                args.Cancel = true;
                source = _source;
            }

            // Outside the lock on purpose: Cancel() runs the registered callbacks
            // synchronously, and a callback that reached back in here would
            // deadlock the handler it was registered by.
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Nothing to cancel any more; the process is already on its way out.
            }
        };
    }
}
