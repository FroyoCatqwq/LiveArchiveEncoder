using System.Diagnostics;

namespace LiveArchiveEncoder.RateControl;

/// <summary>
/// Thread-safe rolling monitor for FFmpeg -progress output.
///
/// Rolling aggregates (window_bytes / window_media_us) are updated only when a
/// sample enters or leaves the window, so no slot ever rescans the whole window.
/// </summary>
public sealed class OutputMonitor
{
    private const int SampleCapacity = 1024;
    private const int DiagnosticCapacity = 64;

    private readonly double _windowSeconds;
    // .NET 9+ System.Threading.Lock: the same mutual exclusion as lock(object)
    // with a smaller, non-inflatable object header and no risk of a stray
    // Monitor.Enter/Exit pair from outside this class.
    private readonly Lock _sync = new();

    private readonly double[] _sampleTime = new double[SampleCapacity];
    private readonly long[] _sampleBytes = new long[SampleCapacity];
    private readonly long[] _sampleMediaUs = new long[SampleCapacity];
    private readonly bool[] _sampleHasMedia = new bool[SampleCapacity];
    private int _sampleHead;
    private int _sampleCount;

    private long _windowBytes;
    private long _windowMediaUs;
    private long _totalSize;
    private long? _lastOutTimeUs;

    private readonly Queue<string> _diagnostics = new();

    public bool ProgressEnded { get; set; }

    public OutputMonitor(double windowSeconds)
    {
        _windowSeconds = Math.Max(0.1, windowSeconds);
    }

    public void AddProgress(long totalSize, long outTimeUs)
    {
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        lock (_sync)
        {
            long delta;
            if (totalSize >= _totalSize)
            {
                delta = totalSize - _totalSize;
            }
            else
            {
                delta = 0;
                _sampleCount = 0;
                _windowBytes = 0;
                _windowMediaUs = 0;
                _lastOutTimeUs = null;
            }
            _totalSize = totalSize;

            long? mediaDelta = null;
            if (_lastOutTimeUs is not null && outTimeUs >= _lastOutTimeUs.Value)
            {
                mediaDelta = outTimeUs - _lastOutTimeUs.Value;
            }
            _lastOutTimeUs = outTimeUs;

            if (delta > 0)
            {
                if (_sampleCount == SampleCapacity)
                {
                    // Extremely unlikely: drop the oldest to stay bounded.
                    RemoveOldest();
                }
                int tail = (_sampleHead + _sampleCount) % SampleCapacity;
                _sampleTime[tail] = now;
                _sampleBytes[tail] = delta;
                _sampleHasMedia[tail] = mediaDelta is not null;
                _sampleMediaUs[tail] = mediaDelta ?? 0;
                _sampleCount++;

                _windowBytes += delta;
                if (mediaDelta is not null && mediaDelta.Value > 0) _windowMediaUs += mediaDelta.Value;
            }

            double cutoff = now - _windowSeconds;
            while (_sampleCount > 0 && _sampleTime[_sampleHead] < cutoff)
            {
                RemoveOldest();
            }
        }
    }

    private void RemoveOldest()
    {
        _windowBytes -= _sampleBytes[_sampleHead];
        if (_sampleHasMedia[_sampleHead] && _sampleMediaUs[_sampleHead] > 0)
        {
            _windowMediaUs -= _sampleMediaUs[_sampleHead];
        }
        _sampleHead = (_sampleHead + 1) % SampleCapacity;
        _sampleCount--;
    }

    public double RateKbps()
    {
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        lock (_sync)
        {
            if (_sampleCount == 0) return 0.0;
            if (_windowBytes <= 0) return 0.0;

            if (_windowMediaUs > 0)
            {
                return _windowBytes * 8.0 / (_windowMediaUs / 1_000_000.0) / 1000.0;
            }

            double span = Math.Max(0.05, now - _sampleTime[_sampleHead]);
            return _windowBytes * 8.0 / span / 1000.0;
        }
    }

    public long TotalBytes()
    {
        lock (_sync) return _totalSize;
    }

    public void AddDiagnostic(string line)
    {
        lock (_sync)
        {
            _diagnostics.Enqueue(line);
            while (_diagnostics.Count > DiagnosticCapacity) _diagnostics.Dequeue();
        }
    }

    /// <summary>Last up to <paramref name="count"/> diagnostic lines.</summary>
    public List<string> TailDiagnostics(int count)
    {
        if (count <= 0) return [];
        lock (_sync)
        {
            if (_diagnostics.Count <= count) return [.. _diagnostics];
            return [.. _diagnostics.Skip(_diagnostics.Count - count)];
        }
    }
}
