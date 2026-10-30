namespace Lae.Core;

/// <summary>
/// Millisecond timer resolution plus the sleep/spin hybrid that the pacing loop
/// waits with.
///
/// <para>The default Windows granularity is ~15.6 ms, which is far too coarse
/// against a 13 fps slot (76.9 ms) or a 30 fps one (33.3 ms): sleeping for "one
/// slot" could overshoot by half a slot and the output cadence would drift
/// against the wall clock. Raising the period to 1 ms costs a negligible amount
/// of power and makes the sleep overshoot under a millisecond.</para>
///
/// <para>Reference counted, because <c>timeBeginPeriod</c> is process-wide: v7's
/// parallel pipeline ran one pump per shard, and without the count the first
/// shard to finish handed the coarse timer back to every shard still
/// encoding.</para>
/// </summary>
public static class FineTimer
{
    private static readonly Lock _gate = new();
    private static int _depth;
    private static bool _raised;

    /// <summary>How many callers currently hold the fine timer (0 = not raised).</summary>
    public static int Depth
    {
        get { lock (_gate) return _depth; }
    }

    /// <summary>
    /// Take a reference to the 1 ms timer. Only the first caller raises it; on
    /// every other platform this just counts.
    /// </summary>
    public static void Enable()
    {
        lock (_gate)
        {
            _depth++;
            if (_depth > 1 || _raised) return;
            if (!OperatingSystem.IsWindows()) return;
            if (Native.TimeBeginPeriod(1) == 0) _raised = true;
        }
    }

    /// <summary>
    /// Give back one reference. The coarse timer returns only when the last
    /// holder lets go, so a shard finishing early cannot change the pacing of
    /// the shards still running.
    /// </summary>
    public static void Disable()
    {
        lock (_gate)
        {
            if (_depth > 0) _depth--;
            if (_depth > 0 || !_raised) return;
            _raised = false;
            _ = Native.TimeEndPeriod(1);
        }
    }

    /// <summary>Monotonic seconds since boot.</summary>
    public static double MonotonicSeconds() => System.Diagnostics.Stopwatch.GetTimestamp()
        / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Seconds elapsed since a <c>Stopwatch.GetTimestamp</c> reading.</summary>
    public static double SecondsSince(long originTicks) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - originTicks)
        / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Frame interval in <c>Stopwatch</c> ticks, for a given rate.</summary>
    public static long IntervalTicks(double perSecond) =>
        (long)Math.Round(System.Diagnostics.Stopwatch.Frequency / perSecond);

    /// <summary>
    /// Block until <paramref name="targetTicks"/> is reached.
    ///
    /// <para>Sleep based, not spin based: with a 1 ms resolution the overshoot is
    /// well under a millisecond, which is nothing against a 30 ms slot, while
    /// spinning for the tail would burn a core for the whole encode.</para>
    /// </summary>
    public static void WaitUntil(long targetTicks)
    {
        long frequency = System.Diagnostics.Stopwatch.Frequency;
        while (true)
        {
            long remaining = targetTicks - System.Diagnostics.Stopwatch.GetTimestamp();
            if (remaining <= 0) return;

            double remainingMs = remaining * 1000.0 / frequency;
            if (remainingMs >= 1.5)
            {
                Thread.Sleep((int)(remainingMs - 1));
            }
            else if (remainingMs >= 0.5)
            {
                Thread.Sleep(1);
            }
            else
            {
                // Sub-millisecond remainder: sleep granularity is coarser than
                // what is left, and oversleeping would cost more than it gains.
                return;
            }
        }
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        internal static extern uint TimeBeginPeriod(uint period);

        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        internal static extern uint TimeEndPeriod(uint period);
    }
}
