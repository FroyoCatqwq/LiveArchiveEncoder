using LiveArchiveEncoder.Diagnostics;

namespace LiveArchiveEncoder.Core;

/// <summary>
/// Write an encode to a sibling temporary, then put it in place under its
/// real name — and never the other way round.
///
/// <para>FFmpeg's <c>-y</c> truncates its destination the moment it opens it,
/// so encoding straight into the requested name has two failure modes that
/// matter for an archival tool: a cancelled or failed run destroys the good
/// copy that was already there, and it leaves a short file that looks finished
/// to everything downstream (the shell's "play the result", a later remux, the
/// user's own file manager).</para>
///
/// <para>The temporary keeps the real extension (<c>clip.part.mp4</c>) so
/// FFmpeg, ffprobe and the extension-driven container logic keep seeing exactly
/// what they saw before; only the name says "not finished yet".</para>
/// </summary>
public static class OutputFile
{
    /// <summary>
    /// The sibling this encode writes to before it is put in place.
    ///
    /// <para>v7.9.8.10: the name carries a per-run tag. A fixed
    /// <c>clip.part.mp4</c> could BE a user's finished file (a manual resume),
    /// and two outputs differing only in case share the same part name on
    /// Windows -- FFmpeg's <c>-y</c> would then truncate one run's product for
    /// another. The name still ends in the real extension, because FFmpeg picks
    /// the muxer from it.</para>
    /// </summary>
    public static FileInfo PartPath(FileInfo final)
    {
        string dir = Path.GetDirectoryName(final.FullName) ?? "";
        string stem = Path.GetFileNameWithoutExtension(final.Name);
        string tag = $"{Environment.ProcessId:x}{Guid.NewGuid().ToString("N")[..8]}";
        return new(Path.Combine(dir, $"{stem}.{tag}.part{final.Extension}"));
    }

    /// <summary>
    /// v7.9.9.2: check BEFORE a long encode that the destination can actually
    /// be replaced. Until now a player/AV scanner holding the target was only
    /// discovered by <see cref="Promote"/> after the whole realtime pass, and a
    /// stale <c>.part</c> from a crashed run was never looked at at all.
    ///
    /// <para>A directory in the way is deliberately NOT rejected here:
    /// v7.9.8.10's "keep the finished part" path owns that case and the e2e
    /// suite pins its output.</para>
    /// </summary>
    /// <summary>Type-init timestamp: a .part created after this belongs to a live run.</summary>
    private static readonly DateTime StartedUtc = DateTime.UtcNow;

    public static (bool Ok, string? Error) EnsureTargetAvailable(FileInfo final)
    {
        string dir = Path.GetDirectoryName(final.FullName) ?? ".";
        if (Directory.Exists(final.FullName)) return (true, null);

        if (File.Exists(final.FullName))
        {
            try
            {
                using var probe = new FileStream(final.FullName, FileMode.Open,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (UnauthorizedAccessException)
            {
                // Killing a player cannot fix an ACL; report it immediately.
                return (false, $"output is read-only or permission denied: {final.FullName}");
            }
            catch (IOException)
            {
                // v7.9.9.3: close only the players WE started FOR THIS FILE.
                // The old KillAll() also closed unrelated playback of B and C,
                // and a stale .part could trigger it for no reason at all.
                ChildProcesses.KillAll(final.FullName);
                try
                {
                    using var probe = new FileStream(final.FullName, FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None);
                }
                catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
                {
                    return (false, $"output is in use by another program: {final.FullName}; "
                        + "close the player (or pick another name) before encoding");
                }
            }
        }

        // v7.9.9.3: glob-filtered scan (no full-directory walk), and ONLY
        // leftovers from an EARLIER process that are not locked. A part from a
        // concurrent encode is created after we started; deleting it on POSIX /
        // UNC succeeds and the other FFmpeg silently loses its output.
        string prefix = Path.GetFileNameWithoutExtension(final.Name) + ".";
        string marker = ".part" + final.Extension;
        try
        {
            foreach (string path in Directory.EnumerateFiles(dir, "*" + marker))
            {
                string name = Path.GetFileName(path);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                if (File.GetCreationTimeUtc(path) >= StartedUtc) continue;
                try
                {
                    // Windows: a live FFmpeg holds the part open. POSIX has no
                    // mandatory locks, but the creation-time guard covers it.
                    using var probe = new FileStream(path, FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None);
                }
                catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
                { continue; }

                try { File.Delete(path); }
                catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
                {
                    return (false, $"stale part file is in use: {path}; "
                        + "close the program holding it before encoding");
                }
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException)
        {
            // An unreadable directory will be reported by the encoder anyway.
        }
        return (true, null);
    }

    /// <summary>
    /// Move a finished part file onto its real name, replacing an older copy.
    /// Both names are in the same directory, so this is a rename, not a copy.
    ///
    /// <para>v7.9.8.12: a player holding the destination (ffplay left open from
    /// an earlier run) makes <c>File.Move(overwrite)</c> fail with a sharing
    /// violation and throw away a perfectly good long encode. Retry briefly,
    /// then save under the first free <c>name (2).ext</c>. A directory standing
    /// in for the output is not a sharing conflict, so that case still reaches
    /// <see cref="Keep"/> unchanged.</para>
    /// </summary>
    /// <returns>The name the finished file actually landed under.</returns>
    public static FileInfo Promote(FileInfo part, FileInfo final)
    {
        part.Refresh();
        if (!part.Exists || part.Length == 0)
            throw new InvalidOperationException($"output is empty: {final.FullName}");

        // A directory in the way is permanent (no retry or alternate can help).
        if (Directory.Exists(final.FullName))
            throw new IOException($"cannot place {final.FullName}: a directory is in the way");

        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                File.Move(part.FullName, final.FullName, overwrite: true);
                AppLog.Info("output.promoted part={Part} placed={Placed} alternate={Alternate}",
                    part.Name, final.FullName, false);
                return final;
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                // v7.9.9.2: on Windows a *sharing violation* (a player holding
                // the target) surfaces as UnauthorizedAccessException just as a
                // real ACL refusal does. v7.9.8.13.2 rethrew that immediately,
                // which silently defeated the whole "(2)" fallback and threw
                // away a finished encode -- exactly the reported bug. Treat both
                // as retryable; the pre-flight already ruled out a true ACL.
                last = exc;
                if (attempt < 3) Thread.Sleep(250 * (attempt + 1));
            }
        }

        FileInfo alternate = Alternate(final);
        try
        {
            File.Move(part.FullName, alternate.FullName, overwrite: false);
            Console.Error.WriteLine(
                $"WARNING: {final.FullName} is in use; the finished file was saved as {alternate.FullName}");
            AppLog.Warn("output.promoted part={Part} placed={Placed} alternate={Alternate}",
                part.Name, alternate.FullName, true);
            return alternate;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw last!;
        }
    }

    /// <summary>First free <c>name (2).ext</c> in the same directory.</summary>
    internal static FileInfo Alternate(FileInfo final)
    {
        string dir = Path.GetDirectoryName(final.FullName) ?? "";
        string stem = Path.GetFileNameWithoutExtension(final.Name);
        for (int n = 2; n < 1000; n++)
        {
            var candidate = new FileInfo(Path.Combine(dir, $"{stem} ({n}){final.Extension}"));
            // FileInfo.Exists is false for a directory (v7.9.8.12), so a
            // directory occupying the candidate name must be skipped too.
            if (!candidate.Exists && !Directory.Exists(candidate.FullName)) return candidate;
        }
        throw new IOException($"no free output name near {final.FullName}");
    }

    /// <summary>
    /// Report a completed encode whose final rename failed, and KEEP the part.
    /// Until v7.9.8.10 every caller's catch ran <see cref="Discard"/> on any
    /// exception, so a destination held by a player or a share that refused the
    /// replace deleted the only good copy of a long realtime capture. The user
    /// gets the path and can rename it by hand.
    /// </summary>
    public static void Keep(FileInfo part, FileInfo final, string reason)
    {
        part.Refresh();
        Console.Error.WriteLine(
            $"ERROR: the encode finished but could not be placed at {final.FullName}: {reason}");
        if (part.Exists)
            Console.Error.WriteLine($"       the finished file is kept at: {part.FullName}");
    }

    /// <summary>
    /// Remove a part file that a failed or cancelled run left behind. Best
    /// effort on purpose: the run has already failed, and "could not delete the
    /// leftover" must not replace the real error.
    /// </summary>
    public static void Discard(FileInfo part)
    {
        try
        {
            part.Refresh();
            if (part.Exists) part.Delete();
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"WARNING: could not remove {part.FullName}: {exc.Message}");
        }
    }
}
