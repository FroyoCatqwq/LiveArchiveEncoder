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
    // v7.9.8.6: real ownership. The old double-release guard was
    // `_freeCount >= _free.Length`, which is only true when the whole pool is
    // idle -- exactly the state in which a duplicate release is harmless. With any
    // buffer lent out, a second Release of the same index used to enqueue it
    // twice, and two consumers would then write the same frame buffer.
    private readonly bool[] _borrowed;
    private readonly int[] _readyIndex;
    private readonly long[] _readySeq;
    private readonly int _readyCapacity;
    private int _readyHead;
    private int _readyCount;
    // Scratch for TryTakeLatest. The class promises no allocation per frame; this
    // array is sized once, and only the single consumer thread touches it.
    private readonly int[] _releases;
    // System.Threading.Lock: this is the one gate taken on every frame (rent,
    // publish, take), and with the stray Monitor.Pulse gone (see Publish) there
    // is nothing left that needs a monitor.
    private readonly Lock _sync = new();

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
        _borrowed = new bool[slots];

        _readyIndex = new int[_readyCapacity];
        _readySeq = new long[_readyCapacity];
        _releases = new int[Math.Max(1, _readyCapacity)];
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
            _borrowed[index] = true;
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
            // v7.9.8.1: this used to be Monitor.Pulse(_sync). Nothing in the
            // process ever waited on this pool -- the consumer polls
            // TryTakeLatest from the pacing loop -- so the pulse woke nobody and
            // only made the gate un-convertible to System.Threading.Lock (which
            // has no Pulse, and would be boxed by Monitor.Pulse into signalling
            // a monitor nobody holds, CS9216). Removed, and the gate is a Lock.
        }
        if (evicted >= 0) Release(evicted);
    }

    /// <summary>
    /// Take the newest frame and release every older one, mirroring
    /// `frame = captured[-1]; captured.clear()`.
    /// </summary>
    public bool TryTakeLatest(out int index, out long sequence)
    {
        index = -1;
        sequence = 0;
        int releases = 0;

        lock (_sync)
        {
            if (_readyCount == 0) return false;

            int newest = (_readyHead + _readyCount - 1) % _readyCapacity;
            index = _readyIndex[newest];
            sequence = _readySeq[newest];

            for (int i = 0; i < _readyCount - 1; i++)
            {
                _releases[releases++] = _readyIndex[(_readyHead + i) % _readyCapacity];
            }
            _readyCount = 0;
        }

        // Outside the gate on purpose: System.Threading.Lock is not reentrant, so
        // Release cannot be called from inside this one. TryTakeLatest belongs to
        // the single pacing thread, so the scratch array cannot be clobbered.
        for (int i = 0; i < releases; i++) Release(_releases[i]);
        return true;
    }

    public int ReadyCount
    {
        get { lock (_sync) return _readyCount; }
    }

    public void Release(int index)
    {
        if (index < 0) return;
        lock (_sync)
        {
            // v7.9.8.6: only a buffer that is actually lent out can be returned.
            // The previous check compared the free count against the pool size,
            // which only caught the harmless case; a real duplicate release used to
            // put the same slot in the free list twice and hand it to two writers,
            // which corrupts the picture silently instead of throwing.
            if (!_borrowed[index]) return;
            _borrowed[index] = false;
            if (_freeCount >= _free.Length) return;
            _free[_freeCount++] = index;
        }
    }
}
