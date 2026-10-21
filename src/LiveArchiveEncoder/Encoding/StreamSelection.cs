using System.Text;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Encoding;

/// <summary>
/// Picking one video and one audio stream out of a multi-stream source (v7.9).
///
/// The indices are the <b>absolute</b> stream numbers -- exactly what
/// <c>ffprobe -show_streams</c> prints as <c>index=</c>, so what the user sees
/// is what they type. Null means "the first stream of that type", which is
/// what every version up to v7.8 did.
///
/// Two halves live here: the policy (is this index a video/audio stream of
/// <em>this</em> source?) and the FFmpeg map arguments the pick turns into.
/// Keeping them together is deliberate -- a map that outlives its validation
/// is how a wrong stream ends up encoded silently.
/// </summary>
public static class StreamSelection
{
    /// <summary>
    /// Validate both picks against the probed inventory and return the source
    /// with <see cref="SourceInfo.Video"/> describing the <em>selected</em>
    /// video stream (metadata such as the pixel format drives the transport
    /// format choice, so the first stream's values must not be reused).
    /// </summary>
    /// <exception cref="ArgumentException">A pick names no stream of the right type.</exception>
    public static SourceInfo Resolve(SourceInfo source, int? video, int? audio)
    {
        string? error = Check(source, video, audio);
        if (error is not null) throw new ArgumentException(error);

        if (video is null) return source;

        SourceStream? pick = source.Streams.FirstOrDefault(s => s.Index == video && s.Type == "video");
        // Check() already rejected a pick that is not a video stream, so the
        // only way to be here without metadata is a stream ffprobe described
        // without width/height; keeping the first video stream then is the
        // only honest option left.
        if (pick?.Video is null) return source;

        return new SourceInfo
        {
            Video = pick.Video,
            Duration = source.Duration,
            HasAudio = source.HasAudio,
            Streams = source.Streams,
        };
    }

    /// <summary>
    /// The rule, as an error message or null. A pick that exists but is the
    /// wrong type (an audio index for <c>--video-stream</c>) is refused too:
    /// ffmpeg's <c>-map 0:N</c> would happily take it and encode the wrong
    /// thing, which is worse than an error.
    /// </summary>
    public static string? Check(SourceInfo source, int? video, int? audio)
    {
        if (video is int v && !IsType(source, v, "video"))
            return $"--video-stream {v} does not name a video stream in this source.\n{Describe(source)}";
        if (audio is int a && !IsType(source, a, "audio"))
            return $"--audio-stream {a} does not name an audio stream in this source.\n{Describe(source)}";
        return null;
    }

    /// <summary>
    /// Human-readable inventory, used by error messages and by
    /// <c>--list-streams</c>.
    /// </summary>
    public static string Describe(SourceInfo source)
    {
        var sb = new StringBuilder("streams in this source:");
        foreach (SourceStream s in source.Streams)
        {
            string size = s.Video is { Width: > 0, Height: > 0 } v ? $" {v.Width}x{v.Height}" : "";
            string lang = s.Language is { Length: > 0 } l ? $" [{l}]" : "";
            string title = s.Title is { Length: > 0 } t ? $" \"{t}\"" : "";
            sb.Append($"\n  {s.Index,3}  {s.Type,-9} {s.Codec ?? "?"}{size}{lang}{title}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// <c>-map</c> argument for the video track of the source. An absolute
    /// index when one was picked, ffmpeg's "first video stream" shorthand
    /// otherwise -- unchanged behaviour for every existing caller.
    /// </summary>
    public static string VideoMap(int? index) => index is int v ? $"0:{v}" : "0:v:0";

    /// <summary>
    /// Same for an audio track. The source is input 0 in the standalone audio
    /// pass and input 1 in the inline mux (input 0 being the raw video pipe),
    /// hence the parameter.
    /// </summary>
    public static string AudioMap(int input, int? index) => index is int a ? $"{input}:{a}" : $"{input}:a:0";

    private static bool IsType(SourceInfo source, int index, string type) =>
        source.Streams.Any(s => s.Index == index && s.Type == type);
}
