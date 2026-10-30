namespace Lae.Media;

/// <summary>
/// Write to a sibling temporary, then put it in place under its real name -- and
/// never the other way round.
///
/// <para>FFmpeg's <c>-y</c> truncates its destination the moment it opens it, so
/// encoding straight into the requested name has two failure modes that matter
/// for an archival tool: a cancelled or failed run destroys the good copy that
/// was already there, and it leaves a short file that looks finished to
/// everything downstream (the next pass, a later remux, the user's own file
/// manager).</para>
///
/// <para>The temporary keeps the real extension (<c>clip.part.mp4</c>) so FFmpeg,
/// ffprobe and every extension-driven rule keep seeing exactly what they saw
/// before; only the name says "not finished yet".</para>
/// </summary>
public static class PartFile
{
    /// <summary>The sibling to write to before the result is put in place.</summary>
    public static string PathFor(string finalPath)
    {
        string full = Path.GetFullPath(finalPath);
        string folder = Path.GetDirectoryName(full) ?? "";
        string stem = Path.GetFileNameWithoutExtension(full);
        return Path.Combine(folder, $"{stem}.part{Path.GetExtension(full)}");
    }

    /// <summary>
    /// Move a finished part file onto its real name, replacing an older copy.
    /// Both names sit in the same directory, so this is a rename, not a copy.
    /// </summary>
    public static void Promote(string partPath, string finalPath)
    {
        var part = new FileInfo(partPath);
        part.Refresh();

        if (!part.Exists || part.Length == 0)
            throw new InvalidOperationException($"the encoder produced no output: {finalPath}");

        File.Move(part.FullName, Path.GetFullPath(finalPath), overwrite: true);
    }

    /// <summary>
    /// Remove a leftover part file. Best effort on purpose: the run has already
    /// failed, and "could not delete the leftover" must not replace the real
    /// error. The warning comes back as a sentence for the caller to print,
    /// rather than being printed here -- a library does not get to write to the
    /// user's terminal.
    /// </summary>
    public static string? Discard(string partPath)
    {
        try
        {
            var part = new FileInfo(partPath);
            part.Refresh();
            if (!part.Exists) return null;
            part.Delete();
            return null;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return $"could not remove the leftover {partPath}: {exc.Message}";
        }
    }
}
