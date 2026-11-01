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
    private readonly PipeLines _pipe;
    private Report _latest = new(new EncoderProgress());
    private EncoderProgress _current;

    public ProgressTap(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _pipe = PipeLines.Start(stream, OnLine);
    }

    /// <summary>The most recent report, or an empty one before FFmpeg has spoken.</summary>
    public EncoderProgress Current => Volatile.Read(ref _latest).Value;

    private void OnLine(string line)
    {
        if (FfmpegProgress.TryRead(line, ref _current)) Volatile.Write(ref _latest, new Report(_current));
    }

    public void Dispose() => _pipe.Dispose();

    private sealed record Report(EncoderProgress Value);
}
