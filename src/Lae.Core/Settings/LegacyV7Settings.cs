using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lae.Core.Settings;

/// <summary>
/// The v7 settings file, as it exists on disk.
///
/// <para>Deliberately a separate type from <see cref="SettingsDocument"/>: this
/// is a wire format frozen at v7.9.8.6, and it must keep parsing exactly that
/// shape even as the live model moves on. Folding the two together is how the
/// old format becomes impossible to read -- v7 itself carried three
/// compatibility shims (<c>Crop43</c>, an absent <c>Extra</c> list, a
/// <c>PlayMode</c> string) that existed only because the stored shape and the
/// live shape were the same type.</para>
///
/// <para>Every member is nullable or has a default: a v1 file legitimately has
/// two thirds of these absent, and a missing member means "the value v7 would
/// have used", not zero.</para>
/// </summary>
public sealed class LegacyV7Settings
{
    public int Version { get; set; }

    public string? InputPath { get; set; }

    public string? OutputPath { get; set; }

    public bool? OutputIsAuto { get; set; }

    public string? Encoder { get; set; }

    public string? Container { get; set; }

    public string? AudioEncoder { get; set; }

    public double? StartTimeSeconds { get; set; }

    public double? EndTimeSeconds { get; set; }

    public double? Fps { get; set; }

    public List<double?>? FpsLevels { get; set; }

    public List<LegacyRung?>? FpsLevelKbps { get; set; }

    public double? Limit { get; set; }

    public double? Recover { get; set; }

    public double? Window { get; set; }

    public int? Qv { get; set; }

    public int? Threads { get; set; }

    public int? BufferFrames { get; set; }

    public int? AudioKbps { get; set; }

    public int? AudioChannels { get; set; }

    public int? AudioRate { get; set; }

    public int? VideoStream { get; set; }

    public int? AudioStream { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public string? FitMode { get; set; }

    public string? CropAnchor { get; set; }

    public string? PadColor { get; set; }

    /// <summary>v7.7 and earlier stored "crop 4:3" as a boolean; false meant a plain stretch.</summary>
    public bool? Crop43 { get; set; }

    public string? ScaleAlgo { get; set; }

    public string? PixFmt { get; set; }

    public string? ColorRange { get; set; }

    public string? ColorSpace { get; set; }

    public string? ColorPrimaries { get; set; }

    public string? ColorTransfer { get; set; }

    public string? Dither { get; set; }

    public string? Deinterlace { get; set; }

    public string? DeintMode { get; set; }

    public string? DeintParity { get; set; }

    public bool? DeintMarkedOnly { get; set; }

    public string? Hwaccel { get; set; }

    public string? X264Preset { get; set; }

    public string? X264Tune { get; set; }

    public string? X264Profile { get; set; }

    public string? X264Level { get; set; }

    public int? Parallel { get; set; }

    public string? Language { get; set; }

    public string? FfmpegSource { get; set; }

    public string? FfmpegProxy { get; set; }

    public string? GhProxy { get; set; }

    public string? NnediWeights { get; set; }

    public bool? AutoUpdateCheck { get; set; }

    public string? PlayMode { get; set; }

    public string? PlayerPath { get; set; }

    public LegacyRecompress? Recompress { get; set; }

    public LegacySubtitles? Subtitles { get; set; }

    public string? EraPreset { get; set; }

    public bool? EraNative { get; set; }

    public string? EraAspect { get; set; }

    public string? H264Impl { get; set; }

    public List<LegacyCustomEra?>? CustomEras { get; set; }

    public DateTimeOffset? SavedAt { get; set; }
}

/// <summary>One rung of the v7 adaptive ladder.</summary>
public sealed class LegacyRung
{
    public double Fps { get; set; }

    public double? DropAboveKbps { get; set; }

    public double? RiseBelowKbps { get; set; }
}

/// <summary>The v7 second-pass block: one first pass plus optional extra ones.</summary>
public sealed class LegacyRecompress
{
    public bool Enabled { get; set; }

    public string? Args { get; set; }

    public string? Container { get; set; }

    public List<LegacyPass?>? Extra { get; set; }
}

/// <summary>One additional v7 pass.</summary>
public sealed class LegacyPass
{
    public string? Args { get; set; }

    public string? Container { get; set; }
}

/// <summary>The v7 subtitle block.</summary>
public sealed class LegacySubtitles
{
    public string? Mode { get; set; }

    public List<string?>? Files { get; set; }

    public int? Stream { get; set; }

    public string? Codec { get; set; }

    public string? FontDir { get; set; }

    public string? FallbackFont { get; set; }

    public string? BoxAspect { get; set; }
}

/// <summary>One user-written preset, as v7 stored it.</summary>
public sealed class LegacyCustomEra
{
    public string? Id { get; set; }

    public string? Site { get; set; }

    public int? Year { get; set; }

    public string? Kind { get; set; }

    public int? YearFrom { get; set; }

    public int? YearTo { get; set; }

    public string? Encoder { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public double? Fps { get; set; }

    public List<double?>? FpsLevels { get; set; }

    public double? LimitKbps { get; set; }

    public int? LiveQv { get; set; }

    public int? LiveCrf { get; set; }

    public double? WindowSeconds { get; set; }

    public int? BufferFrames { get; set; }

    public string? Deinterlace { get; set; }

    public string? ScaleAlgo { get; set; }

    public string? AudioEncoder { get; set; }

    public int? AudioKbps { get; set; }

    public int? AudioChannels { get; set; }

    public int? AudioRate { get; set; }

    public int? RcWidth { get; set; }

    public int? RcHeight { get; set; }

    public double? RcFps { get; set; }

    public int? RcVideoKbps { get; set; }

    public int? RcCrf { get; set; }

    public string? RcProfile { get; set; }

    public int? RcAudioKbps { get; set; }

    public int? RcAudioRate { get; set; }

    public int? RcAudioChannels { get; set; }

    public string? VideoCodec { get; set; }

    public string? AudioCodec { get; set; }

    public string? Container { get; set; }

    public string? H264Impl { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// Reads a v7 file. Separated from the v8 contract on purpose: the v7 file is
/// PascalCase with no naming policy, and putting both shapes in one context
/// would mean one of them lies about its names.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(LegacyV7Settings))]
public sealed partial class LegacyV7JsonContext : JsonSerializerContext;
