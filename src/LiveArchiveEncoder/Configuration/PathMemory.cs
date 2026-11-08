namespace LiveArchiveEncoder.Configuration;

/// <summary>
/// v7.9.9.2: a short most-recently-used list of directories the user encoded
/// from / into. Persisted in config.json so the CUI can offer them again next
/// session; the CLI uses it as the basis for "input a directory, output lands
/// there" auto-completion.
///
/// <para>Deliberately a pure helper: no I/O beyond Directory.Exists, no global
/// state, so tests can exercise it without touching the user's config.</para>
/// </summary>
public static class PathMemory
{
    public const int Limit = 8;

    /// <summary>Keep only existing, absolute, unique paths, newest first.</summary>
    public static List<string> Normalise(IEnumerable<string>? raw)
    {
        var kept = new List<string>();
        if (raw is null) return kept;

        foreach (string? path in raw)
        {
            string trimmed = (path ?? "").Trim();
            if (trimmed.Length == 0 || !Path.IsPathFullyQualified(trimmed)) continue;
            if (!Directory.Exists(trimmed)) continue;

            string full = Path.GetFullPath(trimmed);
            if (kept.Contains(full, StringComparer.OrdinalIgnoreCase)) continue;
            kept.Add(full);
            if (kept.Count >= Limit) break;
        }
        return kept;
    }

    /// <summary>
    /// Put the given directories at the front of <paramref name="mru"/>.
    /// Mutates the caller's list so both Config and UserConfig stay in sync.
    /// </summary>
    public static void Remember(List<string> mru, params string?[] directories)
    {
        var merged = new List<string>();
        foreach (string? dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string full = Path.GetFullPath(dir);
            if (!Directory.Exists(full)) continue;
            merged.Add(full);
        }
        merged.AddRange(mru);

        List<string> normalised = Normalise(merged);
        mru.Clear();
        mru.AddRange(normalised);
    }

    /// <summary>
    /// Resolve an output argument that names a directory into a real file path.
    /// Returns null when the argument is not an existing directory, so the
    /// caller keeps its old "a path is a filename" behaviour unchanged.
    /// </summary>
    public static string? CompleteDirectory(string outputArg, string stem, string extension)
    {
        if (string.IsNullOrWhiteSpace(outputArg) || !Directory.Exists(outputArg)) return null;
        string ext = extension.TrimStart('.');
        return Path.Combine(outputArg, stem + UI.OutputNaming.Suffix + "." + ext);
    }
}
