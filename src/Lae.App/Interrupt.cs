namespace Lae.App;

/// <summary>
/// Ctrl+C means "stop this run cleanly", not "kill the process".
///
/// <para>Without taking the keypress over, the runtime terminates the process
/// where it stands: both FFmpeg children keep running, the part file stays
/// behind, and on a network target the encoder holds the share open for as long
/// as it feels like. Cancelling a token instead lets the encode kill its children
/// and remove its leftovers before the program exits with 130.</para>
/// </summary>
public static class Interrupt
{
    private static readonly CancellationTokenSource Source = new();
    private static int _installed;
    private static int _presses;

    /// <summary>Cancelled on the first Ctrl+C.</summary>
    public static CancellationToken Token => Source.Token;

    /// <summary>Whether a Ctrl+C has already been seen.</summary>
    public static bool Requested => Source.IsCancellationRequested;

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
            if (Interlocked.Increment(ref _presses) > 1)
            {
                args.Cancel = false;
                return;
            }

            args.Cancel = true;
            try
            {
                Source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Nothing to cancel any more; the process is already on its way out.
            }
        };
    }
}
