using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>
/// What one FFmpeg binary can do, discovered once and remembered.
///
/// v7.9.5: the reason this exists is process startup. Before the cache, a
/// single CLI run launched <c>-encoders</c> twice (once for video, once for
/// audio), <c>-filters</c>, <c>-h filter=scale</c>, <c>-h encoder=NAME</c> and
/// <c>-version</c> before it had decoded a single frame -- six launches at
/// ~210 ms each on Windows, plus two preflight trials. Every one of those
/// answers is a property of the binary, not of the run, so they are read once
/// per binary (keyed by path + size + mtime) and kept on disk.
///
/// Measured on this machine (2026-10-23): 6 launches -> 4 on the first run
/// (where the cache is cold) -> 0 on every run after it. What is left before
/// the encode is the source probe and one preflight trial.
///
/// The cache is deliberately disposable: a corrupt or stale file just means
/// another sweep, and <c>LAE_NO_CAPS_CACHE=1</c> forces one.
/// </summary>
public sealed class BuildProfile
{
    /// <summary>Bumped whenever the shape below changes; an older file is ignored.</summary>
    // v7.9.7: bumped from 2 because the cached encoder list is now expected to
    // contain libopenh264 (the era presets select it); a profile written by an
    // older build would silently claim the encoder does not exist.
    public const int SchemaVersion = 3;

    public int Schema { get; set; } = SchemaVersion;

    /// <summary>path + size + mtime, hashed. Two builds never share a cache file.</summary>
    public string Identity { get; set; } = "";

    /// <summary>First line of <c>ffmpeg -version</c>, e.g. "ffmpeg version N-127941-...".</summary>
    public string Version { get; set; } = "";

    /// <summary>The configure line, when the build prints one.</summary>
    public string Configuration { get; set; } = "";

    public bool Gpl { get; set; }

    /// <summary>Legacy/classic video encoders this build has, as EncoderInfo records.</summary>
    public List<EncoderInfo> Encoders { get; set; } = [];

    /// <summary>
    /// Every video encoder name, legacy or not. v7.9.5.1: the second pass can
    /// name libx265 / libvpx-vp9 / libsvtav1, which are not in the legacy table,
    /// and "this build lacks that codec" must be answerable without a launch.
    /// </summary>
    public List<string> VideoEncoders { get; set; } = [];

    public List<string> AudioEncoders { get; set; } = [];
    public List<string> SubtitleEncoders { get; set; } = [];
    public List<string> Filters { get; set; } = [];
    public List<string> ScaleAlgorithms { get; set; } = [];

    /// <summary>
    /// Per-encoder <c>-h encoder=NAME</c> results, filled in the first time an
    /// encoder is actually selected. Keyed by encoder name.
    /// </summary>
    public Dictionary<string, EncoderCaps> Caps { get; set; } = [];

    public DateTimeOffset BuiltAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>Source-generated contract: NativeAOT cannot use reflection here.</summary>
[JsonSourceGenerationOptions(WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BuildProfile))]
internal sealed partial class BuildProfileJsonContext : JsonSerializerContext;

/// <summary>
/// A live view of <see cref="BuildProfile"/>: the sets the UI and the encoder
/// want, plus lazy per-encoder capability lookups that write back to the cache.
/// </summary>
public sealed class Capabilities
{
    private readonly BuildProfile _profile;
    private readonly string _ffmpeg;
    private readonly string? _cachePath;

    internal Capabilities(BuildProfile profile, string ffmpeg, string? cachePath, bool fromCache)
    {
        _profile = profile;
        _ffmpeg = ffmpeg;
        _cachePath = cachePath;
        FromCache = fromCache;

        Encoders = profile.Encoders;
        AudioEncoders = [.. profile.AudioEncoders];
        SubtitleEncoders = [.. profile.SubtitleEncoders];
        VideoEncoders = [.. profile.VideoEncoders];
        Filters = [.. profile.Filters];
        ScaleAlgorithms = [.. profile.ScaleAlgorithms];
    }

    /// <summary>True when nothing had to be launched to answer.</summary>
    public bool FromCache { get; }

    public string Version => _profile.Version;
    public string Configuration => _profile.Configuration;
    public bool Gpl => _profile.Gpl;

    public List<EncoderInfo> Encoders { get; }
    public HashSet<string> AudioEncoders { get; }
    public HashSet<string> SubtitleEncoders { get; }
    public HashSet<string> VideoEncoders { get; }
    public HashSet<string> Filters { get; }
    public HashSet<string> ScaleAlgorithms { get; }

    /// <summary>
    /// <c>ffmpeg -h encoder=NAME</c>, cached per build. The returned object is
    /// shared with the <see cref="EncoderInfo"/> the caller passed in, so
    /// everything downstream sees one instance.
    /// </summary>
    public EncoderCaps EncoderCaps(EncoderInfo encoder)
    {
        if (encoder.Caps is not null) return encoder.Caps;
        if (_profile.Caps.TryGetValue(encoder.Name, out EncoderCaps? cached))
        {
            encoder.Caps = cached;
            return cached;
        }

        EncoderCaps caps = FFmpegCapabilities.ProbeEncoderCaps(_ffmpeg, encoder);
        encoder.Caps = caps;
        _profile.Caps[encoder.Name] = caps;
        // Best effort: a read-only cache directory must not fail a run.
        if (_cachePath is not null) BuildProfileStore.Save(_cachePath, _profile);
        return caps;
    }

    /// <summary>
    /// Re-probe one encoder's capabilities, replacing whatever the cache had.
    /// Used by the interactive "rescan encoders" entry.
    /// </summary>
    public EncoderCaps RefreshEncoderCaps(EncoderInfo encoder)
    {
        _profile.Caps.Remove(encoder.Name);
        encoder.Caps = null;
        return EncoderCaps(encoder);
    }
}

/// <summary>Reading, sweeping and writing <see cref="BuildProfile"/>.</summary>
public static class BuildProfileStore
{
    /// <summary>Escape hatch, for a build replaced in place by one of identical size and mtime.</summary>
    public const string DisableEnvVar = "LAE_NO_CAPS_CACHE";

    private static readonly Dictionary<string, Capabilities> Memory = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    /// <summary>
    /// Capabilities for this FFmpeg. Loads the cache when its identity matches,
    /// sweeps the binary otherwise. Never throws for a cache problem -- only a
    /// broken FFmpeg surfaces as an exception, and it surfaces from the sweep.
    /// </summary>
    public static Capabilities For(string ffmpeg, bool refresh = false)
    {
        string identity = Identity(ffmpeg);
        bool disabled = Environment.GetEnvironmentVariable(DisableEnvVar) is { Length: > 0 } v
            && v is not "0";
        string? path = disabled ? null : Path.Combine(
            ConfigStore.RootDirectory, "cache", $"caps-{identity}.json");

        lock (Gate)
        {
            if (!refresh && Memory.TryGetValue(identity, out Capabilities? known)) return known;
        }

        if (!refresh && path is not null && TryRead(path, identity) is { } cached)
        {
            var fromDisk = new Capabilities(cached, ffmpeg, path, fromCache: true);
            lock (Gate) Memory[identity] = fromDisk;
            return fromDisk;
        }

        BuildProfile swept = Sweep(ffmpeg, identity);
        if (path is not null) Save(path, swept);

        var fresh = new Capabilities(swept, ffmpeg, path, fromCache: false);
        lock (Gate) Memory[identity] = fresh;
        return fresh;
    }

    /// <summary>
    /// The cache key. Size and mtime rather than the version string, because
    /// asking for the version *is* one of the launches this exists to avoid.
    /// </summary>
    public static string Identity(string ffmpeg)
    {
        string key = ffmpeg;
        try
        {
            var info = new FileInfo(ffmpeg);
            if (info.Exists)
                key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { }

        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>
    /// One pass over the binary: version, encoder registry (video, audio and
    /// subtitle from the same <c>-encoders</c> run), filter registry and the
    /// scaler kernels. Four launches, once per build.
    /// </summary>
    public static BuildProfile Sweep(string ffmpeg, string identity)
    {
        (string version, string configuration, bool gpl) = FFmpegDiscovery.BuildInfo(ffmpeg);

        CaptureResult encoders = ProcessRunner.Run([ffmpeg, "-hide_banner", "-encoders"], 30);
        if (encoders.ExitCode != 0)
        {
            throw new InvalidOperationException(encoders.Stderr.Trim() is { Length: > 0 } err
                ? err : "ffmpeg -encoders failed");
        }

        CaptureResult filters = ProcessRunner.Run(ffmpeg, "-hide_banner", "-filters");
        if (filters.ExitCode != 0)
        {
            throw new InvalidOperationException(
                (filters.Stderr.Trim() is { Length: > 0 } ferr ? ferr : filters.Stdout).Trim()
                    is { Length: > 0 } msg ? msg : "ffmpeg -filters failed");
        }

        HashSet<string> names = FFmpegCapabilities.ParseFilterNames(
            filters.Stdout + "\n" + filters.Stderr);
        names = FFmpegCapabilities.VerifyNamedFilters(
            ffmpeg, names, ["scale", .. Constants.DeinterlaceFilters]);
        HashSet<string> scalers = FFmpegCapabilities.DetectScaleAlgorithms(ffmpeg);

        return new BuildProfile
        {
            Identity = identity,
            Version = version,
            Configuration = configuration,
            Gpl = gpl,
            Encoders = FFmpegDiscovery.ParseLegacyEncoders(encoders.Stdout),
            VideoEncoders = [.. FFmpegDiscovery.ParseVideoEncoderNames(encoders.Stdout)],
            AudioEncoders = [.. FFmpegDiscovery.ParseAudioEncoders(encoders.Stdout)],
            SubtitleEncoders = [.. FFmpegDiscovery.ParseSubtitleEncoders(encoders.Stdout)],
            Filters = [.. names],
            ScaleAlgorithms = [.. scalers],
        };
    }

    private static BuildProfile? TryRead(string path, string identity)
    {
        try
        {
            if (!File.Exists(path)) return null;
            BuildProfile? profile = JsonSerializer.Deserialize(
                File.ReadAllText(path), BuildProfileJsonContext.Default.BuildProfile);
            // Valid JSON is not the same as usable cache: System.Text.Json turns
            // "Filters": null into a null member, and Capabilities would then
            // throw a NullReferenceException *outside* this try. The cache exists
            // to save four launches, so anything short of a complete profile is
            // simply a miss.
            return profile is not null
                && profile.Schema == BuildProfile.SchemaVersion
                && profile.Identity == identity
                && profile.Encoders is not null
                && profile.VideoEncoders is not null
                && profile.AudioEncoders is not null
                && profile.SubtitleEncoders is not null
                && profile.Filters is not null
                && profile.ScaleAlgorithms is not null
                && profile.Caps is not null
                && profile.Version is not null
                && profile.Configuration is not null
                ? profile
                : null;
        }
        catch (Exception exc) when (exc is JsonException or IOException or UnauthorizedAccessException
                                        or NotSupportedException)
        {
            return null;
        }
    }

    internal static void Save(string path, BuildProfile profile)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            profile.BuiltAt = DateTimeOffset.Now;
            string json = JsonSerializer.Serialize(profile, BuildProfileJsonContext.Default.BuildProfile);
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or NotSupportedException or ArgumentException)
        {
            // A cache that cannot be written is a slower run, not a failed one.
        }
    }
}
