using System.Diagnostics;
using System.Text.RegularExpressions;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>Executable discovery and FFmpeg registry parsing.</summary>
public static partial class FFmpegDiscovery
{
    public static bool IsBtbn { get; private set; }

    /// <summary>
    /// Pick the ffmpeg to use.
    ///
    /// Order: an explicit <c>--ffmpeg</c> path, then a build this tool installed
    /// itself under <c>%LOCALAPPDATA%\LiveArchiveEncoder\ffmpeg</c>, then whatever
    /// sits next to the executable, then PATH. The managed install outranks PATH
    /// because a user who asked for a specific distribution expects that one to
    /// win over a stray system copy.
    /// </summary>
    public static string FindFFmpeg(string? userValue, string? sourceId = null)
    {
        if (userValue.HasText) return ResolveExecutable(userValue);

        // A managed build only wins when it was explicitly selected. An
        // untouched configuration must keep using the system FFmpeg: silently
        // switching every scripted run to a build the user installed on a whim
        // would change output nobody asked to change.
        if (sourceId.HasText
            && !string.Equals(sourceId, "path", StringComparison.OrdinalIgnoreCase)
            && Toolchain.FFmpegFor(sourceId) is { Length: > 0 } managed)
        {
            IsBtbn = string.Equals(sourceId, "btbn", StringComparison.OrdinalIgnoreCase);
            return managed;
        }

        foreach (string path in CandidateBtbnFFmpegPaths())
        {
            if (File.Exists(path))
            {
                IsBtbn = true;
                return path;
            }
        }
        IsBtbn = false;
        return ResolveExecutable("ffmpeg");
    }

    /// <summary>
    /// ffplay next to the active ffmpeg, else PATH. Absence is normal -- many
    /// builds ship without it -- so callers get null rather than an exception.
    /// </summary>
    public static string? FindFFplay(string ffmpegPath)
    {
        string? dir = Path.GetDirectoryName(ffmpegPath);
        if (!string.IsNullOrEmpty(dir))
        {
            string sibling = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffplay.exe" : "ffplay");
            if (File.Exists(sibling)) return sibling;
        }

        string exeName = OperatingSystem.IsWindows() ? "ffplay.exe" : "ffplay";
        string? envPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(envPath)) return null;
        foreach (string entry in envPath.Split(Path.PathSeparator))
        {
            if (entry.Length == 0) continue;
            try
            {
                string full = Path.Combine(entry, exeName);
                if (File.Exists(full)) return full;
            }
            catch { /* invalid PATH entry */ }
        }
        return null;
    }

    public static string FindFFprobe(string? userValue, string ffmpegPath)
    {
        if (userValue.HasText) return ResolveExecutable(userValue);

        string? dir = Path.GetDirectoryName(ffmpegPath);
        if (!string.IsNullOrEmpty(dir))
        {
            string sibling = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(sibling)) return sibling;
        }
        return ResolveExecutable("ffprobe");
    }

    private static string ResolveExecutable(string value)
    {
        // Python's exe_path(): an explicit path wins, otherwise consult PATH.
        if (value.Contains(Path.DirectorySeparatorChar)
            || (Path.AltDirectorySeparatorChar != '\0' && value.Contains(Path.AltDirectorySeparatorChar)))
        {
            if (File.Exists(value)) return Path.GetFullPath(value);
            throw new FileNotFoundException($"Executable not found: {value}");
        }

        string? found = Which(value);
        if (found is not null) return found;
        throw new FileNotFoundException($"Cannot find {value}; add FFmpeg/bin to PATH.");
    }

    private static string? Which(string name)
    {
        string exeName = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name + ".exe"
            : name;

        string? envPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(envPath)) return null;

        foreach (string dir in envPath.Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            try
            {
                string full = Path.Combine(dir, exeName);
                if (File.Exists(full)) return full;
            }
            catch { /* invalid PATH entry */ }
        }
        return null;
    }

    private static IEnumerable<string> CandidateBtbnFFmpegPaths()
    {
        var roots = new List<string>();
        string? env = Environment.GetEnvironmentVariable("BTBN_FFMPEG_DIR");
        if (env.HasText) roots.Add(env);
        roots.Add(AppContext.BaseDirectory);
        roots.Add(Directory.GetCurrentDirectory());

        // Windows builds ship a .exe; every other platform ships an
        // extension-less binary (the upstream tarballs do exactly that).
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        string[] names = OperatingSystem.IsWindows()
            ? ["ffmpeg-master-latest-win64-gpl",
               "ffmpeg-n9.0-latest-win64-gpl-9.0",
               "ffmpeg-n8.1-latest-win64-gpl-8.1",
               "ffmpeg"]
            : ["ffmpeg-master-latest-linux64-gpl",
               "ffmpeg-master-latest-linux64-lgpl",
               "ffmpeg-master-latest-linux64-nonfree",
               "ffmpeg"];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<string>();

        void Add(string path)
        {
            try
            {
                string key = Path.GetFullPath(path);
                if (seen.Add(key)) unique.Add(key);
            }
            catch { /* invalid path */ }
        }

        foreach (string root in roots)
        {
            foreach (string name in names)
            {
                string baseDir = Path.Combine(root, name);
                Add(Path.Combine(baseDir, "bin", exe));
                Add(Path.Combine(baseDir, exe));
            }
            Add(Path.Combine(root, "bin", exe));
            Add(Path.Combine(root, exe));

            try
            {
                foreach (string child in Directory.EnumerateDirectories(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    Add(Path.Combine(child, "bin", exe));
                }
            }
            catch { /* unreadable directory */ }
        }

        return unique;
    }

    public static (string Version, string Configuration, bool LikelyGpl) BuildInfo(string ffmpeg)
    {
        CaptureResult p = ProcessRunner.Run(ffmpeg, "-hide_banner", "-version");
        string text = p.Stdout + "\n" + p.Stderr;
        string version = text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0)
            ?? "unknown FFmpeg";
        string configuration = text.Split('\n')
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith("configuration:", StringComparison.Ordinal)) ?? "";
        return (version, configuration, configuration.Contains("--enable-gpl"));
    }

    [GeneratedRegex(@"^\s*V([A-Z.]{5})\s+(\S+)\s+(.+?)\s*$")]
    private static partial Regex VideoEncoderLine();

    /// <summary>
    /// Every legacy encoder name in one alternation, v7.8.1.
    ///
    /// It used to be an array of anchored patterns tested one after another (see
    /// archive/retired/RetiredConstants.cs). One source-generated regex does the
    /// same job in a single pass, is compiled ahead of time instead of at first
    /// use, and needs no reflection -- which is what keeps it identical between
    /// the JIT and NativeAOT builds.
    /// </summary>
    [GeneratedRegex(
        @"^(?:a64multi(?:5)?|amv|asv[12]|cinepak|cljr|flashsv2?|flv|h261|h263(?:p)?"
        + @"|mjpeg|msmpeg4(?:v2)?|msrle|msvideo1|mpeg[124]video|mpeg4|qtrle|roqvideo"
        + @"|rpza|rv(?:10|20)|smc|snow|svq1|wmv[12]|zmbv|libtheora|libx264|libxvid|vivo)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LegacyEncoderName();

    [GeneratedRegex(@"^\s*A[A-Z.]{5}\s+(\S+)\s+")]
    private static partial Regex AudioEncoderLine();

    [GeneratedRegex(@"\(codec\s+([^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex CodecInDescription();

    public static List<(string Name, string Flags, string Description)> ParseVideoEncoders(string text)
    {
        var result = new List<(string, string, string)>();
        foreach (string line in text.Split('\n'))
        {
            Match m = VideoEncoderLine().Match(line);
            if (m.Success) result.Add((m.Groups[2].Value, m.Groups[1].Value, m.Groups[3].Value));
        }
        return result;
    }

    /// <summary>
    /// Every video encoder name in <c>ffmpeg -encoders</c>, including the modern
    /// ones this tool never selects for the live pass (libx265, libvpx-vp9,
    /// libsvtav1, ...). v7.9.5.1: the second pass can use those, so "is this
    /// codec in this build" has to be answerable beyond the legacy table.
    /// </summary>
    public static HashSet<string> ParseVideoEncoderNames(string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, _, _) in ParseVideoEncoders(text)) found.Add(name);
        return found;
    }

    public static string ExtractCodec(string description, string name)
    {
        Match m = CodecInDescription().Match(description);
        return m.Success ? m.Groups[1].Value.Trim() : name;
    }

    public static string? LegacyFamily(string name, string description)
    {
        string lname = name.ToLowerInvariant();
        if (Constants.NonArchiveExact.Contains(lname)) return null;
        foreach (string prefix in Constants.ModernPrefixes)
        {
            if (lname.StartsWith(prefix, StringComparison.Ordinal)) return null;
        }

        if (!LegacyEncoderName().IsMatch(lname)) return null;

        string desc = description.ToLowerInvariant();
        if (lname is "libx264" or "libxvid" or "libtheora") return "classic-library";
        if (lname.StartsWith("mpeg", StringComparison.Ordinal)
            || lname.StartsWith("msmpeg", StringComparison.Ordinal)) return "mpeg-classic";
        if (lname.StartsWith("wmv", StringComparison.Ordinal)
            || lname.StartsWith("rv", StringComparison.Ordinal)) return "proprietary-classic";
        return desc.Contains("commodore", StringComparison.Ordinal) ? "home-computer" : "retro/classic";
    }

    public static (string Ext, string Format) GuessContainer(string name)
    {
        if (Constants.ContainerByEncoder.TryGetValue(name, out (string Ext, string Format) value))
            return value;
        if (name.StartsWith("wmv", StringComparison.OrdinalIgnoreCase)) return ("wmv", "asf");
        return ("avi", "avi");
    }

    /// <summary>
    /// The legacy/classic video encoders inside <c>ffmpeg -encoders</c> output.
    ///
    /// v7.9.5: split out of the process call so one <c>-encoders</c> run can
    /// feed the video list, the audio list and the subtitle list at once --
    /// this used to be two launches of the same command, one per list. The
    /// launch itself now happens in <see cref="BuildProfileStore.Sweep"/>.
    /// </summary>
    public static List<EncoderInfo> ParseLegacyEncoders(string text)
    {
        var result = new List<EncoderInfo>();
        foreach ((string name, string flags, string desc) in ParseVideoEncoders(text))
        {
            string? family = LegacyFamily(name, desc);
            if (family is null) continue;
            (string ext, string fmt) = GuessContainer(name);
            result.Add(new EncoderInfo
            {
                Name = name,
                Flags = flags,
                Description = desc.Trim(),
                Codec = ExtractCodec(desc, name),
                Family = family,
                Extension = ext,
                FormatName = fmt,
            });
        }
        return [.. result.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Audio encoder names inside <c>ffmpeg -encoders</c> output.</summary>
    public static HashSet<string> ParseAudioEncoders(string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            Match m = AudioEncoderLine().Match(line);
            if (m.Success) found.Add(m.Groups[1].Value);
        }
        return found;
    }

    /// <summary>
    /// Subtitle encoder names inside <c>ffmpeg -encoders</c> output. The name
    /// has to be a word: the legend block (<c> S..... = Subtitle</c>) would
    /// otherwise contribute an encoder called "=".
    /// </summary>
    public static HashSet<string> ParseSubtitleEncoders(string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            Match m = SubtitleEncoderLine().Match(line);
            if (m.Success) found.Add(m.Groups[1].Value);
        }
        return found;
    }

    [GeneratedRegex(@"^\s*S\.+\s+([A-Za-z0-9_]+)\s", RegexOptions.Multiline)]
    private static partial Regex SubtitleEncoderLine();
}
