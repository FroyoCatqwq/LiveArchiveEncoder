namespace LiveArchiveEncoder.Frames;

/// <summary>
/// Bounded frame store with pre-allocated buffers and explicit ownership.
///
/// Semantics match the Python `collections.deque(maxlen=buffer_frames)`:
///   * the capture side appends frames and silently drops the oldest when full;
///   * the consumer takes the newest frame and drops everything else.
///
/// Unlike ArrayPool rentals, a buffer is only reusable once the consumer
/// releases it, so a frame still referenced as "previous" can never be
/// overwritten by the capture thread. No per-frame allocation happens after
/// construction.
/// </summary>
public sealed class FrameBufferPool
{
    private readonly byte[][] _buffers;
    private readonly int[] _free;
    private int _freeCount;
    private readonly int[] _readyIndex;
    private readonly long[] _readySeq;
    private readonly int _readyCapacity;
    private int _readyHead;
    private int _readyCount;
    private readonly object _sync = new();
    private bool _closed;

    public int FrameSize { get; }

    public FrameBufferPool(int capacity, int frameSize)
    {
        FrameSize = frameSize;
        _readyCapacity = Math.Max(1, capacity);

        // +2 slots: one held by the capture thread, one held as "previous".
        int slots = _readyCapacity + 2;
        _buffers = new byte[slots][];
        for (int i = 0; i < slots; i++) _buffers[i] = new byte[frameSize];

        _free = new int[slots];
        for (int i = 0; i < slots; i++) _free[i] = slots - 1 - i;
        _freeCount = slots;

        _readyIndex = new int[_readyCapacity];
        _readySeq = new long[_readyCapacity];
    }

    public byte[] this[int index] => _buffers[index];

    public bool TryRent(out int index)
    {
        lock (_sync)
        {
            if (_freeCount == 0)
            {
                index = -1;
                return false;
            }
            index = _free[--_freeCount];
            return true;
        }
    }

    /// <summary>Publish a filled buffer; evicts the oldest frame when full.</summary>
    public void Publish(int index, long sequence)
    {
        int evicted = -1;
        lock (_sync)
        {
            if (_readyCount == _readyCapacity)
            {
                evicted = _readyIndex[_readyHead];
                _readyHead = (_readyHead + 1) % _readyCapacity;
                _readyCount--;
            }
            int tail = (_readyHead + _readyCount) % _readyCapacity;
            _readyIndex[tail] = index;
            _readySeq[tail] = sequence;
            _readyCount++;
            Monitor.Pulse(_sync);
        }
        if (evicted >= 0) Release(evicted);
    }

    /// <summary>
    /// Signal that no further frames will ever be published, so that anyone
    /// blocked in <see cref="WaitForFrame"/> wakes up instead of hanging.
    /// </summary>
    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>
    /// Block until a frame is ready, the pool is closed, or the timeout expires.
    ///
    /// Turbo mode replaces the wall-clock slot deadline with this data-driven
    /// wait. Without it the consumer loop outruns the capture thread and every
    /// slot degenerates into a duplicate of the previous frame, which would
    /// silently produce a near-empty video.
    /// </summary>
    public bool WaitForFrame(int timeoutMs)
    {
        lock (_sync)
        {
            if (_readyCount > 0) return true;
            if (_closed || timeoutMs <= 0) return false;
            Monitor.Wait(_sync, timeoutMs);
            return _readyCount > 0;
        }
    }

    /// <summary>
    /// Take the newest frame and release every older one, mirroring
    /// `frame = captured[-1]; captured.clear()`.
    /// </summary>
    public bool TryTakeLatest(out int index, out long sequence)
    {
        index = -1;
        sequence = 0;
        int[] releases = Array.Empty<int>();

        lock (_sync)
        {
            if (_readyCount == 0) return false;

            int newest = (_readyHead + _readyCount - 1) % _readyCapacity;
            index = _readyIndex[newest];
            sequence = _readySeq[newest];

            if (_readyCount > 1)
            {
                releases = new int[_readyCount - 1];
                for (int i = 0; i < releases.Length; i++)
                {
                    releases[i] = _readyIndex[(_readyHead + i) % _readyCapacity];
                }
            }
            _readyCount = 0;
        }

        foreach (int slot in releases) Release(slot);
        return true;
    }

    public int ReadyCount
    {
        get { lock (_sync) return _readyCount; }
    }

    public void Release(int index)
    {
        if (index < 0) return;
        lock (_sync) _free[_freeCount++] = index;
    }
}
