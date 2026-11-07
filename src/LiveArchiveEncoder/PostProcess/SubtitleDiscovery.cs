using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>
/// v7.9.8.13: find the sidecar subtitles that belong to a video by name.
/// A release commonly ships <c>X.sc.ass</c> and <c>X.tc.ass</c> next to
/// <c>X.mkv</c>; neither used to be picked up unless the user typed both paths.
///
/// <para>Matching is deliberately narrow -- same directory, and the file name
/// must be the video stem followed by a dot and a language tag (or by nothing
/// but the extension). A neighbouring episode whose name merely starts with the
/// same words must not be attached by accident.</para>
/// </summary>
public static class SubtitleDiscovery
{
    /// <summary>All matching subtitles, best candidate first.</summary>
    public static IReadOnlyList<string> Find(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return [];

        string full;
        try { full = Path.GetFullPath(sourcePath); }
        catch (Exception exc) when (exc is ArgumentException or NotSupportedException
                                        or PathTooLongException) { return []; }

        string? dir = Path.GetDirectoryName(full);
        if (dir is null || !Directory.Exists(dir)) return [];

        string stem = Path.GetFileNameWithoutExtension(full);
        string prefix = stem + ".";
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            string name = Path.GetFileName(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (!Constants.SubtitleFileExtensions.Contains(ext)) continue;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, stem + ext, StringComparison.OrdinalIgnoreCase)) continue;
            found.Add(file);
        }
        return Rank(found, stem);
    }

    /// <summary>
    /// ASS/SSA first (only they carry font names), then a file named exactly
    /// after the video, then the rest in name order so <c>sc</c> beats
    /// <c>tc</c> deterministically.
    /// </summary>
    public static IReadOnlyList<string> Rank(IEnumerable<string> files, string stem) =>
        [.. files.OrderBy(f => RankKey(f, stem), StringComparer.Ordinal)];

    private static string RankKey(string file, string stem)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        string ext = Path.GetExtension(file).ToLowerInvariant();
        int format = ext is ".ass" or ".ssa" ? 0 : 1;
        int exact = string.Equals(name, stem, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        return $"{format}{exact}{name.ToUpperInvariant()}";
    }
}
