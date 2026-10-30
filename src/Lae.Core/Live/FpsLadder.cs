namespace Lae.Core;

/// <summary>
/// The adaptive frame-rate ladder: which rung the encoder is on right now, and
/// when it moves.
///
/// <para>This is the piece that makes the output look like an archive stream
/// rather than a transcoded file. When the picture cannot fit under the bitrate
/// ceiling, a 2004 server did not raise the ceiling -- it dropped frames. The
/// ladder reproduces that: <c>13, 8, 5, 3</c> by default, stepping down when the
/// rolling bitrate sits above a rung's ceiling and back up when it falls well
/// below it.</para>
///
/// <para>Two rules learned the hard way in v7. Stepping up needs a much lower
/// threshold than stepping down (the default is 75% of the ceiling), or the
/// ladder flaps every window and the picture pulses. And a step is one rung per
/// observation, never straight to the bottom: v7.9.8.2 shipped a version whose
/// quality number crossed scales, the measured bitrate came out 2.9x the
/// ceiling, and the ladder correctly -- but uselessly -- fell to the last rung on
/// the first window.</para>
/// </summary>
public sealed class FpsLadder
{
    private readonly double[] _levels;
    private readonly double[] _dropAbove;
    private readonly double[] _riseBelow;
    private int _rung = -1;

    /// <param name="levels">Frame rates, highest first.</param>
    /// <param name="ceilingKbps">The bitrate each rung may spend, same order as levels.</param>
    /// <param name="floorRatio">How far below a rung's ceiling the rate must fall to climb back.</param>
    public FpsLadder(IReadOnlyList<double> levels, IReadOnlyList<double> ceilingKbps, double floorRatio = 0.75)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(ceilingKbps);
        if (levels.Count == 0) throw new ArgumentException("the ladder needs at least one rung", nameof(levels));
        if (levels.Count != ceilingKbps.Count)
            throw new ArgumentException("every rung needs a ceiling", nameof(ceilingKbps));

        _levels = [.. levels];
        _dropAbove = [.. ceilingKbps];
        _riseBelow = [.. _dropAbove.Select(ceiling => ceiling * floorRatio)];
        _rung = 0;
    }

    /// <summary>Rungs, highest first.</summary>
    public IReadOnlyList<double> Levels => _levels;

    /// <summary>Where the ladder stands now, as a frame rate.</summary>
    public double CurrentFps => _levels[_rung];

    /// <summary>Index of the current rung; 0 is full rate.</summary>
    public int Rung => _rung;

    /// <summary>
    /// One measurement, one possible move. Returns true when the rung changed, so
    /// the caller knows to tell the pump.
    /// </summary>
    public bool Observe(double measuredKbps)
    {
        if (measuredKbps > _dropAbove[_rung] && _rung < _levels.Length - 1)
        {
            _rung++;
            return true;
        }

        if (measuredKbps < _riseBelow[_rung] && _rung > 0)
        {
            _rung--;
            return true;
        }

        return false;
    }

    /// <summary>Back to full rate, for a new run.</summary>
    public void Reset() => _rung = 0;
}

/// <summary>
/// The rolling bitrate the ladder reads.
///
/// <para>A window, not an instant: one busy second of a static shot is not a
/// reason to drop the frame rate, and a per-frame reading would react to the
/// keyframes. Seconds are stored as (timestamp, bytes) pairs and expired on
/// arrival, so memory is proportional to the window and not to the run.</para>
/// </summary>
public sealed class BitrateWindow
{
    private readonly double _seconds;
    private readonly Queue<(double At, long Bytes)> _samples = new();
    private long _totalBytes;
    private int _totalFrames;

    public BitrateWindow(double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        _seconds = seconds;
    }

    /// <summary>How much output has to accumulate before a reading means anything.</summary>
    public double WindowSeconds => _seconds;

    /// <summary>Record a frame's worth of output.</summary>
    public void Add(double now, int bytes)
    {
        _samples.Enqueue((now, bytes));
        _totalBytes += bytes;
        _totalFrames++;

        while (_samples.Count > 1 && now - _samples.Peek().At > _seconds)
            _totalBytes -= _samples.Dequeue().Bytes;
    }

    /// <summary>Bitrate over the window so far, in kbps.</summary>
    public double Kbps(double now)
    {
        double span = Math.Min(_seconds, Math.Max(0.001, now));
        return _totalBytes * 8.0 / span / 1000.0;
    }

    /// <summary>Frames seen, for the report.</summary>
    public int Frames => _totalFrames;

    public void Reset()
    {
        _samples.Clear();
        _totalBytes = 0;
        _totalFrames = 0;
    }
}
