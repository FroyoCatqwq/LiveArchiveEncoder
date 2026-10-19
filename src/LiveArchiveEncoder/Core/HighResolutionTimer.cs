using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveArchiveEncoder.Core;

/// <summary>
/// Windows multimedia timer resolution + hybrid sleep/spin wait.
/// The default Windows timer granularity is ~15.6 ms, which is far too coarse
/// for a 13 fps (76.9 ms) output slot; raising the resolution to 1 ms keeps the
/// output cadence stable without busy-waiting for the whole slot.
/// </summary>
public static class HighResolutionTimer
{
    private static bool _enabled;

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    public static void Enable()
    {
        if (_enabled || !OperatingSystem.IsWindows()) return;
        if (TimeBeginPeriod(1) == 0) _enabled = true;
    }

    /// <summary>
    /// Give the coarse timer back. Returns the raw winmm result so a failure is
    /// visible to the caller instead of being swallowed -- <c>timeEndPeriod</c>
    /// reports TIMERR_NOCANDO when the period was never begun, which is exactly
    /// the mistake worth surfacing.
    /// </summary>
    public static uint Disable()
    {
        if (!_enabled)
        {
            _enabled = false;
            return 0;
        }
        uint result = TimeEndPeriod(1);
        _enabled = false;
        return result;
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
