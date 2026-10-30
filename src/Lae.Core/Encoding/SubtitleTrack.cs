namespace Lae.Core;

/// <summary>
/// An embedded subtitle track, and what has to happen before it can be burned.
///
/// <para>libass draws from a <em>file</em>. It cannot read a stream out of the
/// container it is being fed, so a track has to be unwrapped into one first --
/// that is a real FFmpeg call, not an argument, which is why the plan cannot
/// express it and the host has to do it. What belongs here is the part that is a
/// decision rather than an action: whether the track's codec can be drawn at all,
/// and what the unwrapping command looks like.</para>
///
/// <para>The distinction that matters is text versus bitmap. A <c>subrip</c> or
/// <c>ass</c> track is characters and can be re-wrapped as ASS losslessly. A
/// <c>hdmv_pgs_subtitle</c> or <c>dvd_subtitle</c> track is pictures of text:
/// there is nothing for libass to lay out, and the only way onto the picture is
/// the overlay filter with the images decoded first -- a different feature with a
/// different failure surface. Refusing it with a sentence beats handing FFmpeg
/// something it will complain about three seconds in.</para>
/// </summary>
public static class SubtitleTrack
{
    /// <summary>Coded as characters, so they can be re-wrapped as ASS and drawn.</summary>
    private static readonly string[] Text =
        ["subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text", "microdvd", "subviewer"];

    /// <summary>Coded as pictures, so libass has nothing to draw.</summary>
    private static readonly string[] Bitmap =
        ["hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub", "dvb_teletext"];

    public static bool IsText(string? codec)
        => codec is not null && Text.Contains(codec.Trim().ToLowerInvariant());

    public static bool IsBitmap(string? codec)
        => codec is not null && Bitmap.Contains(codec.Trim().ToLowerInvariant());

    /// <summary>
    /// Why this track cannot be burned, or null when it can.
    ///
    /// <para>An unknown codec is allowed through: a build newer than this list may
    /// know a text format this list does not, and FFmpeg's own verdict -- a
    /// refusal naming the conversion -- is more accurate than a guess made here.
    /// Bitmap formats are refused by name because no argument can recover them.</para>
    /// </summary>
    public static string? Problem(string? codec, int index)
    {
        if (!IsBitmap(codec)) return null;

        return $"subtitle track {index} is a bitmap format ({codec}): there is no text to lay out, "
             + "so it cannot be burned; pick a text track instead";
    }

    /// <summary>
    /// Unwraps one track into a file libass can read.
    ///
    /// <para>Deliberately no <c>-ss</c>: the burn shifts the timeline by the trim
    /// start inside the decoder's filter chain, and that only works while the
    /// subtitle file still carries the source's own timestamps. Seeking here would
    /// rebase them to zero and every caption would arrive late by exactly the
    /// offset the shift was added to correct.</para>
    /// </summary>
    public static List<string> ExtractCommand(string ffmpeg, string source, int index, string destination)
        =>
        [
            ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", source,
            "-map", $"0:{index}",
            "-c:s", "ass",
            destination,
        ];

    /// <summary>
    /// Where an unwrapped track goes: beside the output rather than beside the
    /// source, because a network share or a read-only mount is a normal place for
    /// a recording to live. The name carries the track index and a random tail so
    /// two tracks, or two runs at once, do not collide.
    /// </summary>
    public static string TemporaryPath(string outputPath, int index, string tag)
    {
        string folder = Path.GetDirectoryName(outputPath) ?? "";
        return Path.Combine(folder, $"lae-subs-{index}-{tag}.ass");
    }

    /// <summary>What the run says, so an extracted file appearing in the folder is not a mystery.</summary>
    public static string Note(string sourcePath, int index, string? codec)
        => $"burning subtitle track {index} ({codec ?? "unknown codec"}) from {Path.GetFileName(sourcePath)}; "
         + "unwrapped to a file first because the filter cannot read a stream";
}
