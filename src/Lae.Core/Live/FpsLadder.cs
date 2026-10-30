using Lae.Core.Settings;

namespace Lae.Core;

/// <summary>
/// The adaptive frame-rate ladder: which rung the encoder is on right now, and
/// when it moves.
///
/// <para>This is the piece that makes the output look like an archive stream
/// rather than a transcoded file. When the picture cannot fit under the bitrate
/// ceiling, a 2004 server did not raise the ceiling -- it dropped frames. The
/// ladder reproduces that: <c>13, 8, 5, 3</c> by default, stepping down when the
/// rolling bitrate sits above the next rung's threshold and back up when it
/// falls well below the one it is on.</para>
///
/// <para>Two rules learned the hard way in v7. Stepping up needs a much lower
/// threshold than stepping down (the default is the configured recovery rate,
/// which is 25% below the ceiling), or the ladder flaps every window and the
/// picture pulses. And a step is one rung per observation, never straight to the
/// bottom: v7.9.8.2 shipped a version whose quality number crossed scales, the
/// measured bitrate came out 2.9x the ceiling, and the ladder correctly -- but
/// uselessly -- fell to the last rung on the first window.</para>
///
/// <para>Thresholds belong to rungs, not to positions. v7 stored them keyed by
/// frame rate for exactly that reason: editing the ladder then cannot shift a
/// threshold onto a rung the user never meant.</para>
/// </summary>
public sealed class FpsLadder
{
    private readonly FpsRung[] _rungs;
    private int _rung;
    private double _nextMoveAllowed = double.NegativeInfinity;

    /// <param name="rungs">Rungs, highest frame rate first.</param>
    /// <param name="dropHoldSeconds">
    /// How long the ladder must sit still after descending. v7 used 0.18 s when
    /// the rate was 25% over the ceiling and 0.30 s otherwise; one value is
    /// enough, and it is the thing that keeps a burst from walking the ladder to
    /// the floor in a quarter of a second -- which is exactly what v8 did on its
    /// first real run before this hold existed (13 > 8 > 5 > 3 inside 0.25 s).
    /// </param>
    /// <param name="riseHoldSeconds">The same for climbing back, which v7 held for 0.75 s.</param>
    public FpsLadder(
        IReadOnlyList<FpsRung> rungs,
        double dropHoldSeconds = 0.20,
        double riseHoldSeconds = 0.75)
    {
        ArgumentNullException.ThrowIfNull(rungs);
        if (rungs.Count == 0) throw new ArgumentException("the ladder needs at least one rung", nameof(rungs));
        ArgumentOutOfRangeException.ThrowIfNegative(dropHoldSeconds);
        ArgumentOutOfRangeException.ThrowIfNegative(riseHoldSeconds);

        _rungs = [.. rungs];
        DropHoldSeconds = dropHoldSeconds;
        RiseHoldSeconds = riseHoldSeconds;
        _rung = 0;
    }

    /// <summary>Rungs, highest frame rate first.</summary>
    public IReadOnlyList<FpsRung> Rungs => _rungs;

    /// <summary>Where the ladder stands now, as a frame rate.</summary>
    public double CurrentFps => _rungs[_rung].Fps;

    /// <summary>Index of the current rung; 0 is full rate.</summary>
    public int Rung => _rung;

    /// <summary>True when there is nowhere to go -- a single-rung ladder.</summary>
    public bool IsFlat => _rungs.Length == 1;

    /// <summary>How long the ladder holds still after descending.</summary>
    public double DropHoldSeconds { get; }

    /// <summary>How long it holds still after climbing.</summary>
    public double RiseHoldSeconds { get; }

    /// <summary>
    /// One measurement, one possible move, at a point in programme time.
    /// Returns true when the rung changed, so the caller knows to report it.
    ///
    /// <para>The threshold that matters when descending is the next rung's, not
    /// this one's: a rung's number says where you land, which is the same
    /// direction v7 stored it in and the same direction the settings file spells
    /// it. Descend first, because a rate above the next threshold can never also
    /// be below this rung's recovery point.</para>
    /// </summary>
    public bool Observe(double measuredKbps, double seconds)
    {
        if (seconds < _nextMoveAllowed) return false;

        if (_rung < _rungs.Length - 1 && measuredKbps >= _rungs[_rung + 1].DropAboveKbps)
        {
            _rung++;
            _nextMoveAllowed = seconds + DropHoldSeconds;
            return true;
        }

        if (_rung > 0 && measuredKbps <= _rungs[_rung].RiseBelowKbps)
        {
            _rung--;
            _nextMoveAllowed = seconds + RiseHoldSeconds;
            return true;
        }

        return false;
    }

    /// <summary>Back to full rate, for a new run.</summary>
    public void Reset()
    {
        _rung = 0;
        _nextMoveAllowed = double.NegativeInfinity;
    }
}

/// <summary>
/// Turns the stored settings into a ladder, and decides whether there is one at
/// all.
///
/// <para>The thresholds themselves are <see cref="EncoderMath.BuildLadder"/>'s
/// job -- v7.6's multipliers of the ceiling, keyed by frame rate -- and this type
/// only decides when there is nothing to adapt. Keeping the numbers in one place
/// is the whole reason the side-by-side comparison with v7 can be a comparison
/// of the same behaviour.</para>
/// </summary>
public static class LadderPolicy
{
    /// <summary>
    /// The ladder for these settings, or null when there is nothing to adapt --
    /// one rung, or no ceiling to stay under.
    /// </summary>
    public static FpsLadder? FromDefaults(EncodeDefaults encode)
    {
        ArgumentNullException.ThrowIfNull(encode);
        if (encode.LimitKbps <= 0) return null;

        // SelectLevels drops rungs above the base rate and de-duplicates, so a
        // hand-edited ladder cannot produce a rung that is never reachable.
        if (EncoderMath.SelectLevels(encode.Fps, encode.FpsLevels).Count < 2) return null;

        List<FpsRungSetting> configured =
        [
            .. encode.FpsThresholds
                .Where(t => t is not null)
                .Select(t => new FpsRungSetting
                {
                    Fps = t.Fps,
                    DropAboveKbps = t.DropAboveKbps,
                    RiseBelowKbps = t.RiseBelowKbps,
                }),
        ];

        return new FpsLadder(EncoderMath.BuildLadder(
            encode.Fps, encode.FpsLevels, configured, encode.LimitKbps, encode.RecoverKbps));
    }
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

    /// <summary>
    /// Bitrate over the window so far, in kbps.
    ///
    /// <para>Divided by the window length, or by the programme time elapsed while
    /// the window is still filling -- never by the span between the oldest and
    /// newest sample, which is degenerate for a single sample and would turn one
    /// burst of bytes into an astronomical rate.</para>
    ///
    /// <para>What this does not smooth is the muxer's own reporting: FFmpeg's
    /// <c>total_size</c> moves in chunks, so a window can briefly hold two seconds
    /// of bytes in one report. That is what the ladder's hold is for -- one move
    /// per 0.2 s -- and it is the same shape v7 settled on.</para>
    /// </summary>
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
