using System.Globalization;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Encoding;

/// <summary>
/// Audio half of a preflight trial. Only needed for containers that refuse to be
/// written without an audio stream (AMV), where a video-only trial would fail
/// for a reason unrelated to what the trial is actually checking.
/// </summary>
public sealed record AudioTrial(string Encoder, int Channels, int? Rate, int BlockSize);

/// <summary>
/// Short real-chain trials executed before the realtime encode, so an invalid
/// filter/codec/muxer combination fails in seconds instead of after a full pass.
/// </summary>
public static class Preflight
{
    public static void Video(
        string ffmpeg,
        FileInfo src,
        EncoderInfo encoder,
        double fps,
        int quality,
        int threads,
        int width,
        int height,
        string vf,
        string muxer,
        StreamInfo stream,
        string? x264Preset,
        string? x264Tune,
        string? x264Profile,
        string? x264Level,
        bool hwaccelCuda,
        double startSeconds,
        AudioTrial? audio = null)
    {
        string tempDir = Path.Combine(Path.GetTempPath(),
            "live_archive_preflight_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string output = Path.Combine(tempDir, $"preflight.{encoder.Extension}");
            var cmd = new List<string> { ffmpeg, "-hide_banner", "-loglevel", "error" };
            if (hwaccelCuda) cmd.AddRange(["-hwaccel", "cuda"]);
            if (startSeconds > 0) cmd.AddRange(["-ss", ArgsBuilder.FloatArg(startSeconds)]);
            cmd.AddRange([
                "-threads", Math.Max(1, threads).ToString(),
                "-i", src.FullName,
                "-map", "0:v:0",
                "-vf", vf,
                "-frames:v", "3",
                "-c:v", encoder.Name,
            ]);
            if (audio is null)
            {
                cmd.Add("-an");
            }
            else
            {
                // Containers such as AMV only hold two streams, so the trial must
                // mirror the real command and carry an audio track too.
                cmd.AddRange(["-map", "0:a:0", "-c:a", audio.Encoder]);

                // ADPCM/DPCM/PCM have no rate control; -b:a is silently ignored.
                if (!EncoderMath.AudioIgnoresBitrate(audio.Encoder))
                    cmd.AddRange(["-b:a", "32k"]);

                if (audio.BlockSize > 0)
                    cmd.AddRange(["-block_size:a", audio.BlockSize.ToString(CultureInfo.InvariantCulture)]);

                cmd.AddRange(["-ac", Math.Max(1, audio.Channels).ToString()]);
                if (audio.Rate is not null && audio.Rate.Value != 0)
                    cmd.AddRange(["-ar", audio.Rate.Value.ToString()]);
            }
            cmd.AddRange(ArgsBuilder.VideoQualityArgs(encoder.Name, quality));
            cmd.AddRange(["-r", ArgsBuilder.FpsArg(fps)]);
            if (encoder.Name == "libx264")
            {
                cmd.AddRange(ArgsBuilder.X264Args(x264Preset, x264Tune, x264Profile, x264Level));
            }
            cmd.AddRange(ArgsBuilder.ColorArgs(stream));
            cmd.AddRange(["-f", muxer, "-y", output]);

            CaptureResult result = ProcessRunner.Run(cmd, 120);
            if (result.ExitCode != 0)
            {
                string detail = result.Stderr.Trim() is { Length: > 0 } d
                    ? d : "preflight failed without FFmpeg diagnostics";
                throw new InvalidOperationException($"Video preflight failed for {encoder.Name}: {detail}");
            }
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
            {
                throw new InvalidOperationException("Video preflight produced no usable output");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    public static void Audio(
        string ffmpeg,
        FileInfo src,
        string audioEncoder,
        int audioKbps,
        int audioChannels,
        int? audioRate,
        double startSeconds)
    {
        var cmd = new List<string> { ffmpeg, "-hide_banner", "-loglevel", "error" };
        if (startSeconds > 0) cmd.AddRange(["-ss", ArgsBuilder.FloatArg(startSeconds)]);
        cmd.AddRange(["-i", src.FullName, "-map", "0:a:0", "-vn", "-t", "2"]);
        cmd.AddRange(["-c:a", audioEncoder]);
        if (!EncoderMath.AudioIgnoresBitrate(audioEncoder))
            cmd.AddRange(["-b:a", $"{audioKbps}k"]);
        cmd.AddRange([
            "-ac", Math.Max(1, audioChannels).ToString(),
        ]);
        if (audioRate is not null && audioRate.Value != 0)
            cmd.AddRange(["-ar", audioRate.Value.ToString()]);
        cmd.AddRange(["-f", "null", "-"]);

        CaptureResult result = ProcessRunner.Run(cmd, 120);
        if (result.ExitCode == 0) return;

        string detail = result.Stderr.Trim() is { Length: > 0 } d
            ? d : "audio preflight failed without FFmpeg diagnostics";
        if (audioEncoder == "roq_dpcm")
        {
            throw new InvalidOperationException(
                "Audio preflight failed for roq_dpcm. This encoder requires 22050 Hz "
                + $"and mono/stereo audio. FFmpeg said: {detail}");
        }
        throw new InvalidOperationException($"Audio preflight failed for {audioEncoder}: {detail}");
    }
}
