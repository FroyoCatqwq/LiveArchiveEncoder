using System.Globalization;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>
/// One video encoder the second pass may use, with the containers it actually
/// writes and the knob that means "quality" for it.
///
/// v7.9.5.1: before this table the recompress fragment was free text with a
/// container that defaulted to <c>mp4</c> no matter what codec the fragment
/// named -- <c>-c:v libvpx-vp9</c> into "clip_web.mp4" failed inside FFmpeg
/// with a muxer message, several seconds after the run had started. Naming the
/// codec now also selects the container, and a combination the muxer cannot
/// hold is refused before anything runs.
/// </summary>
/// <param name="Name">Encoder name as FFmpeg spells it.</param>
/// <param name="Containers">Containers it can be written to, most idiomatic first.</param>
/// <param name="QualityFlag">"-crf" (x264/x265/vp9/av1), "-q:v" (MPEG-style) or
/// "" when the encoder only understands a bitrate.</param>
/// <param name="DefaultQuality">Value used when the user did not pick one.</param>
/// <param name="ExtraArgs">Arguments this codec needs to produce a playable file.</param>
public sealed record SecondPassVideoCodec(
    string Name,
    string[] Containers,
    string QualityFlag,
    int DefaultQuality,
    string[] ExtraArgs);

/// <summary>An audio encoder the second pass may use.</summary>
public sealed record SecondPassAudioCodec(
    string Name,
    string[] Containers,
    int DefaultKbps,
    string[] ExtraArgs);

/// <summary>
/// The catalog behind <c>--recompress-codec</c> / <c>--recompress-audio-codec</c>
/// and behind the era presets, so a preset and a hand-typed codec can never
/// disagree about which container is legal.
///
/// Only encoders that were actually run on this project's test material are
/// listed: <c>libx265</c>, <c>libvpx-vp9</c>, <c>libvpx</c> (VP8), <c>libaom-av1</c>
/// and <c>libsvtav1</c> were each encoded and probed back by codec name
/// (2026-10-24), and the legacy ones are the same names the live pass uses.
/// </summary>
public static class RecompressCodecs
{
    public static readonly IReadOnlyList<SecondPassVideoCodec> Video =
    [
        new("libx264", ["mp4", "mkv", "mov", "avi", "flv"], "-crf", 26,
            ["-preset", "veryfast", "-pix_fmt", "yuv420p"]),
        // HEVC in MP4 only opens in a player that understands the hvc1 tag;
        // without it QuickTime and Windows' own player refuse the file.
        new("libx265", ["mp4", "mkv", "mov"], "-crf", 30,
            ["-preset", "medium", "-tag:v", "hvc1", "-pix_fmt", "yuv420p"]),
        new("libvpx-vp9", ["webm", "mkv"], "-crf", 34,
            ["-b:v", "0", "-row-mt", "1", "-pix_fmt", "yuv420p"]),
        // VP8 has no constant-quality mode: -crf is a floor and -b:v the target.
        new("libvpx", ["webm", "mkv"], "-crf", 12,
            ["-b:v", "1M", "-pix_fmt", "yuv420p"]),
        new("libaom-av1", ["mkv", "webm", "mp4"], "-crf", 32,
            ["-b:v", "0", "-cpu-used", "8", "-row-mt", "1", "-pix_fmt", "yuv420p"]),
        new("libsvtav1", ["mkv", "webm", "mp4"], "-crf", 40,
            ["-preset", "8", "-pix_fmt", "yuv420p"]),
        // The 2005-2009 web codec. Sorenson Spark (flv1) is what a Chinese
        // portal's transcoder actually produced; writing H.264 and calling it
        // "2007" would be a cleaner picture than the era ever had.
        new("flv", ["flv"], "-q:v", 6, ["-pix_fmt", "yuv420p"]),
        new("mpeg4", ["avi", "mp4", "mkv", "mov"], "-q:v", 6, ["-pix_fmt", "yuv420p"]),
        new("mpeg2video", ["mpeg", "avi", "mkv", "mov"], "-q:v", 6, ["-pix_fmt", "yuv420p"]),
        new("mpeg1video", ["mpeg", "avi", "mkv"], "-q:v", 8, ["-pix_fmt", "yuv420p"]),
        new("mjpeg", ["avi", "mov", "mkv"], "-q:v", 4, ["-pix_fmt", "yuvj420p"]),
        new("wmv2", ["asf", "avi"], "-q:v", 6, []),
        new("h263", ["avi", "flv"], "-q:v", 6, ["-pix_fmt", "yuv420p"]),
    ];

    public static readonly IReadOnlyList<SecondPassAudioCodec> Audio =
    [
        new("aac", ["mp4", "mkv", "mov", "flv", "avi"], 96, []),
        new("libmp3lame", ["mp4", "mkv", "avi", "flv", "mov"], 96, []),
        // Opus and Vorbis are WebM/MKV residents; putting them in MP4 needs
        // -strict experimental and no consumer player expects it.
        new("libopus", ["webm", "mkv", "ogg"], 64, []),
        new("libvorbis", ["webm", "mkv", "ogg"], 96, []),
        new("ac3", ["mkv", "avi", "mp4", "mov"], 192, []),
        new("flac", ["mkv", "ogg"], 0, []),
        // v7.9.6.3: the Windows Media pair the 2002-2004 portals served. Measured
        // on 2026-10-25 with ffmpeg N-128013: wmv2 + wmav2 at 32 kbps / 22.05 kHz
        // mono into .wmv probes back as codec_name wmv2 / wmav2 in an asf
        // container, and AVI takes the same pair.
        new("wmav2", ["asf", "avi"], 32, []),
    ];

    /// <summary>Audio encoders by name, plus the "leave the track alone" spelling.</summary>
    public const string CopyAudio = "copy";

    public static SecondPassVideoCodec? FindVideo(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Video.FirstOrDefault(v => string.Equals(v.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static SecondPassAudioCodec? FindAudio(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string wanted = name.Trim();
        if (string.Equals(wanted, CopyAudio, StringComparison.OrdinalIgnoreCase)) return null;
        return Audio.FirstOrDefault(a => string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCopyAudio(string? name) =>
        string.Equals(name?.Trim(), CopyAudio, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The audio encoder that belongs with a video codec when the user did not
    /// name one: Opus for the WebM family (the only combination a browser
    /// expects), AAC for everything else, MP3 for the FLV era.
    /// </summary>
    public static string RecommendedAudio(string videoCodec) => videoCodec.Trim().ToLowerInvariant() switch
    {
        "flv" or "h263" => "libmp3lame",
        // WMV wants its own audio: mp3-in-ASF plays, but a 2003 .wmv whose audio
        // codec is mp3 is not what the site served.
        "wmv2" => "wmav2",
        "libvpx-vp9" or "libvpx" or "libaom-av1" or "libsvtav1" => "libopus",
        _ => "aac",
    };

    /// <summary>Containers both halves can live in, most idiomatic first.</summary>
    public static List<string> ContainersFor(string videoCodec, string? audioCodec)
    {
        SecondPassVideoCodec? video = FindVideo(videoCodec);
        if (video is null) return [];

        if (IsCopyAudio(audioCodec)) return [.. video.Containers];
        SecondPassAudioCodec? audio = FindAudio(audioCodec);
        if (audio is null) return [.. video.Containers];
        return [.. video.Containers.Where(c => audio.Containers.Contains(c, StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// The container a codec pair implies, or null when the pair shares none.
    /// </summary>
    public static string? DefaultContainer(string videoCodec, string? audioCodec)
    {
        List<string> shared = ContainersFor(videoCodec, audioCodec);
        return shared.Count > 0 ? shared[0] : null;
    }

    /// <summary>
    /// Check a requested container against the pair. Returns null when it is
    /// fine, otherwise the sentence the caller prints.
    /// </summary>
    public static string? ValidateContainer(string videoCodec, string? audioCodec, string container)
    {
        string wanted = container.Trim().TrimStart('.').ToLowerInvariant();
        string audio = IsCopyAudio(audioCodec) ? CopyAudio : (audioCodec ?? "").Trim().ToLowerInvariant();

        SecondPassVideoCodec? video = FindVideo(videoCodec);
        if (video is null)
            return $"unknown video codec '{videoCodec}'; known: {string.Join(", ", VideoNames)}";
        if (!video.Containers.Contains(wanted, StringComparer.OrdinalIgnoreCase))
            return $"{video.Name} cannot be written to .{wanted}; it supports {string.Join(", ", video.Containers)}";

        if (IsCopyAudio(audio)) return null;
        // No audio codec named (or one the caller does not want checked): the
        // video half's containers are all that can be verified here.
        if (!audio.HasText) return null;
        SecondPassAudioCodec? audioInfo = FindAudio(audio);
        if (audioInfo is null)
            return $"unknown audio codec '{audio}'; known: {string.Join(", ", AudioNames)}, or {CopyAudio}";
        if (!audioInfo.Containers.Contains(wanted, StringComparer.OrdinalIgnoreCase))
            return $"{audioInfo.Name} cannot be written to .{wanted}; it supports {string.Join(", ", audioInfo.Containers)}";
        return null;
    }

    /// <summary>
    /// The video half of a fragment: the codec, its quality knob and whatever
    /// else the encoder needs. Bitrate is used only where there is no quality
    /// knob at all.
    /// </summary>
    public static string VideoArgs(SecondPassVideoCodec codec, int? quality, int? kbps = null)
    {
        var parts = new List<string> { "-c:v", codec.Name };
        if (codec.QualityFlag.Length > 0 && quality is { } q)
            parts.AddRange([codec.QualityFlag, q.ToString(CultureInfo.InvariantCulture)]);
        else if (kbps is { } k)
            parts.AddRange(["-b:v", $"{k}k"]);

        parts.AddRange(codec.ExtraArgs);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// The video half at a fixed bitrate, which is how a site's ladder is
    /// written down. Any <c>-b:v</c> the codec carries for its constant-quality
    /// mode is dropped first -- VP9's "-b:v 0" would otherwise override the
    /// number the preset asked for.
    /// </summary>
    public static string VideoArgsAtBitrate(SecondPassVideoCodec codec, int kbps)
    {
        var parts = new List<string> { "-c:v", codec.Name, "-b:v", $"{kbps}k" };
        for (int i = 0; i < codec.ExtraArgs.Length; i++)
        {
            if (codec.ExtraArgs[i] == "-b:v")
            {
                i++;        // skip the value too
                continue;
            }
            parts.Add(codec.ExtraArgs[i]);
        }
        return string.Join(' ', parts);
    }

    /// <summary>The audio half; <c>-b:a 0</c> is omitted for lossless codecs.</summary>
    public static string AudioArgs(string? audioCodec, int? kbps, int? rate, int? channels)
    {
        var parts = new List<string>();
        if (IsCopyAudio(audioCodec) || string.IsNullOrWhiteSpace(audioCodec))
        {
            parts.AddRange(["-c:a", CopyAudio]);
            return string.Join(' ', parts);
        }

        SecondPassAudioCodec? info = FindAudio(audioCodec);
        parts.AddRange(["-c:a", info?.Name ?? audioCodec.Trim()]);
        int effective = kbps ?? info?.DefaultKbps ?? 96;
        // FLAC is lossless: "96k" is meaningless to it.
        if (effective > 0 && !string.Equals(info?.Name, "flac", StringComparison.OrdinalIgnoreCase))
            parts.AddRange(["-b:a", $"{effective}k"]);
        if (channels is { } ch) parts.AddRange(["-ac", ch.ToString(CultureInfo.InvariantCulture)]);
        if (rate is { } hz) parts.AddRange(["-ar", hz.ToString(CultureInfo.InvariantCulture)]);
        parts.AddRange(info?.ExtraArgs ?? []);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// The video codec a fragment names (<c>-c:v</c> / <c>-codec:v</c>), if it is
    /// one this catalog knows. Null means "the fragment does something the
    /// catalog cannot reason about" -- <c>copy</c>, a filter-based encode, a
    /// codec added by a future FFmpeg -- and the caller then skips the
    /// container check rather than guessing.
    /// </summary>
    public static string? DetectVideoCodec(IReadOnlyList<string> tokens)
    {
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i] is not ("-c:v" or "-codec:v" or "-vcodec")) continue;
            return FindVideo(tokens[i + 1])?.Name;
        }
        return null;
    }

    /// <summary>The audio codec a fragment names, same rules as above.</summary>
    public static string? DetectAudioCodec(IReadOnlyList<string> tokens)
    {
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i] is not ("-c:a" or "-codec:a" or "-acodec")) continue;
            string value = tokens[i + 1];
            return IsCopyAudio(value) ? CopyAudio : FindAudio(value)?.Name;
        }
        return null;
    }

    public static IEnumerable<string> VideoNames => Video.Select(v => v.Name);
    public static IEnumerable<string> AudioNames => Audio.Select(a => a.Name);
    public static string VideoIdList => string.Join(", ", VideoNames);
    public static string AudioIdList => string.Join(", ", AudioNames);
}
