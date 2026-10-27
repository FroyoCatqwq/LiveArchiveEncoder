using System.Globalization;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Encoding;

/// <summary>How the audio track reaches the output file.</summary>
public enum AudioMode
{
    /// <summary>Separate pass, falling back to inline when the muxer cannot take it.</summary>
    Auto,

    /// <summary>Original v6.3.3 behaviour: one FFmpeg process, video pipe + audio input.</summary>
    Inline,

    /// <summary>Video-only encode, audio encoded in parallel, then muxed with -c copy.</summary>
    Separate,
}

/// <summary>
/// Audio side of the split pipeline: encode the track to its own file, then mux it
/// into the video-only encode with <c>-c copy</c>.
/// </summary>
public static class AudioTrack
{
    /// <summary>
    /// Container used for the intermediate audio-only file. The final muxer is not
    /// reusable here: several legacy muxers (roq, rm, amv) only accept a complete
    /// stream pair, so the track is staged in a container the codec natively owns.
    /// </summary>
    private static readonly Dictionary<string, string> ContainerByEncoder = new(StringComparer.Ordinal)
    {
        ["libmp3lame"] = "mp3",
        ["mp3_mf"] = "mp3",
        ["mp3"] = "mp3",
        ["libshine"] = "mp3",
        ["aac"] = "adts",
        ["libfdk_aac"] = "adts",
        ["ac3"] = "ac3",
        ["ac3_fixed"] = "ac3",
        ["mp2"] = "mp2",
        ["mp2fixed"] = "mp2",
        ["libtwolame"] = "mp2",
        ["wmav1"] = "asf",
        ["wmav2"] = "asf",
        ["libvorbis"] = "ogg",
        ["vorbis"] = "ogg",
        ["libopus"] = "ogg",
        ["opus"] = "ogg",
        ["libspeex"] = "ogg",
        ["flac"] = "flac",
        ["roq_dpcm"] = "roq",
        // v7.9.8.1: real_144 is a RealMedia codec with no other home; the rm
        // muxer writes it as a bare .ra (verified: `-c:a real_144 -f rm x.ra`
        // probes back as ra_144, 8 kHz mono).
        ["real_144"] = "rm",
        ["adpcm_ima_amv"] = "amv",
        ["adpcm_ima_wav"] = "wav",
        ["adpcm_ms"] = "wav",
        ["pcm_s16le"] = "wav",
        ["pcm_u8"] = "wav",
    };

    public static string ContainerFor(string audioEncoder) =>
        ContainerByEncoder.TryGetValue(audioEncoder, out string? container) ? container : "matroska";

    /// <summary>
    /// Audio-only encode. Seeking happens on the input so the produced track starts
    /// at timestamp 0, which is what keeps it aligned with the video-only encode.
    /// </summary>
    public static List<string> BuildEncodeCommand(
        string ffmpeg,
        string sourcePath,
        string audioEncoder,
        int audioKbps,
        int audioChannels,
        int? audioRate,
        double startSeconds,
        double duration,
        string outputPath,
        int? audioStream = null)
    {
        var cmd = new List<string> { ffmpeg, "-hide_banner", "-loglevel", "error" };
        // v7.9: a URL source needs the reconnect family here just like the
        // video pass does -- this is its own process reading its own connection.
        cmd.AddRange(Remote.HttpSource.ReadArgs(sourcePath));
        cmd.AddRange(Remote.Smb.RemoteReadArgs(sourcePath));
        if (startSeconds > 0) cmd.AddRange(["-ss", ArgsBuilder.FloatArg(startSeconds)]);
        cmd.AddRange(["-i", sourcePath, "-map", StreamSelection.AudioMap(0, audioStream), "-vn"]);
        cmd.AddRange(["-t", ArgsBuilder.FloatArg(duration)]);
        cmd.AddRange(["-c:a", audioEncoder]);
        if (!EncoderMath.AudioIgnoresBitrate(audioEncoder))
            cmd.AddRange(["-b:a", $"{audioKbps}k"]);
        cmd.AddRange([
            "-ac", Math.Max(1, audioChannels).ToString(CultureInfo.InvariantCulture),
        ]);
        if (audioRate is not null && audioRate.Value != 0)
            cmd.AddRange(["-ar", audioRate.Value.ToString(CultureInfo.InvariantCulture)]);
        cmd.AddRange(["-f", ContainerFor(audioEncoder), "-y", outputPath]);
        return cmd;
    }

    /// <summary>
    /// Mux the finished video-only file and the finished audio track. Both streams
    /// are copied: re-encoding either one would defeat the point of splitting them.
    /// </summary>
    public static List<string> BuildMuxCommand(
        string ffmpeg,
        string videoOnlyPath,
        string audioPath,
        double duration,
        string muxer,
        string outputPath)
    {
        return
        [
            ffmpeg, "-hide_banner", "-loglevel", "error",
            "-i", videoOnlyPath,
            "-i", audioPath,
            "-map", "0:v:0",
            "-map", "1:a:0",
            "-c:v", "copy",
            "-c:a", "copy",
            "-t", ArgsBuilder.FloatArg(duration),
            "-f", muxer,
            "-y", outputPath,
        ];
    }

    /// <summary>
    /// Audio-only encode. Seeking happens on the input so the produced track starts
    /// at timestamp 0, which is what keeps it aligned with the video-only encode.
    ///
    /// Runs at <see cref="System.Diagnostics.ProcessPriorityClass.BelowNormal"/>:
    /// the track is produced in parallel with the video pass, and a full-speed audio
    /// encode would otherwise compete with the capture thread for CPU and perturb the
    /// adaptive controller's frame selection. The audio pass is not latency-critical --
    /// it only has to finish before the mux.
    /// </summary>
    public static CaptureResult Encode(
        string ffmpeg, string sourcePath, string audioEncoder, int audioKbps,
        int audioChannels, int? audioRate, double startSeconds, double duration,
        string outputPath, int timeoutSeconds, int? audioStream = null,
        CancellationToken cancellationToken = default)
    {
        return ProcessRunner.Run(
            BuildEncodeCommand(ffmpeg, sourcePath, audioEncoder, audioKbps,
                audioChannels, audioRate, startSeconds, duration, outputPath, audioStream),
            timeoutSeconds,
            System.Diagnostics.ProcessPriorityClass.BelowNormal,
            cancellationToken);
    }

    public static CaptureResult Mux(
        string ffmpeg, string videoOnlyPath, string audioPath,
        double duration, string muxer, string outputPath, int timeoutSeconds)
    {
        return ProcessRunner.Run(
            BuildMuxCommand(ffmpeg, videoOnlyPath, audioPath, duration, muxer, outputPath),
            timeoutSeconds);
    }
}
