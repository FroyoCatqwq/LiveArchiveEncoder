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

    /// <summary>Cancelled on the first Ctrl+C.</summary>
    public static CancellationToken Token => Source.Token;

    /// <summary>Whether a Ctrl+C has already been seen.</summary>
    public static bool Requested => Source.IsCancellationRequested;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        Console.CancelKeyPress += (_, args) =>
        {
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
