using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveArchiveEncoder.Core;

/// <summary>
/// Windows multimedia timer resolution + hybrid sleep/spin wait.
/// The default Windows timer granularity is ~15.6 ms, which is far too coarse
/// for a 13 fps (76.9 ms) output slot; raising the resolution to 1 ms keeps the
/// output cadence stable without busy-waiting for the whole slot.
///
/// Reference counted (v7.9.3.1). <c>timeBeginPeriod</c> is a process-wide
/// setting, and the parallel pipeline runs one <c>Execute</c> per shard: without
/// a count, the first shard to finish called <c>timeEndPeriod</c> and handed the
/// coarse 15.6 ms timer back to every shard still encoding. The count is taken
/// under a lock because Enable/Disable are called from several shard threads at
/// once.
/// </summary>
public static class HighResolutionTimer
{
    private static readonly Lock _gate = new();
    private static int _depth;
    private static bool _periodRaised;

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    /// <summary>How many callers currently hold the fine timer (0 = not raised).</summary>
    public static int Depth
    {
        get { lock (_gate) return _depth; }
    }

    /// <summary>
    /// Take a reference to the 1 ms timer. The period is raised by the first
    /// caller only; on every other platform this just counts.
    /// </summary>
    public static void Enable()
    {
        lock (_gate)
        {
            _depth++;
            if (_depth > 1 || _periodRaised) return;
            if (!OperatingSystem.IsWindows()) return;
            if (TimeBeginPeriod(1) == 0) _periodRaised = true;
        }
    }

    /// <summary>
    /// Give back one reference. The coarse timer returns only when the last
    /// holder lets go, so a shard finishing early cannot change the pacing of
    /// the shards still running. Returns the raw winmm result so a failure is
    /// visible to the caller instead of being swallowed -- <c>timeEndPeriod</c>
    /// reports TIMERR_NOCANDO when the period was never begun, which is exactly
    /// the mistake worth surfacing.
    /// </summary>
    public static uint Disable()
    {
        lock (_gate)
        {
            if (_depth > 0) _depth--;
            if (_depth > 0 || !_periodRaised) return 0;
            _periodRaised = false;
            return TimeEndPeriod(1);
        }
    }

    /// <summary>Monotonic seconds since boot (same origin as Python's time.monotonic()).</summary>
    public static double MonotonicSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public static double SecondsSince(long originTicks) =>
        (Stopwatch.GetTimestamp() - originTicks) / (double)Stopwatch.Frequency;

    /// <summary>
    /// Block until <paramref name="targetTicks"/> is reached.
    ///
    /// Sleep-based, not spin-based. With a 1 ms timer resolution the sleep overshoot
    /// is well under 1 ms, which is negligible against a 76.9 ms (13 fps) slot, while
    /// spinning for the tail would burn a full core for the whole encode.
    /// </summary>
    public static void WaitUntil(long targetTicks)
    {
        long frequency = Stopwatch.Frequency;
        while (true)
        {
            long remaining = targetTicks - Stopwatch.GetTimestamp();
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
                // what is left, and oversleeping here would cost more than it gains.
                return;
            }
        }
    }
}
