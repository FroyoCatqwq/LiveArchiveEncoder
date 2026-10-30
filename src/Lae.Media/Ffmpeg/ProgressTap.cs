using Lae.Core;

namespace Lae.Media;

/// <summary>
/// Drains <c>ffmpeg -progress pipe:1</c> on its own thread and keeps the latest
/// report.
///
/// <para>Reading rather than polling, for the same reason the diagnostics tap
/// reads: a child whose pipe is not drained blocks forever once the buffer
/// fills, and a blocked encoder looks exactly like a slow one.</para>
///
/// <para>The value is a struct in a field, written by the reader and read by the
/// pacing loop. No lock: a torn read of two longs would at worst give one stale
/// sample, and the window is a moving average over two seconds -- a lock on the
/// hot path would cost more than the error it prevents. <c>volatile</c> is not
/// available for a struct, so the field is a reference to an immutable record
/// instead, which is what makes the swap atomic.</para>
/// </summary>
public sealed class ProgressTap : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private Report _latest = new(new EncoderProgress());

    public ProgressTap(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _reader = Task.Run(() => Drain(stream));
    }

    /// <summary>The most recent report, or an empty one before FFmpeg has spoken.</summary>
    public EncoderProgress Current => Volatile.Read(ref _latest).Value;

    private async Task Drain(Stream stream)
    {
        using var reader = new StreamReader(stream);
        EncoderProgress current = default;

        try
        {
            while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                if (FfmpegProgress.TryRead(line, ref current)) Volatile.Write(ref _latest, new Report(current));
            }
        }
        catch (Exception exc) when (exc is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The run ended and the pipe went away.
        }
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch (ObjectDisposedException) { /* already down */ }
        try { _reader.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { /* shutdown race */ }
        _stop.Dispose();
    }

    private sealed record Report(EncoderProgress Value);
}
