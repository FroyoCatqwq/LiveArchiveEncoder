namespace LiveArchiveEncoder.Configuration;

/// <summary>
/// Named settings files, so one machine can keep more than one setup.
///
/// Scope, deliberately: this is a shelf of snapshots inside the tool's own
/// directory (<c>configs/*.json</c>, a sibling of <c>config.json</c>), not a
/// general "open any file" dialog. Loading still goes through
/// <see cref="ConfigStore.Apply"/>, so a profile written against a different
/// FFmpeg build cannot install an encoder that build does not have.
///
/// The name normalisation is the security-relevant part: the name comes straight
/// from a prompt, and <c>Path.GetFileName</c> plus an invalid-character sweep is
/// what keeps <c>../../something</c> from writing outside the shelf.
/// </summary>
public static class ConfigProfiles
{
    /// <summary>Directory holding the named profiles.</summary>
    public static string Directory => Path.Combine(ConfigStore.RootDirectory, "configs");

    /// <summary>Profiles on the shelf, ordered the way the menu lists them.</summary>
    public static List<FileInfo> List()
    {
        try
        {
            var dir = new DirectoryInfo(Directory);
            if (!dir.Exists) return [];
            return [.. dir.GetFiles("*.json").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // An unreadable shelf is an empty shelf, not a crash on startup.
            return [];
        }
    }

    /// <summary>
    /// Turn what the user typed into a file name inside <see cref="Directory"/>,
    /// or null when nothing usable is left.
    ///
    /// Only the last path segment survives: typing <c>../other/config</c> saves
    /// <c>config.json</c> on the shelf rather than overwriting a file one level
    /// up. Everything the platform considers invalid in a name becomes an
    /// underscore, and the extension is forced to <c>.json</c>.
    /// </summary>
    public static string? NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string name = Path.GetFileName(raw.Trim().Trim('"'));
        if (name.Length == 0) return null;

        foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');

        // ".." and "." are directory references, not names.
        name = name.Trim();
        if (name is "" or "." or "..") return null;

        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name += ".json";
        if (name.Length > 68) name = name[..64] + ".json";
        return name;
    }

    /// <summary>Full path a profile name maps to, or null when the name is unusable.</summary>
    public static string? PathFor(string? rawName)
    {
        string? name = NormalizeName(rawName);
        return name is null ? null : Path.Combine(Directory, name);
    }

    /// <summary>
    /// Write the current state as a named profile. Returns the file that was
    /// written, or null when the name was unusable or the write failed.
    /// </summary>
    public static FileInfo? SaveAs(Config c, string? rawName)
    {
        string? path = PathFor(rawName);
        if (path is null) return null;
        return ConfigStore.Save(ConfigStore.Capture(c), path) ? new FileInfo(path) : null;
    }

    /// <summary>
    /// Load a profile and apply it to the live config.
    ///
    /// Returns the file that was read, or null when it does not exist or cannot
    /// be parsed -- the caller reports which, because "no such profile" and
    /// "that file is not a settings file" need different answers.
    /// </summary>
    public static FileInfo? LoadInto(Config c, FileInfo file, IReadOnlyList<EncoderInfo> encoders)
    {
        if (!file.Exists) return null;
        UserConfig stored = ConfigStore.Load(file.FullName);
        // A structurally empty file (or one from a much older build) still
        // applies: every field is optional and the live config keeps whatever
        // the profile does not mention.
        ConfigStore.Apply(stored, c, encoders);
        return file;
    }

    /// <summary>Delete a profile. Missing files count as success.</summary>
    public static bool Delete(string? rawName)
    {
        string? path = PathFor(rawName);
        if (path is null) return false;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
