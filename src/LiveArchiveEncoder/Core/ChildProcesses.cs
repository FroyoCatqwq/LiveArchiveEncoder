using System.Diagnostics;

namespace LiveArchiveEncoder.Core;

/// <summary>
/// v7.9.9.2: player processes this app started itself. A player holding the
/// output open is the common reason a re-run cannot replace it; ending exactly
/// those is both safe and sufficient. Processes the user launched from
/// elsewhere are NEVER touched -- killing an arbitrary editor/player would be
/// far worse than asking the user to close it.
/// </summary>
public static class ChildProcesses
{
    private static readonly Lock Gate = new();
    private static readonly List<Process> Tracked = [];

    /// <summary>Register a process started by <c>Player.Launch</c>.</summary>
    public static void Track(Process? process)
    {
        if (process is null) return;
        lock (Gate)
        {
            // A long CUI session may play every result. Drop exited entries so
            // the list (and their OS handles) cannot grow for the whole session.
            for (int i = Tracked.Count - 1; i >= 0; i--)
            {
                Process old = Tracked[i];
                if (!Exited(old)) continue;
                Tracked.RemoveAt(i);
                old.Dispose();
            }
            Tracked.Add(process);
        }
    }

    /// <summary>End players we started; returns how many were actually killed.</summary>
    public static int KillAll()
    {
        lock (Gate)
        {
            int ended = 0;
            foreach (Process process in Tracked)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(2000);
                        ended++;
                    }
                }
                catch (Exception)
                {
                    // Already gone, or no permission to signal it. Either way
                    // the caller re-probes the file and reports the real state.
                }
                finally { process.Dispose(); }
            }
            Tracked.Clear();
            return ended;
        }
    }

    private static bool Exited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception) { return true; }
    }
}
