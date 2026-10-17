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
    private static CancellationTokenSource _source = new();

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
        if (_source.IsCancellationRequested) return;
        Console.Error.WriteLine("\nInterrupted.");
        _source.Cancel();
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
        if (_source.IsCancellationRequested) _source = new CancellationTokenSource();
        return _source.Token;
    }
}
