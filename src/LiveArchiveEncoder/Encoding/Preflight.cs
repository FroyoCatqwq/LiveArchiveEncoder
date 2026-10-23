using System.Globalization;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Encoding;

/// <summary>
/// Audio half of a preflight trial.
///
/// v7.9.5: the trial always carries the real audio settings, not just for the
/// containers that cannot be written without one. It used to be a second
/// process launched in parallel with the video trial, which meant one more
/// launch per run for information the same command line can produce: the audio
/// encoder opens exactly the same way whether or not a video stream is beside
/// it.
/// </summary>
public sealed record AudioTrial(string Encoder, int Channels, int? Rate, int BlockSize, int Kbps);

/// <summary>
/// Short real-chain trials executed before the realtime encode, so an invalid
/// filter/codec/muxer combination fails in seconds instead of after a full pass.
///
/// v7.9.5: there is one trial, and it carries both streams. The audio encoder
/// used to get a second FFmpeg process of its own, launched in parallel; the
/// only thing that process could report was whether <c>-c:a</c> opens, which
/// this trial answers too -- and answers on the command line the run will
/// actually execute, rather than on an audio-only approximation of it.
/// </summary>
public static class Preflight
{
    public static void Video(
        string ffmpeg,
        string sourcePath,
        EncoderInfo encoder,
        double fps,
        int quality,
        int threads,
        string vf,
        string muxer,
        StreamInfo stream,
        string? x264Preset,
        string? x264Tune,
        string? x264Profile,
        string? x264Level,
        string hwaccel,
        double startSeconds,
        AudioTrial? audio = null,
        int? videoStream = null,
        int? audioStream = null,
        string? pixFmt = null,
        Configuration.ColorOverride? colors = null,
        string dither = "auto",
        string? transportFmt = null)
    {
        string tempDir = Path.Combine(Path.GetTempPath(),
            "live_archive_preflight_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string output = Path.Combine(tempDir, $"preflight.{encoder.Extension}");
            var cmd = new List<string> { ffmpeg, "-hide_banner", "-loglevel", "error" };
            // v7.9.6: the dither mode is a global swscale option, so it has to be
            // on the command line before any input -- and the trial has to carry
            // it, or the trial would validate a slightly different conversion
            // than the run performs.
            cmd.AddRange(ArgsBuilder.DitherArgs(dither, transportFmt ?? stream.PixFmt));
            // v7.9: a URL source needs the reconnect family on this throwaway
            // read too, or a flaky connection fails the preflight and with it
            // an encode that would have survived.
            // v7.9.3.1: the share half was missing here -- every other read path
            // adds both, and without -rw_timeout a stalled CIFS mount hangs the
            // trial instead of failing it fast, which is this file's whole job.
            cmd.AddRange(Remote.HttpSource.ReadArgs(sourcePath));
            cmd.AddRange(Remote.Smb.RemoteReadArgs(sourcePath));
            if (!string.Equals(hwaccel, FFmpeg.Hwaccel.Off, StringComparison.OrdinalIgnoreCase))
                cmd.AddRange(["-hwaccel", hwaccel]);
            if (startSeconds > 0) cmd.AddRange(["-ss", ArgsBuilder.FloatArg(startSeconds)]);
            cmd.AddRange([
                "-threads", Math.Max(1, threads).ToString(),
                "-i", sourcePath,
                "-map", StreamSelection.VideoMap(videoStream),
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
                // The trial mirrors the real command, audio included: the point
                // is to open the same encoder/rate/channel combination the
                // encode will, and to fail before a realtime pass starts.
                cmd.AddRange(["-map", StreamSelection.AudioMap(0, audioStream), "-c:a", audio.Encoder]);

                // ADPCM/DPCM/PCM have no rate control; -b:a is silently ignored.
                if (!EncoderMath.AudioIgnoresBitrate(audio.Encoder) && audio.Kbps > 0)
                    cmd.AddRange(["-b:a", $"{audio.Kbps}k"]);

                if (audio.BlockSize > 0)
                    cmd.AddRange(["-block_size:a", audio.BlockSize.ToString(CultureInfo.InvariantCulture)]);

                cmd.AddRange(["-ac", Math.Max(1, audio.Channels).ToString()]);
                if (audio.Rate is not null && audio.Rate.Value != 0)
                    cmd.AddRange(["-ar", audio.Rate.Value.ToString()]);
            }
            cmd.AddRange(ArgsBuilder.VideoQualityArgs(encoder.Name, quality));
            // v7.9.6: an explicit request is also pinned on the encoder side, so
            // the output really keeps it instead of being quietly retuned to the
            // encoder's own default.
            if (pixFmt.HasText) cmd.AddRange(["-pix_fmt", pixFmt]);
            cmd.AddRange(["-r", ArgsBuilder.FpsArg(fps)]);
            if (encoder.Name == "libx264")
            {
                cmd.AddRange(ArgsBuilder.X264Args(x264Preset, x264Tune, x264Profile, x264Level));
                cmd.AddRange(ArgsBuilder.X264ColorArgs(colors));
            }
            cmd.AddRange(ArgsBuilder.ColorArgs(stream, colors));
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
}
