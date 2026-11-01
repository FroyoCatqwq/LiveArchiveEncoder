namespace Lae.Core;

/// <summary>
/// What probing the source found out.
///
/// <para>Pure data: the probe itself (spawning ffprobe, parsing JSON) lives in
/// <c>Lae.Media</c>. Keeping the result here is what lets the whole planning step
/// -- pixel format choice, dimension rounding, filter chain -- be tested without
/// a media file or a process.</para>
/// </summary>
public sealed record SourceInfo
{
    /// <summary>Length in seconds. Zero means "unknown", which planning tolerates.</summary>
    public double DurationSeconds { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>The source's own pixel format; the transport format starts from it.</summary>
    public string PixelFormat { get; init; } = "yuv420p";

    /// <summary>Colour tags as the container declares them; null means it said nothing.</summary>
    public string? ColorRange { get; init; }

    public string? ColorSpace { get; init; }

    public string? ColorPrimaries { get; init; }

    public string? ColorTransfer { get; init; }

    /// <summary>True when the source carries at least one audio stream.</summary>
    public bool HasAudio { get; init; }

    /// <summary>Audio rate reported by the probe; 0 when there is no audio or it is unknown.</summary>
    public int AudioRate { get; init; }

    public int AudioChannels { get; init; }

    /// <summary>Every stream, in the order ffprobe reported it (index == position).</summary>
    public IReadOnlyList<SourceStream> Streams { get; init; } = [];

    /// <summary>The video stream this run will read, once a pick is applied.</summary>
    public SourceStream? Video(int? absoluteIndex) => Pick(absoluteIndex, "video");

    /// <summary>The audio stream this run will keep, once a pick is applied.</summary>
    public SourceStream? Audio(int? absoluteIndex) => Pick(absoluteIndex, "audio");

    private SourceStream? Pick(int? absoluteIndex, string type)
    {
        if (absoluteIndex is { } index)
        {
            return Streams.FirstOrDefault(s => s.Index == index && s.Type == type);
        }

        return Streams.FirstOrDefault(s => s.Type == type);
    }

    /// <summary>
    /// The stream pick, resolved and checked. Returns a reason when the requested
    /// index is out of range or names the wrong kind of stream -- a stale index
    /// from a settings file must not silently select the subtitle track.
    ///
    /// <para><paramref name="messages"/> because this is one of the sentences a user
    /// reads most: a wrong index is the commonest way to name a stream wrongly, and
    /// "that stream is audio, not subtitle" is the whole answer.</para>
    /// </summary>
    public string? CheckPick(int? absoluteIndex, string type, string flag, Localization.IMessages messages)
    {
        if (absoluteIndex is not { } index) return null;

        SourceStream? stream = Streams.FirstOrDefault(s => s.Index == index);
        if (stream is null)
            return messages.Format("plan.error.streamMissing", new { flag, index });

        if (stream.Type != type)
            return messages.Format("plan.error.streamWrongType", new { flag, index, actual = stream.Type, type });

        return null;
    }
}
