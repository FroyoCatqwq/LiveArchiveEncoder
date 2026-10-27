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

    /// <summary>
    /// v7.9.6.3: container (or video stream) bitrate in kbps, when ffprobe
    /// reports one. This is what a network read has to sustain.
    /// </summary>
    public double? BitRateKbps { get; init; }

    /// <summary>
    /// v7.9: every stream of the source, in index order. <see cref="Video"/>
    /// is the first video stream unless the caller picked another one; this
    /// list is what makes that pick validatable and listable.
    /// </summary>
    public IReadOnlyList<SourceStream> Streams { get; init; } = [];
}

/// <summary>ffprobe wrappers for stream metadata, duration and audio presence.</summary>
public static class Probe
{
    /// <summary>
    /// Timeout for probing a <em>source</em>.
    ///
    /// The 30 s <see cref="ProcessRunner"/> default is not enough for the
    /// sources this tool actually meets: a 179 MB VOB on a network share took
    /// <b>2m15s</b> to probe (measured 2026-10-21), because MPEG-PS carries no
    /// index and the duration only falls out of reading the whole programme
    /// stream. A network stall is bounded by <c>-rw_timeout</c> already, so this
    /// is the backstop behind it, not the first line of defence.
    /// </summary>
    public const int SourceTimeoutSeconds = 300;

    /// <summary>
    /// Stream metadata, duration and audio presence in a <b>single</b> ffprobe run.
    ///
    /// The three separate probes cost ~0.6s of pure process startup on Windows,
    /// which dominated the whole encode once the realtime pacing was removed.
    /// This is behaviour-preserving: <c>-show_streams -show_format</c> already
    /// carries every field the individual calls asked for, including the video
    /// stream duration that takes precedence over the container one.
    ///
    /// Takes the path as a string because the source may be an HTTP(S) URL
    /// (v7.9), which is not a <see cref="FileInfo"/>.
    /// </summary>
    public static SourceInfo Source(string ffprobe, string path)
    {
        // -rw_timeout for UNC sources only: ffprobe is the first process to touch
        // the file, so a stalled share would hang there before FFmpeg ever runs.
        // A URL gets the same courtesy for the same reason.
        CaptureResult p = ProcessRunner.Run([
            ffprobe, "-v", "error",
            .. Remote.Smb.RemoteProbeArgs(path),
            .. Remote.HttpSource.ProbeArgs(path),
            "-show_streams", "-show_format", "-of", "json", path,
        ], SourceTimeoutSeconds);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                p.Stderr.Trim() is { Length: > 0 } err ? err : "cannot probe source");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(p.Stdout);
            JsonElement root = doc.RootElement;

            static string? Get(JsonElement e, string name)
                => e.TryGetProperty(name, out JsonElement x) ? x.GetString() : null;

            static StreamInfo ReadVideo(JsonElement e) => new()
            {
                Width = int.Parse(e.GetProperty("width").ToString(), CultureInfo.InvariantCulture),
                Height = int.Parse(e.GetProperty("height").ToString(), CultureInfo.InvariantCulture),
                PixFmt = Get(e, "pix_fmt") ?? "yuv420p",
                ColorRange = Get(e, "color_range"),
                ColorSpace = Get(e, "color_space"),
                ColorPrimaries = Get(e, "color_primaries"),
                ColorTransfer = Get(e, "color_transfer"),
            };

            JsonElement? video = null;
            bool hasAudio = false;
            var streams = new List<SourceStream>();
            if (root.TryGetProperty("streams", out JsonElement streamsElement))
            {
                foreach (JsonElement s in streamsElement.EnumerateArray())
                {
                    string? type = s.TryGetProperty("codec_type", out JsonElement t) ? t.GetString() : null;
                    if (type == "video") video ??= s;
                    else if (type == "audio") hasAudio = true;

                    // v7.9: the full inventory, for --list-streams and for
                    // validating a --video-stream/--audio-stream pick. A video
                    // stream carries its own metadata so a non-default pick
                    // still gets the right transport pixel format.
                    if (s.TryGetProperty("index", out JsonElement idx)
                        && int.TryParse(idx.ToString(), CultureInfo.InvariantCulture, out int index))
                    {
                        StreamInfo? vinfo = type == "video" && s.TryGetProperty("width", out JsonElement _)
                            ? ReadVideo(s)
                            : null;
                        streams.Add(new SourceStream(
                            index,
                            type ?? "?",
                            s.TryGetProperty("codec_name", out JsonElement cn) ? cn.GetString() : null,
                            s.TryGetProperty("tags", out JsonElement tags)
                                && tags.TryGetProperty("language", out JsonElement lang) ? lang.GetString() : null,
                            s.TryGetProperty("tags", out JsonElement titleTags)
                                && titleTags.TryGetProperty("title", out JsonElement title) ? title.GetString() : null,
                            vinfo));
                    }
                }
            }
            if (video is null)
                throw new InvalidOperationException("ffprobe returned incomplete video stream information");

            JsonElement v = video.Value;

            var info = ReadVideo(v);

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

            return new SourceInfo
            {
                Video = info,
                Duration = duration,
                HasAudio = hasAudio,
                BitRateKbps = ReadBitRateKbps(root, v),
                Streams = streams,
            };
        }
        catch (Exception exc) when (exc is KeyNotFoundException or FormatException
                                        or JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("ffprobe returned incomplete source information", exc);
        }
    }

    /// <summary>
    /// Container bitrate, then the video stream's own -- the number a network
    /// source has to be able to deliver (v7.9.6.3).
    ///
    /// <para>Used to judge whether a share may be read by several shards at
    /// once. ffprobe reports <c>bit_rate</c> as a string of bits per second and
    /// omits it entirely for some containers (raw MPEG-PS, for one), hence the
    /// two-step lookup and the null.</para>
    /// </summary>
    private static double? ReadBitRateKbps(JsonElement root, JsonElement video)
    {
        static double? Of(JsonElement e)
        {
            if (!e.TryGetProperty("bit_rate", out JsonElement value)) return null;
            if (!double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double bps))
                return null;
            return double.IsFinite(bps) && bps > 0 ? bps / 1000.0 : null;
        }

        if (root.TryGetProperty("format", out JsonElement format) && Of(format) is { } container)
            return container;
        return Of(video);
    }

    /// <summary>
    /// Duration of an existing product file: the video stream's own duration,
    /// falling back to the container's. Prefer <see cref="Source"/> when more
    /// than one field is needed.
    /// </summary>
    public static double? Duration(string ffprobe, FileInfo src)
    {
        // The source timeout, not the 30 s default: a container that stores no
        // per-stream duration makes ffprobe read the whole file, and over a slow
        // share that is minutes -- a TimeoutException here used to fail an encode
        // whose join had already succeeded.
        CaptureResult streamProbe = ProcessRunner.Run([
            ffprobe, "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=duration", "-of", "default=nw=1:nk=1", src.FullName,
        ], SourceTimeoutSeconds);

        if (streamProbe.ExitCode == 0
            && double.TryParse(streamProbe.Stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value) && value > 0)
        {
            return value;
        }

        CaptureResult formatProbe = ProcessRunner.Run([
            ffprobe, "-v", "error", "-show_entries", "format=duration",
            "-of", "default=nw=1:nk=1", src.FullName,
        ], SourceTimeoutSeconds);
        if (formatProbe.ExitCode != 0) return null;

        return double.TryParse(formatProbe.Stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double formatValue)
            && double.IsFinite(formatValue) && formatValue > 0
            ? formatValue
            : null;
    }

    /// <summary>
    /// Number of video packets, counted without decoding.
    ///
    /// v7.9.2: the shard-join check needs this because a container can lose the
    /// picture while keeping the sound. FLV writes no per-stream duration, so
    /// <see cref="Duration"/> falls back to the format duration -- which the
    /// audio track keeps at full length even when the last shard's video never
    /// made it. Counting packets catches exactly that.
    /// </summary>
    public static int? VideoPacketCount(string ffprobe, FileInfo src)
    {
        // -count_packets reads every packet in the file: on a large or remote
        // file that is far beyond the 30 s default.
        CaptureResult p = ProcessRunner.Run([
            ffprobe, "-v", "error", "-select_streams", "v:0", "-count_packets",
            "-show_entries", "stream=nb_read_packets", "-of", "default=nw=1:nk=1", src.FullName,
        ], SourceTimeoutSeconds);
        if (p.ExitCode != 0) return null;
        return int.TryParse(p.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            && n > 0
            ? n
            : null;
    }
}
