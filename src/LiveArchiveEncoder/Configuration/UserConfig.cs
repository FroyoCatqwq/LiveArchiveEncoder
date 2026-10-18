using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveArchiveEncoder.Configuration;

/// <summary>
/// Source-generated JSON contract for <see cref="UserConfig"/>.
///
/// Required for NativeAOT: reflection-based <c>JsonSerializer</c> throws
/// <c>JsonSerializerIsReflectionDisabled</c> there, which surfaced as a crash on
/// startup in the AOT builds while the JIT build worked fine. Generating the
/// contract at compile time keeps both flavours behaving identically.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UserConfig))]
internal partial class UserConfigJsonContext : JsonSerializerContext;

/// <summary>
/// Persisted settings for the interactive shell.
///
/// The shape mirrors <see cref="Config"/> but stores the encoder by name only:
/// encoder availability depends on the FFmpeg build that happens to be present,
/// so a name that cannot be resolved on load is dropped instead of trusted.
/// </summary>
public sealed class UserConfig
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public string? InputPath { get; set; }
    public string? OutputPath { get; set; }
    public bool OutputIsAuto { get; set; } = true;

    public string? Encoder { get; set; }
    public string? AudioEncoder { get; set; }

    public double StartTimeSeconds { get; set; }
    public double? EndTimeSeconds { get; set; }
    public double Fps { get; set; } = Constants.DefaultFps;
    public List<double> FpsLevels { get; set; } = [13.0, 8.0, 5.0, 3.0];
    public double Limit { get; set; } = 200.0;
    public double Recover { get; set; } = 150.0;
    public double Window { get; set; } = 2.0;
    public int Qv { get; set; } = 8;
    public int Threads { get; set; } = 16;
    public int BufferFrames { get; set; } = 6;
    public int AudioKbps { get; set; } = 32;
    public int AudioChannels { get; set; } = 1;
    public int? AudioRate { get; set; }
    public int Width { get; set; } = Constants.DefaultWidth;
    public int Height { get; set; } = Constants.DefaultHeight;
    public bool Crop43 { get; set; } = true;
    public string ScaleAlgo { get; set; } = "neighbor";
    public string Deinterlace { get; set; } = "off";
    public string DeintMode { get; set; } = "send_frame";
    public string DeintParity { get; set; } = "auto";
    public bool DeintMarkedOnly { get; set; }
    /// <summary>Hardware decode method: "off" (default), "auto", or a method name.</summary>
    public string Hwaccel { get; set; } = "off";
    public string X264Preset { get; set; } = "medium";
    public string X264Tune { get; set; } = "";
    public string X264Profile { get; set; } = "";
    public string X264Level { get; set; } = "";

    public bool Turbo { get; set; }
    public int Parallel { get; set; } = 1;
    public string Language { get; set; } = "en";

    /// <summary>Selected FFmpeg distribution id (btbn / gyan / avbuild / nonfree / path).</summary>
    public string FfmpegSource { get; set; } = FFmpeg.FfmpegCatalog.DefaultId;

    /// <summary>Proxy for FFmpeg downloads; null means "ask every time".</summary>
    public string? FfmpegProxy { get; set; }

    /// <summary>"off" / "ffplay" / "external".</summary>
    public string PlayMode { get; set; } = "off";

    /// <summary>External player executable; empty means the shell association.</summary>
    public string? PlayerPath { get; set; }

    // ---- v7.6: post-processing stages ----
    // Absent from v1 files, which is fine: System.Text.Json leaves a property
    // alone when the JSON does not mention it, so an old config keeps the
    // defaults (both stages off) instead of nulling anything out.

    public WatermarkSettings Watermark { get; set; } = new();

    public RecompressSettings Recompress { get; set; } = new();

    /// <summary>Last applied era preset id, or null for "custom settings".</summary>
    public string? EraPreset { get; set; }

    /// <summary>Ignored on load; present so the file is self-describing.</summary>
    public DateTimeOffset? SavedAt { get; set; }
}

/// <summary>
/// Reads and writes <see cref="UserConfig"/> as JSON.
///
/// Location is <c>%LOCALAPPDATA%\LiveArchiveEncoder\config.json</c>, falling back
/// to a file next to the executable when there is no user profile (service
/// accounts, some CI sandboxes). Writes are atomic -- a crash mid-save leaves the
/// previous file intact rather than a truncated one that would reset everything.
/// </summary>
public static class ConfigStore
{
    public static string DefaultPath
    {
        get
        {
            string? appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = string.IsNullOrEmpty(appData)
                ? AppContext.BaseDirectory
                : Path.Combine(appData, "LiveArchiveEncoder");
            return Path.Combine(dir, "config.json");
        }
    }

    public static UserConfig Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        if (!File.Exists(file)) return new UserConfig();
        try
        {
            UserConfig? cfg = JsonSerializer.Deserialize(
                File.ReadAllText(file), UserConfigJsonContext.Default.UserConfig);
            return cfg ?? new UserConfig();
        }
        catch (Exception exc) when (exc is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt file must never keep the program from starting.
            return new UserConfig();
        }
    }

    public static bool Save(UserConfig cfg, string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            string? dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            cfg.SavedAt = DateTimeOffset.Now;
            string json = JsonSerializer.Serialize(cfg, UserConfigJsonContext.Default.UserConfig);

            string temp = file + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    public static UserConfig Capture(Config c) => new()
    {
        InputPath = c.InputPath?.FullName,
        OutputPath = c.OutputPath?.FullName,
        OutputIsAuto = c.OutputIsAuto,
        Encoder = c.Encoder?.Name,
        AudioEncoder = c.AudioEncoder,
        StartTimeSeconds = c.StartTimeSeconds,
        EndTimeSeconds = c.EndTimeSeconds,
        Fps = c.Fps,
        FpsLevels = [.. c.FpsLevels],
        Limit = c.Limit,
        Recover = c.Recover,
        Window = c.Window,
        Qv = c.Qv,
        Threads = c.Threads,
        BufferFrames = c.BufferFrames,
        AudioKbps = c.AudioKbps,
        AudioChannels = c.AudioChannels,
        AudioRate = c.AudioRate,
        Width = c.Width,
        Height = c.Height,
        Crop43 = c.Crop43,
        ScaleAlgo = c.ScaleAlgo,
        Deinterlace = c.Deinterlace,
        DeintMode = c.DeintMode,
        DeintParity = c.DeintParity,
        DeintMarkedOnly = c.DeintMarkedOnly,
        Hwaccel = c.Hwaccel,
        X264Preset = c.X264Preset,
        X264Tune = c.X264Tune,
        X264Profile = c.X264Profile,
        X264Level = c.X264Level,
        Turbo = c.Turbo,
        Parallel = c.Parallel,
        Language = c.Language,
        FfmpegSource = c.FfmpegSource,
        FfmpegProxy = c.FfmpegProxy,
        PlayMode = UI.Player.ModeName(c.Play),
        PlayerPath = c.PlayerPath,
        Watermark = CloneWatermark(c.Watermark),
        Recompress = CloneRecompress(c.Recompress),
        EraPreset = c.EraPreset,
    };

    private static WatermarkSettings CloneWatermark(WatermarkSettings s) => new()
    {
        Enabled = s.Enabled,
        Text = s.Text,
        Position = s.Position,
        FontSize = s.FontSize,
        Margin = s.Margin,
        FontColor = s.FontColor,
        FontFile = s.FontFile,
    };

    private static RecompressSettings CloneRecompress(RecompressSettings s) => new()
    {
        Enabled = s.Enabled,
        Args = s.Args,
        Container = s.Container,
        KeepIntermediate = s.KeepIntermediate,
    };

    /// <summary>
    /// Copy a stored snapshot onto a live <see cref="Config"/>.
    /// Anything the current FFmpeg build cannot provide is left alone, so a
    /// config written against a different FFmpeg never yields a broken state.
    /// </summary>
    public static void Apply(UserConfig stored, Config c, IReadOnlyList<EncoderInfo> encoders)
    {
        if (stored.InputPath is { Length: > 0 } input && File.Exists(input))
            c.InputPath = new FileInfo(input);

        if (stored.OutputPath is { Length: > 0 } output)
        {
            c.OutputPath = new FileInfo(output);
            c.OutputIsAuto = stored.OutputIsAuto;
        }

        if (stored.Encoder is { Length: > 0 } name)
        {
            EncoderInfo? match = encoders.FirstOrDefault(
                e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) c.Encoder = match;
        }

        if (stored.AudioEncoder is { Length: > 0 } audio) c.AudioEncoder = audio;

        c.StartTimeSeconds = stored.StartTimeSeconds;
        c.EndTimeSeconds = stored.EndTimeSeconds;
        c.Fps = stored.Fps;
        if (stored.FpsLevels.Count > 0) c.FpsLevels = [.. stored.FpsLevels];
        c.Limit = stored.Limit;
        c.Recover = stored.Recover;
        c.Window = stored.Window;
        c.Qv = stored.Qv;
        c.Threads = stored.Threads;
        c.BufferFrames = stored.BufferFrames;
        c.AudioKbps = stored.AudioKbps;
        c.AudioChannels = stored.AudioChannels;
        c.AudioRate = stored.AudioRate;
        c.Width = stored.Width;
        c.Height = stored.Height;
        c.Crop43 = stored.Crop43;
        c.ScaleAlgo = stored.ScaleAlgo;
        c.Deinterlace = stored.Deinterlace;
        c.DeintMode = stored.DeintMode;
        c.DeintParity = stored.DeintParity;
        c.DeintMarkedOnly = stored.DeintMarkedOnly;
        c.Hwaccel = SanitiseHwaccel(stored.Hwaccel);
        c.X264Preset = stored.X264Preset;
        c.X264Tune = stored.X264Tune;
        c.X264Profile = stored.X264Profile;
        c.X264Level = stored.X264Level;
        c.Turbo = stored.Turbo;
        // Never restore a nonsensical shard count from an older/corrupt file.
        c.Parallel = Math.Max(1, stored.Parallel);
        c.Language = stored.Language;

        // A source that is not installed is still remembered (so an install picks
        // it up later). "path" means "stay on the system FFmpeg" and is the only
        // non-distribution value accepted; anything else unrecognised falls back
        // to the default so a typo can never make discovery fail.
        string wanted = (stored.FfmpegSource ?? "").Trim().ToLowerInvariant();
        if (wanted == "path") c.FfmpegSource = "path";
        else c.FfmpegSource = FFmpeg.FfmpegCatalog.Find(wanted) is null
            ? FFmpeg.FfmpegCatalog.DefaultId
            : wanted;
        c.FfmpegProxy = stored.FfmpegProxy;
        c.Play = UI.Player.ParseMode(stored.PlayMode);
        c.PlayerPath = stored.PlayerPath;

        // Restored whole rather than field by field: both stages are plain data
        // with no dependency on the FFmpeg build, so there is nothing here that
        // could be unavailable the way an encoder can be.
        c.Watermark = CloneWatermark(stored.Watermark);
        c.Recompress = CloneRecompress(stored.Recompress);
        c.EraPreset = stored.EraPreset;
    }

    /// <summary>
    /// A stored method name is only a request: the driver and the device are
    /// checked at encode time, not here. What is rejected at load is the shape --
    /// an empty string, or a v7.4 file that wrote a boolean-ish value. Anything
    /// unrecognised becomes "off", which is the safe default on headless boxes.
    /// </summary>
    private static string SanitiseHwaccel(string? value)
    {
        string name = (value ?? "").Trim();
        if (name.Length == 0) return "off";
        if (name.Equals("off", StringComparison.OrdinalIgnoreCase)) return "off";
        if (name.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "auto";
        // Method names are what `ffmpeg -hwaccels` prints: lowercase, no spaces.
        return name.Any(char.IsWhiteSpace) ? "off" : name.ToLowerInvariant();
    }
}
