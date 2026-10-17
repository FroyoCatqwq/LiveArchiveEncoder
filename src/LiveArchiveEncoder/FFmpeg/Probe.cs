using System.Globalization;
using System.Text.Json;
using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>Everything the encoder needs to know about a source file.</summary>
public sealed class SourceInfo
{
    public required StreamInfo Video { get; init; }
    public double? Duration { get; init; }
    public bool HasAudio { get; init; }
}

/// <summary>ffprobe wrappers for stream metadata, duration and audio presence.</summary>
public static class Probe
{
    /// <summary>
    /// Stream metadata, duration and audio presence in a <b>single</b> ffprobe run.
    ///
    /// The three separate probes cost ~0.6s of pure process startup on Windows,
    /// which dominated the whole encode once the realtime pacing was removed.
    /// This is behaviour-preserving: <c>-show_streams -show_format</c> already
    /// carries every field the individual calls asked for, including the video
    /// stream duration that takes precedence over the container one.
    /// </summary>
    public static SourceInfo Source(string ffprobe, FileInfo src)
    {
        // -rw_timeout for UNC sources only: ffprobe is the first process to touch
        // the file, so a stalled share would hang there before FFmpeg ever runs.
        CaptureResult p = ProcessRunner.Run([
            ffprobe, "-v", "error",
            .. Remote.Smb.RemoteProbeArgs(src.FullName),
            "-show_streams", "-show_format", "-of", "json", src.FullName,
        ]);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                p.Stderr.Trim() is { Length: > 0 } err ? err : "cannot probe source");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(p.Stdout);
            JsonElement root = doc.RootElement;

            JsonElement? video = null;
            bool hasAudio = false;
            if (root.TryGetProperty("streams", out JsonElement streams))
            {
                foreach (JsonElement s in streams.EnumerateArray())
                {
                    string? type = s.TryGetProperty("codec_type", out JsonElement t) ? t.GetString() : null;
                    if (type == "video") video ??= s;
                    else if (type == "audio") hasAudio = true;
                }
            }
            if (video is null)
                throw new InvalidOperationException("ffprobe returned incomplete video stream information");

            JsonElement v = video.Value;

            static string? Get(JsonElement e, string name)
                => e.TryGetProperty(name, out JsonElement x) ? x.GetString() : null;

            var info = new StreamInfo
            {
                Width = int.Parse(v.GetProperty("width").ToString(), CultureInfo.InvariantCulture),
                Height = int.Parse(v.GetProperty("height").ToString(), CultureInfo.InvariantCulture),
                PixFmt = Get(v, "pix_fmt") ?? "yuv420p",
                ColorRange = Get(v, "color_range"),
                ColorSpace = Get(v, "color_space"),
                ColorPrimaries = Get(v, "color_primaries"),
                ColorTransfer = Get(v, "color_transfer"),
            };

            // Same precedence as the old split probes: the video stream duration
            // wins, the container duration is the fallback.
            double? duration = null;
            if (v.TryGetProperty("duration", out JsonElement streamDuration)
                && double.TryParse(streamDuration.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double sv)
                && double.IsFinite(sv) && sv > 0)
            {
                duration = sv;
            }
            else if (root.TryGetProperty("format", out JsonElement format)
                     && format.TryGetProperty("duration", out JsonElement formatDuration)
                     && double.TryParse(formatDuration.ToString(), NumberStyles.Float,
                         CultureInfo.InvariantCulture, out double fv)
                     && double.IsFinite(fv) && fv > 0)
            {
                duration = fv;
            }

            return new SourceInfo { Video = info, Duration = duration, HasAudio = hasAudio };
        }
        catch (Exception exc) when (exc is KeyNotFoundException or FormatException
                                        or JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("ffprobe returned incomplete source information", exc);
        }
    }

    /// <summary>Single-field probe; prefer <see cref="Source"/> when more is needed.</summary>
    public static StreamInfo Stream(string ffprobe, FileInfo src)
    {
        CaptureResult p = ProcessRunner.Run([
            ffprobe, "-v", "error", "-select_streams", "v:0",
            "-show_entries",
            "stream=width,height,pix_fmt,color_range,color_space,color_primaries,color_transfer",
            "-of", "json", src.FullName,
        ]);
        if (p.ExitCode != 0)
            throw new InvalidOperationException(p.Stderr.Trim() is { Length: > 0 } err ? err : "cannot probe video stream");

        try
        {
            using JsonDocument doc = JsonDocument.Parse(p.Stdout);
            JsonElement stream = doc.RootElement.GetProperty("streams")[0];

            static string? Get(JsonElement e, string name)
                => e.TryGetProperty(name, out JsonElement v) ? v.GetString() : null;

            return new StreamInfo
            {
                Width = int.Parse(stream.GetProperty("width").ToString(), CultureInfo.InvariantCulture),
                Height = int.Parse(stream.GetProperty("height").ToString(), CultureInfo.InvariantCulture),
                PixFmt = Get(stream, "pix_fmt") ?? "yuv420p",
                ColorRange = Get(stream, "color_range"),
                ColorSpace = Get(stream, "color_space"),
                ColorPrimaries = Get(stream, "color_primaries"),
                ColorTransfer = Get(stream, "color_transfer"),
            };
        }
        catch (Exception exc) when (exc is KeyNotFoundException or IndexOutOfRangeException
                                        or FormatException or JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("ffprobe returned incomplete video stream information", exc);
        }
    }

    public static double? Duration(string ffprobe, FileInfo src)
    {
        CaptureResult streamProbe = ProcessRunner.Run([
            ffprobe, "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=duration", "-of", "default=nw=1:nk=1", src.FullName,
        ]);

        if (streamProbe.ExitCode == 0
            && double.TryParse(streamProbe.Stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value) && value > 0)
        {
            return value;
        }

        CaptureResult formatProbe = ProcessRunner.Run([
            ffprobe, "-v", "error", "-show_entries", "format=duration",
            "-of", "default=nw=1:nk=1", src.FullName,
        ]);
        if (formatProbe.ExitCode != 0) return null;

        return double.TryParse(formatProbe.Stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double formatValue)
            && double.IsFinite(formatValue) && formatValue > 0
            ? formatValue
            : null;
    }

    public static bool HasAudio(string ffprobe, FileInfo src)
    {
        CaptureResult p = ProcessRunner.Run([
            ffprobe, "-v", "error", "-select_streams", "a:0",
            "-show_entries", "stream=index", "-of", "csv=p=0", src.FullName,
        ]);
        return p.ExitCode == 0 && p.Stdout.Trim().Length > 0;
    }
}
