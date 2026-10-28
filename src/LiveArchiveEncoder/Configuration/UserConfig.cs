using System.Text.Json;
using System.Text.Json.Serialization;
using LiveArchiveEncoder.Core;

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
internal sealed partial class UserConfigJsonContext : JsonSerializerContext;

/// <summary>
/// Persisted settings for the interactive shell.
///
/// The shape mirrors <see cref="Config"/> but stores the encoder by name only:
/// encoder availability depends on the FFmpeg build that happens to be present,
/// so a name that cannot be resolved on load is dropped instead of trusted.
/// </summary>
public sealed class UserConfig
{
    /// <summary>
    /// 6 = v7.9.6 (pixel format, colour tags, dither). Only informational: every
    /// field added since v5 is optional, so an older file loads as-is and a
    /// newer one is not refused -- the program never reads this number back.
    /// </summary>
    public const int CurrentVersion = 6;

    public int Version { get; set; } = CurrentVersion;

    public string? InputPath { get; set; }
    public string? OutputPath { get; set; }
    public bool OutputIsAuto { get; set; } = true;

    public string? Encoder { get; set; }

    /// <summary>Container override ("avi", "mkv", ...); null = the encoder's default.</summary>
    public string? Container { get; set; }

    public string? AudioEncoder { get; set; }

    public double StartTimeSeconds { get; set; }
    public double? EndTimeSeconds { get; set; }
    public double Fps { get; set; } = Constants.DefaultFps;
    public List<double> FpsLevels { get; set; } = [13.0, 8.0, 5.0, 3.0];

    /// <summary>
    /// v7.7: bitrate thresholds per ladder rung. Absent from v2 files, which is
    /// fine -- an empty list means "derive from Limit/Recover", i.e. v7.6 rules.
    /// </summary>
    public List<FpsRungSetting> FpsLevelKbps { get; set; } = [];

    public double Limit { get; set; } = 200.0;
    public double Recover { get; set; } = 150.0;
    public double Window { get; set; } = 2.0;
    public int Qv { get; set; } = 8;
    public int Threads { get; set; } = 16;
    public int BufferFrames { get; set; } = 6;
    public int AudioKbps { get; set; } = 32;
    public int AudioChannels { get; set; } = 1;
    public int? AudioRate { get; set; }

    /// <summary>
    /// v7.9: absolute stream index of the video track to encode; null is "the
    /// first video stream". Absent from older files, which is fine -- System.Text.Json
    /// leaves the property alone and the default is the v7.8 behaviour.
    /// </summary>
    public int? VideoStream { get; set; }

    /// <summary>v7.9: absolute stream index of the audio track to keep; null is the first.</summary>
    public int? AudioStream { get; set; }

    public int Width { get; set; } = Constants.DefaultWidth;
    public int Height { get; set; } = Constants.DefaultHeight;

    /// <summary>
    /// v7.8: how the picture is fitted into the frame. "crop" fills it and
    /// discards the overflow (the old "crop 4:3" behaviour), "fit" keeps the
    /// whole picture and pads it, "stretch" fills it and distorts.
    /// </summary>
    public string FitMode { get; set; } = "crop";
    public string CropAnchor { get; set; } = "center";
    public string PadColor { get; set; } = "black";

    /// <summary>
    /// v7.7 and earlier stored "crop 4:3" as a boolean. Kept so an old file
    /// still means what it meant -- "no" was a plain stretch -- and left null
    /// by <see cref="ConfigStore.Capture"/> from v7.8 on.
    /// </summary>
    public bool? Crop43 { get; set; }

    public string ScaleAlgo { get; set; } = "neighbor";

    /// <summary>
    /// v7.9.6: output pixel format, null/"auto" for the automatic choice.
    /// Absent from older files, which is fine -- null is the old behaviour.
    /// </summary>
    public string? PixFmt { get; set; }

    /// <summary>v7.9.6: colour metadata overrides; null keeps the source's.</summary>
    public string? ColorRange { get; set; }
    public string? ColorSpace { get; set; }
    public string? ColorPrimaries { get; set; }
    public string? ColorTransfer { get; set; }

    /// <summary>v7.9.6: swscale dither mode for low-colour output; "auto" = no flag.</summary>
    public string Dither { get; set; } = "auto";

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

    public int Parallel { get; set; } = 1;
    public string Language { get; set; } = "en";

    /// <summary>Selected FFmpeg distribution id (btbn / gyan / avbuild / nonfree / path).</summary>
    public string FfmpegSource { get; set; } = FFmpeg.FfmpegCatalog.DefaultId;

    /// <summary>Proxy for FFmpeg downloads; null means "ask every time".</summary>
    public string? FfmpegProxy { get; set; }

    /// <summary>
    /// v7.9.8.3: GitHub download front used for FFmpeg and for the NNEDI3
    /// weights, e.g. <c>https://gh-proxy.com/</c>. Null means "straight from
    /// GitHub". Absent from older files, which is the same thing.
    /// </summary>
    public string? GhProxy { get; set; }

    /// <summary>v7.9.8.3: explicit NNEDI3 weights file; null uses the managed install.</summary>
    public string? NnediWeights { get; set; }

    /// <summary>
    /// v7.9.8.5: look for a newer build when the shell starts. Absent from older
    /// files, so the default (on) applies to them -- and "on" is what a program
    /// that can now update itself should do.
    /// </summary>
    public bool AutoUpdateCheck { get; set; } = true;

    /// <summary>"off" / "ffplay" / "external".</summary>
    public string PlayMode { get; set; } = "off";

    /// <summary>External player executable; empty means the shell association.</summary>
    public string? PlayerPath { get; set; }

    // ---- v7.6: the post-processing stage ----
    // Absent from v1 files, which is fine: System.Text.Json leaves a property
    // alone when the JSON does not mention it, so an old config keeps the
    // default (the stage off) instead of nulling anything out.

    public RecompressSettings Recompress { get; set; } = new();

    /// <summary>
    /// v7.9.1: subtitle request. Absent from older files, which is fine -- the
    /// defaults mean "burn, nothing picked", i.e. the v7.9.0 behaviour.
    /// </summary>
    public SubtitleConfig Subtitles { get; set; } = new();

    /// <summary>Last applied era preset id, or null for "custom settings".</summary>
    public string? EraPreset { get; set; }

    /// <summary>
    /// v7.9.5.1: whether a preset's second pass uses the site's own codec.
    /// Absent from an older file, which is fine -- the default is true.
    /// </summary>
    public bool EraNative { get; set; } = true;

    /// <summary>
    /// v7.9.6.3: frame shape for the applied preset -- "source", "4:3" or
    /// "16:9". Null in a file written before the switch existed, which reads as
    /// "source" and therefore reproduces the old output exactly.
    /// </summary>
    public string? EraAspect { get; set; }

    /// <summary>
    /// v7.9.7: which H.264 encoder the run uses where it says H.264 --
    /// "libx264" or "libopenh264". Null means "ask the preset".
    /// </summary>
    public string? H264Impl { get; set; }

    /// <summary>
    /// v7.9.5.1: presets the user wrote. Null (absent from the file) means
    /// "leave whatever is loaded alone" -- loading a named profile that predates
    /// this field must not delete presets someone added in the meantime.
    /// </summary>
    public List<CustomEraConfig>? CustomEras { get; set; }

    /// <summary>Ignored on load; present so the file is self-describing.</summary>
    public DateTimeOffset? SavedAt { get; set; }
}

/// <summary>
/// One user-written era/site preset, as it is stored.
///
/// The shape mirrors <see cref="PostProcess.EraPreset"/> but with plain values
/// only: no delegate, no computed fragment, nothing that would make the JSON
/// contract depend on the code that reads it. The fragment is regenerated from
/// these numbers on load, so a stored preset can never carry a stale argument
/// list.
/// </summary>
public sealed class CustomEraConfig
{
    public string Id { get; set; } = "";

    /// <summary>v7.9.7: the site the row belongs to, used to group the menu.</summary>
    public string? Site { get; set; }

    /// <summary>v7.9.7: the year the row describes, 0 for a range-only preset.</summary>
    public int Year { get; set; }

    /// <summary>"era" (both layers) or "site" (second pass only).</summary>
    public string Kind { get; set; } = "site";

    public int YearFrom { get; set; } = 2005;
    public int YearTo { get; set; } = 2010;

    // ---- live layer, null for a site preset ----
    public string? Encoder { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? Fps { get; set; }

    /// <summary>
    /// v7.9.6.1: the period's adaptive frame-rate ladder, highest first. Null
    /// (absent from older files) means "derive one from <see cref="Fps"/>",
    /// which is exactly the pre-v7.9.6.1 behaviour.
    /// </summary>
    public List<double>? FpsLevels { get; set; }

    public double? LimitKbps { get; set; }

    /// <summary>
    /// v7.9.8.2: the live pass's quality, in both scales the encoders read.
    /// Absent from older files, which is why the load path falls back to the
    /// band the row's own ceiling implies rather than to zero.
    /// </summary>
    public int? LiveQv { get; set; }
    public int? LiveCrf { get; set; }

    /// <summary>
    /// v7.9.8.2: the rest of the capture pipeline -- how long the ladder
    /// averages, how deep the frame queue is, and which deinterlacer existed.
    /// Absent from older files, where the year's own row supplied them.
    /// </summary>
    public double? WindowSeconds { get; set; }
    public int? BufferFrames { get; set; }
    public string? Deinterlace { get; set; }

    public string? ScaleAlgo { get; set; }
    public string? AudioEncoder { get; set; }
    public int? AudioKbps { get; set; }
    public int? AudioChannels { get; set; }
    public int? AudioRate { get; set; }

    // ---- second pass ----
    public int RcWidth { get; set; } = 320;
    public int RcHeight { get; set; } = 240;
    public double RcFps { get; set; } = 15;
    public int RcVideoKbps { get; set; } = 300;
    public int RcCrf { get; set; } = 28;
    public string RcProfile { get; set; } = "baseline";
    public int RcAudioKbps { get; set; } = 32;
    public int RcAudioRate { get; set; } = 22050;
    public int RcAudioChannels { get; set; } = 1;
    public string VideoCodec { get; set; } = "libx264";
    public string AudioCodec { get; set; } = "aac";
    public string Container { get; set; } = "mp4";

    /// <summary>v7.9.7: "libopenh264" or "libx264" for a row whose codec is H.264.</summary>
    public string? H264Impl { get; set; }

    public string? Note { get; set; }
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
    /// <summary>
    /// Folder holding <c>config.json</c> and the <c>configs</c> shelf of named
    /// profiles. <c>LAE_CONFIG_DIR</c> overrides it -- that is what a portable
    /// install (config next to the exe on a USB stick) and an isolated test run
    /// both need.
    /// </summary>
    public static string RootDirectory
    {
        get
        {
            string? over = Environment.GetEnvironmentVariable("LAE_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(over)) return over;

            string? appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(appData)
                ? AppContext.BaseDirectory
                : Path.Combine(appData, "LiveArchiveEncoder");
        }
    }

    public static string DefaultPath => Path.Combine(RootDirectory, "config.json");

    public static UserConfig Load(string? path = null)
        => TryLoad(path ?? DefaultPath) ?? new UserConfig();

    /// <summary>
    /// Same read, but a missing or unparsable file returns null instead of an
    /// empty <see cref="UserConfig"/> (v7.9.3.1).
    ///
    /// The difference matters for a named profile: <see cref="Load"/> answers
    /// "start from defaults", which is right at startup and wrong when the
    /// caller is about to install the result over a live configuration -- a
    /// truncated profile used to reset every setting instead of being reported
    /// as the bad file it is.
    /// </summary>
    public static UserConfig? TryLoad(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            UserConfig? loaded = JsonSerializer.Deserialize(
                File.ReadAllText(path), UserConfigJsonContext.Default.UserConfig);
            if (loaded is not null) Normalise(loaded);
            return loaded;
        }
        catch (Exception exc) when (exc is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt file must never keep the program from starting.
            return null;
        }
    }

    /// <summary>
    /// Repair a snapshot that spelled a non-nullable member as <c>null</c>.
    ///
    /// <para>System.Text.Json takes <c>"FpsLevels": null</c> literally: the
    /// property ends up null even though its type forbids it, and every reader
    /// then dereferences it unguarded. Only <see cref="Apply"/> used to defend
    /// itself, so the same file crashed whichever path did not go through Apply
    /// -- <c>--emit-args=file.json</c> died with a NullReferenceException inside
    /// <c>CliArgs.LadderSpec</c>.</para>
    ///
    /// <para>One repair at the read boundary means every caller sees the shape
    /// the type promises, instead of each one re-checking.</para>
    /// </summary>
    private static void Normalise(UserConfig c)
    {
        // An empty ladder is not a ladder: the encoder always needs a current
        // rung, so both "absent" and "blank" mean the built-in four.
        c.FpsLevels = c.FpsLevels is { Count: > 0 } levels ? levels : [13.0, 8.0, 5.0, 3.0];
        c.FpsLevelKbps = c.FpsLevelKbps is { } rungs ? [.. rungs.Where(r => r is not null)] : [];

        // null and [] differ here on purpose: null means "the file predates the
        // field, leave the presets already loaded alone", [] means "none".
        c.CustomEras = c.CustomEras is { } eras ? [.. eras.Where(e => e is not null)] : null;

        c.Recompress = CloneRecompress(c.Recompress ?? new RecompressSettings());
        c.Subtitles = CloneSubtitles(c.Subtitles ?? new SubtitleConfig());

        // The rest are strings with a documented default; null would reach an
        // FFmpeg argument or a switch statement and become a null reference or
        // a silent "no such option" further from the cause.
        c.Dither = c.Dither.Or("auto");
        c.Deinterlace = c.Deinterlace.Or("off");
        c.DeintMode = c.DeintMode.Or("send_frame");
        c.DeintParity = c.DeintParity.Or("auto");
        c.Hwaccel = c.Hwaccel.Or("off");
        c.X264Preset = c.X264Preset.Or("medium");
        c.X264Tune = c.X264Tune.Or("");
        c.X264Profile = c.X264Profile.Or("");
        c.X264Level = c.X264Level.Or("");
        c.FitMode = c.FitMode.Or("crop");
        c.CropAnchor = c.CropAnchor.Or("center");
        c.PadColor = c.PadColor.Or("black");
        c.ScaleAlgo = c.ScaleAlgo.Or("neighbor");
        c.Language = c.Language.Or("en");
        c.FfmpegSource = c.FfmpegSource.Or(FFmpeg.FfmpegCatalog.DefaultId);
        c.PlayMode = c.PlayMode.Or("off");
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
        Container = c.Container,
        AudioEncoder = c.AudioEncoder,
        StartTimeSeconds = c.StartTimeSeconds,
        EndTimeSeconds = c.EndTimeSeconds,
        Fps = c.Fps,
        FpsLevels = [.. c.FpsLevels],
        FpsLevelKbps = [.. c.FpsLevelKbps.Select(CloneRung)],
        Limit = c.Limit,
        Recover = c.Recover,
        Window = c.Window,
        Qv = c.Qv,
        Threads = c.Threads,
        BufferFrames = c.BufferFrames,
        AudioKbps = c.AudioKbps,
        AudioChannels = c.AudioChannels,
        AudioRate = c.AudioRate,
        VideoStream = c.VideoStream,
        AudioStream = c.AudioStream,
        Width = c.Width,
        Height = c.Height,
        FitMode = c.FitMode,
        CropAnchor = c.CropAnchor,
        PadColor = c.PadColor,
        ScaleAlgo = c.ScaleAlgo,
        PixFmt = c.PixFmt,
        ColorRange = c.ColorRange,
        ColorSpace = c.ColorSpace,
        ColorPrimaries = c.ColorPrimaries,
        ColorTransfer = c.ColorTransfer,
        Dither = c.Dither,
        Deinterlace = c.Deinterlace,
        DeintMode = c.DeintMode,
        DeintParity = c.DeintParity,
        DeintMarkedOnly = c.DeintMarkedOnly,
        Hwaccel = c.Hwaccel,
        X264Preset = c.X264Preset,
        X264Tune = c.X264Tune,
        X264Profile = c.X264Profile,
        X264Level = c.X264Level,
        Parallel = c.Parallel,
        Language = c.Language,
        FfmpegSource = c.FfmpegSource,
        FfmpegProxy = c.FfmpegProxy,
        GhProxy = c.GhProxy,
        NnediWeights = c.NnediWeights,
        AutoUpdateCheck = c.AutoUpdateCheck,
        PlayMode = UI.Player.ModeName(c.Play),
        PlayerPath = c.PlayerPath,
        Recompress = CloneRecompress(c.Recompress),
        Subtitles = CloneSubtitles(c.Subtitles),
        EraPreset = c.EraPreset,
        EraNative = c.EraNative,
        EraAspect = c.EraAspect,
        H264Impl = c.H264Impl,
        CustomEras = [.. c.CustomEras.Select(EraConfigMap.ToConfig)],
    };

    private static SubtitleConfig CloneSubtitles(SubtitleConfig s) => new()
    {
        // v7.9.3.1: a hand-edited or third-party file can spell any of these as
        // null, and a null in a non-nullable slot only surfaces later as an
        // NRE somewhere unrelated (Config.Subtitles.Files.Count, Args.Length).
        Mode = s.Mode.Or("burn"),
        Files = s.Files is null ? [] : [.. s.Files.Where(f => f.HasText)],
        Stream = s.Stream,
        Codec = s.Codec,
        FontDir = s.FontDir,
        FallbackFont = s.FallbackFont,
        BoxAspect = s.BoxAspect.Or("source"),
    };

    private static FpsRungSetting CloneRung(FpsRungSetting s) => new()
    {
        Fps = s.Fps,
        DropAboveKbps = s.DropAboveKbps,
        RiseBelowKbps = s.RiseBelowKbps,
    };

    private static RecompressSettings CloneRecompress(RecompressSettings s) => new()
    {
        Enabled = s.Enabled,
        Args = s.Args.Or(""),
        Container = s.Container.Or("mp4"),
        // v7.9.8.3: the extra passes are part of the request, so they are copied
        // rather than shared -- the file is written from this snapshot, and a
        // later menu edit must not change what it says.
        Extra = s.Extra is null
            ? []
            : [.. s.Extra.Where(p => p is not null).Select(p => p.Clone())],
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

        if (stored.Container is { Length: > 0 } container) c.Container = container;

        if (stored.AudioEncoder is { Length: > 0 } audio) c.AudioEncoder = audio;

        c.StartTimeSeconds = stored.StartTimeSeconds;
        c.EndTimeSeconds = stored.EndTimeSeconds;
        c.Fps = stored.Fps;
        // v7.9.3.1: a JSON file may spell any list (or nested object) as null.
        // System.Text.Json honours that literally, so every Count/Select below
        // has to survive it -- a `"FpsLevels": null` used to crash the shell on
        // startup with an NRE, which is a lot of blast radius for one typo.
        if (stored.FpsLevels is { Count: > 0 } levels) c.FpsLevels = [.. levels];
        // Thresholds are keyed by FPS, so a rung that is no longer on the ladder
        // is dropped here instead of silently resurrecting a stale value later.
        if (stored.FpsLevelKbps is { Count: > 0 } rungs)
            c.FpsLevelKbps = [.. rungs.Select(CloneRung)];
        c.Limit = stored.Limit;
        c.Recover = stored.Recover;
        c.Window = stored.Window;
        c.Qv = stored.Qv;
        c.Threads = stored.Threads;
        c.BufferFrames = stored.BufferFrames;
        c.AudioKbps = stored.AudioKbps;
        c.AudioChannels = stored.AudioChannels;
        c.AudioRate = stored.AudioRate;
        // A remembered stream index is only a request: the encoder re-checks it
        // against the actual source and refuses one that is out of range or of
        // the wrong type, so a stale index cannot silently pick the wrong track.
        c.VideoStream = stored.VideoStream;
        c.AudioStream = stored.AudioStream;
        c.Width = stored.Width;
        c.Height = stored.Height;
        c.FitMode = Core.EncoderMath.CanonicalFitMode(stored.FitMode);
        c.CropAnchor = Core.EncoderMath.CanonicalCropAnchor(stored.CropAnchor);
        c.PadColor = stored.PadColor.Or("black").Trim();
        // Migration: "crop 4:3 = no" meant no crop at all, i.e. a plain stretch.
        if (stored.Crop43 is false) c.FitMode = "stretch";
        c.ScaleAlgo = stored.ScaleAlgo.Or(c.ScaleAlgo);
        // v7.9.6: a stored format that this build no longer knows is dropped
        // rather than trusted -- an unknown transport format would only fail
        // later, inside a frame-size calculation with no context.
        c.PixFmt = Core.PixelFormatCatalog.TryGet(stored.PixFmt, out Core.PixFmtInfo pix)
            ? pix.Name
            : null;
        c.ColorRange = SanitiseColor(stored.ColorRange, Constants.ColorRanges);
        c.ColorSpace = SanitiseColor(stored.ColorSpace, Constants.ColorSpaces);
        c.ColorPrimaries = SanitiseColor(stored.ColorPrimaries, Constants.ColorPrimaries);
        c.ColorTransfer = SanitiseColor(stored.ColorTransfer, Constants.ColorTransfers);
        c.Dither = Constants.DitherModes.Contains((stored.Dither ?? "").Trim().ToLowerInvariant())
            ? stored.Dither!.Trim().ToLowerInvariant()
            : "auto";
        c.Deinterlace = stored.Deinterlace.Or(c.Deinterlace);
        c.DeintMode = stored.DeintMode.Or(c.DeintMode);
        c.DeintParity = stored.DeintParity.Or(c.DeintParity);
        c.DeintMarkedOnly = stored.DeintMarkedOnly;
        c.Hwaccel = SanitiseHwaccel(stored.Hwaccel);
        // Every one of these is read with .Length / fed to FFmpeg as a value, so
        // a null has to become the safe spelling rather than travel onwards.
        c.X264Preset = stored.X264Preset.Or("medium");
        c.X264Tune = stored.X264Tune.Or("");
        c.X264Profile = stored.X264Profile.Or("");
        c.X264Level = stored.X264Level.Or("");
        // Never restore a nonsensical shard count from an older/corrupt file.
        c.Parallel = Math.Max(1, stored.Parallel);
        c.Language = stored.Language.HasText ? stored.Language : c.Language;

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
        // v7.9.8.3: a prefix that no longer parses is dropped rather than carried
        // into a download, where it would turn every URL into nonsense. "off" is
        // the one literal that survives: it records a decision, and a decision
        // must outlive the run that made it (null means "never asked", which is
        // what lets Nnedi probe and pick a route on its own).
        c.GhProxy = string.Equals(stored.GhProxy?.Trim(), FFmpeg.GitHubProxy.Off,
                StringComparison.OrdinalIgnoreCase)
            ? FFmpeg.GitHubProxy.Off
            : FFmpeg.GitHubProxy.Normalise(stored.GhProxy).Prefix;
        c.NnediWeights = stored.NnediWeights;
        c.AutoUpdateCheck = stored.AutoUpdateCheck;
        c.Play = UI.Player.ParseMode(stored.PlayMode);
        c.PlayerPath = stored.PlayerPath;

        // Restored whole rather than field by field: the stage is plain data
        // with no dependency on the FFmpeg build, so there is nothing here that
        // could be unavailable the way an encoder can be. A file that omits or
        // nulls the block keeps the live one.
        if (stored.Recompress is not null) c.Recompress = CloneRecompress(stored.Recompress);
        if (stored.Subtitles is not null) c.Subtitles = CloneSubtitles(stored.Subtitles);
        c.EraPreset = stored.EraPreset;
        c.EraNative = stored.EraNative;
        c.EraAspect = stored.EraAspect;
        c.H264Impl = stored.H264Impl;

        // v7.9.5.1: the user's own presets become part of the live table here,
        // so every later `--era` lookup in this process sees them. A file that
        // does not mention them keeps the ones already loaded (a named profile
        // written before this field existed must not wipe them).
        if (stored.CustomEras is { } rows)
        {
            c.CustomEras = [];
            foreach (CustomEraConfig row in rows)
            {
                PostProcess.EraPreset? preset = EraConfigMap.FromConfig(row);
                if (preset is not null) c.CustomEras.Add(preset);
            }
            PostProcess.EraPresets.SetCustom(c.CustomEras);
        }
    }

    /// <summary>
    /// A stored colour value is only kept when it is one this build offers.
    /// Anything else -- a typo, a value from a newer version, a null -- becomes
    /// null, which means "keep what the source declared". That is the safe
    /// direction: an unrecognised tag would otherwise reach FFmpeg and abort the
    /// run long after the settings file was read.
    /// </summary>
    private static string? SanitiseColor(string? value, string[] allowed)
    {
        string name = (value ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0 || name == "auto" || name == "source") return null;
        return allowed.Contains(name) ? name : null;
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

/// <summary>
/// The stored shape &lt;-&gt; live shape mapping for user-written era presets
/// (v7.9.5.1).
///
/// Loading is deliberately forgiving in the same way the rest of the settings
/// file is: an id that is not a slug, a codec this build of the program has
/// never heard of, or numbers that make no sense are replaced with sane values
/// instead of throwing. A preset is a convenience, not a contract -- and a
/// settings file that refuses to load is worse than one that loads with a row
/// missing.
/// </summary>
public static class EraConfigMap
{
    public static CustomEraConfig ToConfig(PostProcess.EraPreset p) => new()
    {
        Id = p.Id,
        Site = p.Site,
        Year = p.Year,
        Kind = p.Kind == PostProcess.EraKind.Era ? "era" : "site",
        YearFrom = p.YearFrom,
        YearTo = p.YearTo,
        Encoder = p.Encoder,
        Width = p.Width,
        Height = p.Height,
        Fps = p.Fps,
        FpsLevels = p.FpsLevels is { Length: > 0 } levels ? [.. levels] : null,
        LimitKbps = p.LimitKbps,
        LiveQv = p.LiveQv,
        LiveCrf = p.LiveCrf,
        WindowSeconds = p.WindowSeconds,
        BufferFrames = p.BufferFrames,
        Deinterlace = p.Deinterlace,
        ScaleAlgo = p.ScaleAlgo,
        AudioEncoder = p.AudioEncoder,
        AudioKbps = p.AudioKbps,
        AudioChannels = p.AudioChannels,
        AudioRate = p.AudioRate,
        RcWidth = p.RcWidth,
        RcHeight = p.RcHeight,
        RcFps = p.RcFps,
        RcVideoKbps = p.RcVideoKbps,
        RcCrf = p.RcCrf,
        RcProfile = p.RcProfile,
        RcAudioKbps = p.RcAudioKbps,
        RcAudioRate = p.RcAudioRate,
        RcAudioChannels = p.RcAudioChannels,
        VideoCodec = p.VideoCodec,
        AudioCodec = p.AudioCodec,
        Container = p.RecompressContainer,
        H264Impl = p.H264Impl,
        Note = p.NoteText,
    };

    public static PostProcess.EraPreset? FromConfig(CustomEraConfig c)
    {
        if (c is null) return null;
        string id = NormaliseId(c.Id);
        if (id.Length == 0) return null;

        bool era = string.Equals(c.Kind, "era", StringComparison.OrdinalIgnoreCase);
        string videoCodec = PostProcess.RecompressCodecs.FindVideo(c.VideoCodec)?.Name ?? "libx264";
        string audioCodec = PostProcess.RecompressCodecs.IsCopyAudio(c.AudioCodec)
            ? PostProcess.RecompressCodecs.CopyAudio
            : PostProcess.RecompressCodecs.FindAudio(c.AudioCodec)?.Name ?? "aac";

        string? container = PostProcess.RecompressCodecs.ValidateContainer(
            videoCodec, audioCodec, c.Container.Or("mp4")) is null
            ? c.Container.Or("mp4").Trim().TrimStart('.').ToLowerInvariant()
            : PostProcess.RecompressCodecs.DefaultContainer(videoCodec, audioCodec) ?? "mp4";

        // v7.9.6.2: a live layer is whatever the file gives, not what the kind
        // implies -- a site row may now carry one too (the built-in ones do,
        // and a hand-written row should not be refused the same fields). An
        // older file with no Width/Height therefore still loads as "second pass
        // only", which is what it meant.
        int? liveWidth = Positive(c.Width, 0);
        int? liveHeight = Positive(c.Height, 0);
        double? liveLimit = Positive(c.LimitKbps, 0);
        if (liveWidth is not null && liveHeight is not null && liveLimit is null)
        {
            // A row that names a frame size but no ceiling meant the site's own
            // video bitrate; that is what the built-in rows do.
            liveLimit = Positive(c.RcVideoKbps, 0);
        }

        return new PostProcess.EraPreset
        {
            Id = id,
            Site = c.Site.HasText ? c.Site.Trim().ToLowerInvariant() : "",
            Year = Math.Clamp(c.Year, 0, 2100),
            BuiltIn = false,
            Kind = era ? PostProcess.EraKind.Era : PostProcess.EraKind.Site,
            YearFrom = Math.Clamp(c.YearFrom, 1990, 2100),
            YearTo = Math.Clamp(Math.Max(c.YearTo, c.YearFrom), 1990, 2100),
            Encoder = c.Encoder.HasText ? c.Encoder.Trim().ToLowerInvariant() : null,
            Width = liveWidth,
            Height = liveHeight,
            Fps = Positive(c.Fps, 0),
            FpsLevels = CleanLevels(c.FpsLevels),
            LimitKbps = liveLimit,
            // v7.9.8.2: a file written before the live quality existed states
            // no number; the band its own ceiling implies is the honest
            // replacement, and it is written back on the next edit.
            LiveQv = Math.Clamp(Value(c.LiveQv, PostProcess.EraPreset.QualityBand(liveLimit).Qv), 2, 31),
            LiveCrf = Math.Clamp(Value(c.LiveCrf, PostProcess.EraPreset.QualityBand(liveLimit).Crf), 0, 51),
            // Same idea for the pipeline: a file written before these fields
            // existed gets the year it claims, not a modern default.
            WindowSeconds = c.WindowSeconds is > 0 and <= 30
                ? c.WindowSeconds.Value
                : PostProcess.EraPresets.LivePipeline(c.YearFrom).Window,
            BufferFrames = c.BufferFrames is >= 2 and <= 64
                ? c.BufferFrames.Value
                : PostProcess.EraPresets.LivePipeline(c.YearFrom).Buffer,
            Deinterlace = c.Deinterlace is { Length: > 0 } deint ? deint.Trim().ToLowerInvariant()
                : PostProcess.EraPresets.LivePipeline(c.YearFrom).Deint,
            ScaleAlgo = c.ScaleAlgo.HasText ? c.ScaleAlgo.Trim().ToLowerInvariant() : (era ? "bicubic" : null),
            AudioEncoder = c.AudioEncoder.HasText ? c.AudioEncoder.Trim().ToLowerInvariant() : null,
            AudioKbps = Positive(c.AudioKbps, 0),
            AudioChannels = Positive(c.AudioChannels, 0),
            AudioRate = Positive(c.AudioRate, 0),
            RcWidth = Positive(c.RcWidth, 320) ?? 320,
            RcHeight = Positive(c.RcHeight, 240) ?? 240,
            RcFps = Positive(c.RcFps, 15) ?? 15,
            RcVideoKbps = Positive(c.RcVideoKbps, 300) ?? 300,
            RcCrf = Math.Clamp(Value(c.RcCrf, 28), 1, 51),
            RcProfile = c.RcProfile is "baseline" or "main" or "high" ? c.RcProfile : "baseline",
            RcAudioKbps = Positive(c.RcAudioKbps, 32) ?? 32,
            RcAudioRate = Positive(c.RcAudioRate, 22050) ?? 22050,
            RcAudioChannels = Math.Clamp(Value(c.RcAudioChannels, 1), 1, 8),
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            RecompressContainer = container,
            H264Impl = c.H264Impl is "libopenh264" ? "libopenh264" : c.H264Impl is "libx264" ? "libx264" : null,
            NoteText = c.Note.HasText ? c.Note.Trim() : null,
            NoteKey = null,
        };
    }

    /// <summary>
    /// Ids are lower-case slugs so a command line can carry them unquoted.
    /// v7.9.7: a slash survives, because a custom row may be written as
    /// <c>mysite/2012</c> to sit next to the built-in <c>site/year</c> rows;
    /// only one is allowed, and the machine host is dropped.
    /// </summary>
    public static string NormaliseId(string? raw)
    {
        if (!raw.HasText) return "";
        var sb = new System.Text.StringBuilder(raw.Length);
        bool slash = false;
        foreach (char ch in raw.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (ch == '/' && !slash && sb.Length > 0)
            {
                sb.Append('/');
                slash = true;
            }
            else if (ch is '_' or '-' or ' ' or '.') sb.Append('_');
        }
        string id = sb.ToString().Trim('_').Trim('/');
        // Keep it short enough to type and to print in a menu column.
        return id.Length > 24 ? id[..24] : id;
    }

    private static int Value(int? value, int fallback) =>
        value is { } v && v != 0 ? v : fallback;

    /// <summary>
    /// Sanitise a stored ladder: finite, positive, descending, at most eight
    /// rungs. A hand-edited file can hold anything -- an unsorted list, a
    /// duplicate, a negative value -- and a non-monotonic ladder only surfaces
    /// later as an unreachable rung. Sorting is the forgiving reading of a file
    /// that meant "these are my rungs, I typed them in a hurry".
    /// </summary>
    private static double[]? CleanLevels(List<double>? levels)
    {
        if (levels is null) return null;

        var values = new List<double>();
        foreach (double value in levels)
        {
            if (!double.IsFinite(value) || value <= 0 || value > 120) continue;
            values.Add(Math.Round(value, 6));
        }
        values.Sort((a, b) => b.CompareTo(a));

        var cleaned = new List<double>(values.Count);
        foreach (double value in values)
        {
            if (cleaned.Count == 0 || value < cleaned[^1] - 1e-9) cleaned.Add(value);
            if (cleaned.Count == 8) break;
        }
        return cleaned.Count > 1 ? [.. cleaned] : null;
    }

    private static int? Positive(int? value, int fallback) =>
        value is { } v && v > 0 ? v : (fallback > 0 ? fallback : null);

    private static double? Positive(double? value, double fallback) =>
        value is { } v && v > 0 && double.IsFinite(v) ? v : (fallback > 0 ? fallback : null);
}
