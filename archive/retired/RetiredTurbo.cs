// Retired members, kept for reference. NOT COMPILED: this file lives outside
// every project on purpose (see archive/README.md).
//
// How each one was found: the v7.9 removal of the Turbo mode cross-referenced
// every declaration in src/ against src/, tests/ and the e2e harnesses; what
// is below had exactly one caller each -- the Turbo code path itself.
//
// Restoring one is a copy-paste into the file it came from.

namespace LiveArchiveEncoder.Archive;

// ---------------------------------------------------------------------------
// Encoding/RealtimeOptions.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Retired in v7.9.
///
/// <c>--turbo</c> dropped the deliberate 1/fps wall-clock pacing and ran as fast
/// as the decode/encode pipeline allowed (~58x on a 20 s 320x240 source).
///
/// Removed rather than kept because it fought the point of the tool: a live
/// recording simulation. The fresh/repeat mix and the adaptive-FPS hysteresis
/// are wall-clock driven, so a Turbo run produced an archive that did not match
/// what a live recording would have looked like -- and the mode was deprecated
/// in v7.7 with nobody asking for it to stay.
/// </summary>
internal static class RetiredRealtimeOptions
{
    public bool Turbo { get; init; }
}

// ---------------------------------------------------------------------------
// Encoding/RealtimeEncoder.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Retired in v7.9.
///
/// The three Turbo branches of the single-pass slot loop, verbatim. With the
/// property gone they collapse to the wall-clock path that remains:
///
///   1. capture thread -- publish, then advance <c>nextDeadline</c> by
///      <c>interval * frequency</c> and sleep until it (reset when overdue,
///      which is what Python did);
///   2. slot loop -- <c>nextSlot += slotIntervalTicks</c>, sleep until it;
///   3. hysteresis clock -- <c>now = HighResolutionTimer.MonotonicSeconds()</c>,
///      because the 0.18/0.30/0.75/0.80 s constants are seconds of programme
///      and the realtime pacing makes wall clock and programme time agree;
///   4. pacing -- no <c>WaitForFrame</c> call: the decoder pipe and the bounded
///      pool are the back-pressure, and the slot deadline is the wait.
/// </summary>
internal static class RetiredTurboSlotLoop
{
    // if (!o.Turbo)
    // {
    //     nextDeadline += (long)(interval * frequency);
    //     long nowTicks = Stopwatch.GetTimestamp();
    //     if (nextDeadline > nowTicks) HighResolutionTimer.WaitUntil(nextDeadline);
    //     else nextDeadline = nowTicks;
    // }
    // // Turbo: no wall-clock pacing. The decoder pipe and the bounded pool are
    // // the only back-pressure, so the capture side runs at decode speed.

    // long nowSlotTicks = Stopwatch.GetTimestamp();
    // if (o.Turbo)
    // {
    //     nextSlot = nowSlotTicks;
    // }
    // else
    // {
    //     nextSlot += slotIntervalTicks;
    //     if (nextSlot > nowSlotTicks) HighResolutionTimer.WaitUntil(nextSlot);
    //     else nextSlot = nowSlotTicks;
    // }

    // double now = o.Turbo ? slots / o.Fps : HighResolutionTimer.MonotonicSeconds();

    // if (o.Turbo && emitFresh && pool.ReadyCount == 0 && !captureState.Done)
    // {
    //     pool.WaitForFrame(2000);
    // }
}

// ---------------------------------------------------------------------------
// Frames/FrameBufferPool.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Retired in v7.9.
///
/// A data-driven wait for the capture thread, only ever called from the Turbo
/// branch above. Without wall-clock pacing the consumer would otherwise outrun
/// the decoder and every slot would resolve to "reuse previous", collapsing the
/// video to a still.
///
/// <see cref="FrameBufferPool.Close"/> went with it: it existed to wake this
/// wait up at EOF, and with no waiter left the pulse had nothing to wake.
/// </summary>
internal static class RetiredFrameBufferPool
{
    public bool WaitForFrame(int timeoutMs)
    {
        lock (this)
        {
            // if (_readyCount > 0) return true;
            // if (_closed || timeoutMs <= 0) return false;
            // Monitor.Wait(_sync, timeoutMs);
            // return _readyCount > 0;
            return false;
        }
    }

    public void Close()
    {
        lock (this)
        {
            // _closed = true;
            // Monitor.PulseAll(_sync);
        }
    }
}

// ---------------------------------------------------------------------------
// UI/CliOptions.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Retired in v7.9.
///
/// The <c>--turbo</c> parse case, the <c>Turbo</c> property, the usage row and
/// the completion flag. All four are gone: an old script that still passes the
/// flag now gets "unrecognized argument: --turbo" (rc=2), which is asserted in
/// <c>CliParserTests.TurboIsGone</c> and the e2e <c>turbo_gone</c> check.
/// </summary>
internal static class RetiredTurboCli
{
    // case "--turbo": turbo = true; break;
    // ("--turbo", "", "DEPRECATED: drop the 1/fps realtime pacing (much faster; output differs)"),
}

// ---------------------------------------------------------------------------
// Localization/Strings.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Retired in v7.9.
///
/// Seven keys in each language: <c>menu.turbo</c>, <c>turbo.on</c>,
/// <c>turbo.off</c>, <c>turbo.enabled</c>, <c>turbo.disabled</c>,
/// <c>turbo.deprecated</c>, <c>turbo.menu.deprecated</c>. The last two were
/// already unreferenced once the Program.cs warning and the Report.cs dump line
/// were removed; <c>LocalizationTests</c> only requires that both languages
/// carry the same keys, so deleting all of them keeps the suites green.
/// </summary>
internal static class RetiredTurboStrings
{
    // ["turbo.on"] = "ON (no realtime pacing)";
    // ["turbo.off"] = "off (realtime)";
    // ["turbo.menu.deprecated"] = "Turbo mode (deprecated)";
    // ... and the zh equivalents.
}
