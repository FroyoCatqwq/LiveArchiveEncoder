namespace Lae.Core;

/// <summary>One subtitle track carried into the output, and the codec it is written as.</summary>
/// <param name="Index">Absolute stream index in the source, as ffprobe numbers it.</param>
/// <param name="Codec">The value <c>-c:s</c> takes for it.</param>
public readonly record struct SubtitleCarry(int Index, string Codec);

/// <summary>
/// Carrying subtitles into the output as a stream of their own, instead of drawing
/// them into the picture.
///
/// <para>The two are not alternatives for the same file. Burning needs a text
/// track: libass lays characters out, and a PGS or VobSub track is pictures of
/// text, so there is nothing to lay out. Carrying needs a container that can hold
/// the track: Matroska holds anything, MP4 holds <c>mov_text</c> only, WebM holds
/// <c>webvtt</c> only, and AVI, FLV, MPEG-PS, ASF and RealMedia have no subtitle
/// muxer at all.</para>
///
/// <para>Which is why this is a table and not a flag. A user who asks to carry a
/// PGS track into an MP4 is asking for something no muxer can do, and the answer
/// has to name the container that can -- not fail inside FFmpeg after the encode
/// has already run.</para>
/// </summary>
public static class SubtitleCopy
{
    /// <summary>Containers that can hold a subtitle stream, and what they write.</summary>
    private static readonly Dictionary<string, string> ConvertsTo = new(StringComparer.OrdinalIgnoreCase)
    {
        // Matroska is the archive's answer to "keep it as it is": it stores the
        // packet stream untouched, which is why a remux is not a re-encode.
        ["mkv"] = "copy",
        // MP4 has exactly one subtitle codec and it is text. The space between
        // "text" and "mov_text" is where the size and timing of every cue live,
        // so this is a real conversion, not a relabelling.
        ["mp4"] = "mov_text",
        ["m4v"] = "mov_text",
        ["mov"] = "mov_text",
        ["webm"] = "webvtt",
    };

    /// <summary>
    /// What to write <paramref name="sourceCodec"/> as in <paramref name="container"/>,
    /// or why that cannot be done.
    /// </summary>
    public static (string? Codec, string? Problem) Resolve(
        string container, string? sourceCodec, int index, Localization.IMessages messages)
    {
        string ext = container.Trim().TrimStart('.').ToLowerInvariant();

        if (!ConvertsTo.TryGetValue(ext, out string? target))
        {
            return (null, messages.Format("plan.error.containerNoSubtitles", new { container = ext }));
        }

        if (target == "copy") return ("copy", null);

        if (SubtitleTrack.IsBitmap(sourceCodec))
        {
            return (null, messages.Format("plan.error.bitmapNeedsMatroska", new
            {
                index,
                codec = sourceCodec,
                container = ext,
            }));
        }

        return (target, null);
    }

    /// <summary>
    /// Resolves a whole set of tracks.
    ///
    /// <para>No per-track codec bookkeeping is needed: the table maps a container
    /// to exactly one subtitle codec, so every track that survives
    /// <see cref="Resolve(string, string?, int)"/> shares it and one <c>-c:s</c>
    /// covers the output. If the table ever grows a container that needs two, this
    /// is the method that stops being right -- hence the note rather than a silent
    /// assumption.</para>
    /// </summary>
    public static (IReadOnlyList<SubtitleCarry> Tracks, IReadOnlyList<string> Problems) Resolve(
        string container, SourceInfo source, IReadOnlyList<int> indices, Localization.IMessages messages)
    {
        var carries = new List<SubtitleCarry>(indices.Count);
        var problems = new List<string>();

        foreach (int index in indices.Distinct())
        {
            if (source.CheckPick(index, "subtitle", "--subtitle-copy", messages) is { } pickProblem)
            {
                problems.Add(pickProblem);
                continue;
            }

            string? sourceCodec = source.Streams.First(s => s.Index == index).Codec;
            (string? codec, string? problem) = Resolve(container, sourceCodec, index, messages);

            if (codec is null) problems.Add(problem!);
            else carries.Add(new SubtitleCarry(index, codec));
        }

        return (carries, problems);
    }
}
