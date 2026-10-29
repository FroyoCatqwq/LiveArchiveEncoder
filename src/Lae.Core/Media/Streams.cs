// v8 migration: the media-description half of v7's Models.cs, copied verbatim
// except for the namespace. The v7 tree still builds during the migration so the
// comparison matrix has an oracle to compare against; this copy is deleted with
// that tree at v8.0.0. Logic is unchanged on purpose -- a move that also edits is
// a move nobody can review.
namespace Lae.Core;

/// <summary>What to do with the file once an encode finishes.</summary>
public enum PlayMode
{
    Off = 0,
    /// <summary>ffplay from the active FFmpeg build.</summary>
    Ffplay = 1,
    /// <summary>A user-supplied executable; falls back to the shell association when unset.</summary>
    External = 2,
}

/// <summary>Video stream information reported by ffprobe.</summary>
public sealed class StreamInfo
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string PixFmt { get; init; } = "yuv420p";
    public string? ColorRange { get; init; }
    public string? ColorSpace { get; init; }
    public string? ColorPrimaries { get; init; }
    public string? ColorTransfer { get; init; }
}

/// <summary>
/// v7.9.6: the colour metadata the user pinned, as opposed to what the source
/// declared. A null field means "keep the source's value".
/// </summary>
public sealed record ColorOverride(string? Range, string? Space, string? Primaries, string? Transfer)
{
    public static readonly ColorOverride None = new(null, null, null, null);

    public bool Any => Range is not null || Space is not null || Primaries is not null || Transfer is not null;

    /// <summary>Build from loose option values; blanks and "source" mean "not set".</summary>
    public static ColorOverride Parse(string? range, string? space, string? primaries, string? transfer) =>
        new(Clean(range), Clean(space), Clean(primaries), Clean(transfer));

    private static string? Clean(string? value)
    {
        string text = (value ?? "").Trim().ToLowerInvariant();
        return text is "" or "auto" or "source" or "unknown" ? null : text;
    }
}

/// <summary>
/// One stream of the source, as ffprobe describes it.
///
/// v7.9: a source may carry several video tracks (a VOB with an angle, an MKV
/// with a duplicate encode) and several audio tracks (language tracks), and
/// the user needs to be able to name one. <see cref="Index"/> is the absolute
/// stream index -- the number ffprobe prints and the number the user types.
/// </summary>
/// <param name="Index">Absolute stream index (what ffprobe shows as <c>index=</c>).</param>
/// <param name="Type">codec_type: video, audio, subtitle, ...</param>
/// <param name="Codec">codec_name, e.g. h264 or ac3.</param>
/// <param name="Language">Language tag, when the container carries one.</param>
/// <param name="Title">Stream title, when the container carries one.</param>
/// <param name="Video">Video metadata; null for every other stream type.</param>
public sealed record SourceStream(
    int Index, string Type, string? Codec, string? Language, string? Title, StreamInfo? Video = null);

/// <summary>Capabilities reported by `ffmpeg -h encoder=NAME`.</summary>
public sealed class EncoderCaps
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Flags { get; init; } = "";
    public List<string> SupportedPixFmts { get; init; } = [];
    public List<double> SupportedRates { get; init; } = [];
    public bool HasCrf { get; init; }
    public bool HasQscale { get; init; }
}

/// <summary>A video encoder discovered from `ffmpeg -encoders`.</summary>
public sealed class EncoderInfo
{
    public string Name { get; init; } = "";
    public string Flags { get; init; } = "";
    public string Description { get; init; } = "";
    public string Codec { get; init; } = "";
    public string Family { get; init; } = "";
    public string Extension { get; init; } = "avi";
    public string FormatName { get; init; } = "avi";
    public EncoderCaps? Caps { get; set; }

    /// <summary>
    /// The same encoder writing another container. Everything downstream
    /// (muxer argument, output extension check, audio codec choice, concat
    /// eligibility) reads <see cref="FormatName"/>, so the selection is applied
    /// by swapping in a copy rather than by threading a second value through
    /// every call site.
    /// </summary>
    public EncoderInfo WithContainer(string extension, string formatName) => new()
    {
        Name = Name,
        Flags = Flags,
        Description = Description,
        Codec = Codec,
        Family = Family,
        Extension = extension,
        FormatName = formatName,
        Caps = Caps,
    };
}

/// <summary>
/// A rung of the adaptive FPS ladder as the user configured it.
///
/// <see cref="Fps"/> is the only mandatory part. When a threshold is null the
/// encoder derives it from the run's limit/recover values, which is exactly what
/// every version up to v7.6 did -- so an old settings file keeps behaving the
/// same way after the upgrade.
/// </summary>
public sealed class FpsRungSetting
{
    public double Fps { get; set; }

    /// <summary>Descend onto this rung once the video bitrate reaches this value.</summary>
    public double? DropAboveKbps { get; set; }

    /// <summary>Climb back off this rung once the bitrate falls to this value.</summary>
    public double? RiseBelowKbps { get; set; }
}

/// <summary>
/// A fully resolved ladder rung. Rung 0 is the base rate and is never entered
/// from below, so its drop threshold is infinite.
/// </summary>
public readonly record struct FpsRung(double Fps, double DropAboveKbps, double RiseBelowKbps);
