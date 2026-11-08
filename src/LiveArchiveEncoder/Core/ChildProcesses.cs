using System.Diagnostics;

namespace LiveArchiveEncoder.Core;

/// <summary>
/// v7.9.9.2: player processes this app started itself. A player holding the
/// output open is the common reason a re-run cannot replace it.
///
/// <para>v7.9.9.3: each entry also records WHICH file it opened. The old
/// KillAll() killed every player we had ever launched, so a lock on output A
/// closed the user's unrelated playback of B and C. Now only the holders of the
/// reported path are ended, and WaitForExit runs outside the gate so a new
/// Track() is never blocked by a slow kill.</para>
/// </summary>
public static class ChildProcesses
{
    private static readonly Lock Gate = new();
    private static readonly List<(Process Process, string File)> Tracked = [];

    /// <summary>Register a player started for <paramref name="openedFile"/>.</summary>
    public static void Track(Process? process, string openedFile)
    {
        if (process is null) return;
        lock (Gate)
        {
            // A long CUI session may play every result. Drop exited entries so
            // the list (and their OS handles) cannot grow for the whole session.
            for (int i = Tracked.Count - 1; i >= 0; i--)
            {
                (Process old, _) = Tracked[i];
                if (!Exited(old)) continue;
                Tracked.RemoveAt(i);
                old.Dispose();
            }
            Tracked.Add((process, Path.GetFullPath(openedFile)));
        }
    }

    /// <summary>
    /// End players we started for this exact path; returns how many were killed.
    /// A null path ends nothing (there is nothing to match).
    /// </summary>
    public static int KillAll(string? heldPath = null)
    {
        if (heldPath is null) return 0;
        string wanted = Path.GetFullPath(heldPath);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var victims = new List<Process>();
        lock (Gate)
        {
            for (int i = Tracked.Count - 1; i >= 0; i--)
            {
                (Process process, string file) = Tracked[i];
                if (!string.Equals(file, wanted, comparison)) continue;
                Tracked.RemoveAt(i);
                victims.Add(process);
            }
        }

        int ended = 0;
        foreach (Process process in victims)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);      // deliberately outside Gate
                    ended++;
                }
            }
            catch (Exception)
            {
                // Already gone, or no permission to signal it. Either way the
                // caller re-probes the file and reports the real state.
            }
            finally { process.Dispose(); }
        }
        return ended;
    }

    private static bool Exited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception) { return true; }
    }
}
