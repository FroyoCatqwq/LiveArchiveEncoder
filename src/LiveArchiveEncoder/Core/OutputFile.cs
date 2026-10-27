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
    /// <summary>The sibling this encode writes to before it is put in place.</summary>
    public static FileInfo PartPath(FileInfo final) => new(Path.Combine(
        Path.GetDirectoryName(final.FullName) ?? "",
        $"{Path.GetFileNameWithoutExtension(final.Name)}.part{final.Extension}"));

    /// <summary>
    /// Move a finished part file onto its real name, replacing an older copy.
    /// Both names are in the same directory, so this is a rename, not a copy.
    /// </summary>
    public static void Promote(FileInfo part, FileInfo final)
    {
        part.Refresh();
        if (!part.Exists || part.Length == 0)
            throw new InvalidOperationException($"output is empty: {final.FullName}");
        File.Move(part.FullName, final.FullName, overwrite: true);
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
