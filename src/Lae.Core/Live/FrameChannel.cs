namespace Lae.Core;

/// <summary>
/// The frame handoff between "whatever produces frames" and the pacing loop.
///
/// <para>A bounded queue, not a single slot. The producer is paced to one frame
/// per slot, so in a healthy run the queue never holds more than one frame and
/// this costs nothing -- but a producer that gets ahead (a local file decoding
/// faster than the clock, a network source that burst, a machine that was busy
/// elsewhere for a moment) must not lose frames, because the consumer still owes
/// one slot for each of them.</para>
///
/// <para>When the queue really is full the oldest frame is dropped. That is the
/// one rule a live capture cannot avoid: the encoder is behind, and the only
/// choices are to fall further behind or to skip. Skipping keeps the output
/// aligned with the wall clock, which is the whole point of pacing it.</para>
///
/// <para>Every buffer is allocated once. A raw frame is tens to hundreds of
/// kilobytes, and allocating one per frame at 30 fps would put the encode's own
/// garbage collection into the timing it is trying to measure.</para>
/// </summary>
public sealed class FrameChannel
{
    private readonly Lock _gate = new();
    private readonly byte[][] _slots;
    private int _head;
    private int _count;
    private bool _completed;
    private long _dropped;

    public FrameChannel(int frameSize, int capacity = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        FrameSize = frameSize;
        _slots = new byte[capacity][];
        for (int i = 0; i < capacity; i++) _slots[i] = new byte[frameSize];
    }

    /// <summary>Bytes per frame; a publish of any other length is a bug, not input.</summary>
    public int FrameSize { get; }

    /// <summary>How many frames the queue can hold before it starts dropping.</summary>
    public int Capacity => _slots.Length;

    /// <summary>Frames waiting to be taken.</summary>
    public int Count
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>Frames thrown away because the consumer was behind.</summary>
    public long Dropped
    {
        get { lock (_gate) return _dropped; }
    }

    /// <summary>True once the producer has said it will publish no more.</summary>
    public bool Completed
    {
        get { lock (_gate) return _completed; }
    }

    /// <summary>Offer a frame. Drops the oldest one when the queue is full.</summary>
    public void Publish(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != FrameSize)
        {
            throw new ArgumentException(
                $"frame is {frame.Length} bytes, channel expects {FrameSize}", nameof(frame));
        }

        lock (_gate)
        {
            int tail = (_head + _count) % _slots.Length;
            frame.CopyTo(_slots[tail]);

            if (_count == _slots.Length)
            {
                _head = (_head + 1) % _slots.Length;
                _dropped++;
            }
            else
            {
                _count++;
            }
        }
    }

    /// <summary>
    /// Copy the oldest waiting frame into <paramref name="destination"/>. Returns
    /// false when there is nothing to take, which is the caller's cue to repeat
    /// the frame it already has.
    /// </summary>
    public bool TryTake(Span<byte> destination)
    {
        if (destination.Length < FrameSize)
        {
            throw new ArgumentException(
                $"destination is {destination.Length} bytes, channel needs {FrameSize}", nameof(destination));
        }

        lock (_gate)
        {
            if (_count == 0) return false;

            _slots[_head].AsSpan().CopyTo(destination);
            _head = (_head + 1) % _slots.Length;
            _count--;
            return true;
        }
    }

    /// <summary>The producer is done; the consumer may stop once the queue drains.</summary>
    public void Complete()
    {
        lock (_gate) _completed = true;
    }
}
