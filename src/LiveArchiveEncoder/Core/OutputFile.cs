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

        // v7.9.8.13.2: a directory and an ACL refusal are permanent failures;
        // retrying them only adds ~1.5 s before the same error. Keep the old
        // "keep the part" behaviour for those, immediately.
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
            catch (UnauthorizedAccessException)
            {
                throw;                       // permission denied: no retry helps
            }
            catch (IOException exc)
            {
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
