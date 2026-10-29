namespace Lae.Media.Toolchain;

/// <summary>What the machine can offer before an encode is worth starting.</summary>
/// <param name="Ffmpeg">Full path of the FFmpeg executable, or null.</param>
/// <param name="Ffprobe">Full path of ffprobe, or null.</param>
public sealed record ToolchainProbeResult
{
    public string? Ffmpeg { get; init; }

    public string? Ffprobe { get; init; }

    public bool Complete => Ffmpeg is not null && Ffprobe is not null;

    /// <summary>One line per missing tool, each naming what it is needed for.</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (Ffmpeg is null) missing.Add("ffmpeg (required: it does the encoding)");
        if (Ffprobe is null) missing.Add("ffprobe (required: it reads the source's streams and duration)");
        return missing;
    }
}

/// <summary>
/// Looks for FFmpeg without running it.
///
/// <para>v8 has no interactive shell, and v7's first-run prompt ("no FFmpeg
/// found, download it?") went with it. Without a replacement, a new user's first
/// command fails with <c>Cannot find ffmpeg</c> and no next step -- a dead end
/// v7.9.6.3 had deliberately removed. <c>lae setup</c> is that replacement: it
/// reports what is present and what to do, and downloads nothing behind the
/// user's back.</para>
///
/// <para>Scanning PATH rather than launching <c>ffmpeg -version</c> keeps this
/// cheap and side-effect free: <c>setup</c> must be safe to run in a script, and
/// a probe that starts processes is also a probe that can hang.</para>
/// </summary>
public static class ToolchainProbe
{
    private static readonly string[] Names = OperatingSystem.IsWindows()
        ? ["ffmpeg.exe", "ffprobe.exe"]
        : ["ffmpeg", "ffprobe"];

    /// <summary>
    /// Resolves the tools from an explicit directory first, then from PATH.
    /// An explicit <paramref name="directory"/> is what
    /// <c>ToolchainSettings.FfmpegPath</c> means: the user has already decided.
    /// </summary>
    public static ToolchainProbeResult Probe(string? directory = null)
    {
        string? ffmpeg = Find(Names[0], directory);
        string? ffprobe = Find(Names[1], directory);
        return new ToolchainProbeResult { Ffmpeg = ffmpeg, Ffprobe = ffprobe };
    }

    private static string? Find(string name, string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string folder;
            try
            {
                folder = entry.Trim().Trim('"');
            }
            catch (ArgumentException)
            {
                // A PATH entry can contain characters a path cannot; skip it
                // rather than failing the whole probe over someone's typo.
                continue;
            }

            if (folder.Length == 0) continue;

            try
            {
                string candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                continue;
            }
        }

        return null;
    }
}
