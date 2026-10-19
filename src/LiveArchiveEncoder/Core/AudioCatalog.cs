using System.Globalization;
using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.Core;

/// <summary>
/// What one audio encoder actually accepts.
///
/// Before v7.8 the program only knew which container "usually" goes with which
/// audio codec, and never checked the numbers. That silently produced empty
/// files: <c>ac3</c> accepts only 32/44.1/48 kHz, and the dialup98 era preset
/// asked it for 16 kHz, so FFmpeg aborted with "Specified sample rate 16000 is
/// not supported by the ac3 encoder" and wrote a zero-byte archive.
///
/// Rates are the rates FFmpeg's encoder lists or accepts without resampling;
/// an empty list means "anything goes" (PCM, FLAC, ADPCM).
/// </summary>
public sealed class AudioCodecInfo
{
    public required string Name { get; init; }

    /// <summary>Empty means unconstrained.</summary>
    public int[] SampleRates { get; init; } = [];

    public int MinChannels { get; init; } = 1;
    public int MaxChannels { get; init; } = 8;

    /// <summary>Permissive on purpose: a low bitrate is a quality choice, an
    /// unsupported sample rate is a hard failure.</summary>
    public int MinKbps { get; init; } = 8;
    public int MaxKbps { get; init; } = 640;

    /// <summary>Short note shown in the picker, e.g. the rate restriction.</summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// Audio encoder catalogue: what fits a container, and what the numbers have to
/// be once one is chosen.
/// </summary>
public static class AudioCatalog
{
    private static readonly AudioCodecInfo[] Known =
    [
        new()
        {
            Name = "ac3",
            SampleRates = [32000, 44100, 48000],
            MaxChannels = 6,
            Note = "32 / 44.1 / 48 kHz only",
        },
        new()
        {
            Name = "eac3",
            SampleRates = [32000, 44100, 48000],
            MaxChannels = 6,
            Note = "32 / 44.1 / 48 kHz only",
        },
        new()
        {
            Name = "aac",
            SampleRates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000,
                           64000, 88200, 96000],
            MaxChannels = 8,
        },
        new()
        {
            Name = "libmp3lame",
            SampleRates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000],
            MaxChannels = 2,
        },
        new()
        {
            Name = "mp3_mf",
            SampleRates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000],
            MaxChannels = 2,
        },
        new()
        {
            Name = "mp2",
            SampleRates = [22050, 24000, 32000, 44100, 48000],
            MaxChannels = 2,
            Note = "22.05 kHz and up",
        },
        new()
        {
            Name = "mp2fixed",
            SampleRates = [22050, 24000, 32000, 44100, 48000],
            MaxChannels = 2,
        },
        new()
        {
            Name = "wmav1",
            SampleRates = [8000, 11025, 16000, 22050, 32000, 44100, 48000],
            MaxChannels = 2,
        },
        new()
        {
            Name = "wmav2",
            SampleRates = [8000, 11025, 16000, 22050, 32000, 44100, 48000],
            MaxChannels = 2,
        },
        new()
        {
            Name = "libvorbis", SampleRates = [8000, 11025, 16000, 22050, 32000, 44100, 48000],
            MaxChannels = 8,
        },
        new()
        {
            Name = "vorbis", SampleRates = [8000, 11025, 16000, 22050, 32000, 44100, 48000],
            MaxChannels = 8,
        },
        // Opus always runs at 48 kHz internally; FFmpeg resamples, but only
        // these input rates are accepted by the encoder's own rate list.
        new()
        {
            Name = "libopus", SampleRates = [8000, 12000, 16000, 24000, 48000],
            MaxChannels = 8,
        },
        new() { Name = "opus", SampleRates = [8000, 12000, 16000, 24000, 48000], MaxChannels = 8 },
        new()
        {
            Name = "adpcm_ima_amv", SampleRates = [22050], MaxChannels = 1,
            Note = "AMV: 22050 Hz mono only",
        },
        new()
        {
            Name = "adpcm_ima_wav", MaxChannels = 2,
            Note = "any rate",
        },
        new()
        {
            Name = "roq_dpcm", SampleRates = [22050], MaxChannels = 2,
            Note = "RoQ: 22050 Hz only",
        },
        new() { Name = "flac", MaxChannels = 8, Note = "any rate" },
        new() { Name = "pcm_s16le", MaxChannels = 8, Note = "any rate, no compression" },
        new() { Name = "pcm_u8", MaxChannels = 8, Note = "any rate, no compression" },
    ];

    /// <summary>
    /// Which audio codecs each muxer accepts. A superset of the preference list
    /// in <see cref="Constants.DefaultAudioByContainer"/>: the scan menu offers
    /// everything that muxes, while the automatic choice still walks the
    /// preference list first.
    /// </summary>
    private static readonly Dictionary<string, string[]> ContainerCodecs = new()
    {
        ["avi"] = ["libmp3lame", "mp3_mf", "mp2", "aac", "ac3", "wmav1", "wmav2",
                   "adpcm_ima_wav", "pcm_s16le"],
        ["mp4"] = ["aac", "libmp3lame", "ac3", "libopus", "opus", "flac"],
        ["matroska"] = ["aac", "ac3", "libmp3lame", "mp2", "libvorbis", "vorbis",
                        "libopus", "opus", "flac", "wmav2", "pcm_s16le"],
        ["mpeg"] = ["mp2", "mp3_mf", "libmp3lame", "ac3", "pcm_s16le"],
        ["asf"] = ["wmav2", "wmav1", "libmp3lame", "mp3_mf", "ac3", "pcm_s16le"],
        ["ogg"] = ["libvorbis", "vorbis", "libopus", "opus", "flac"],
        ["rm"] = ["ac3", "aac"],
        ["flv"] = ["aac", "libmp3lame", "mp3_mf"],
        ["mov"] = ["aac", "libmp3lame", "ac3", "pcm_s16le"],
        ["amv"] = ["adpcm_ima_amv", "adpcm_ima_wav"],
        ["roq"] = ["roq_dpcm"],
        ["a64"] = [],
        ["h261"] = [],
        ["h263"] = [],
    };

    public static AudioCodecInfo? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Known.FirstOrDefault(
            x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> Names => [.. Known.Select(x => x.Name)];

    /// <summary>True when the codec is one we know; unknown codecs are unconstrained.</summary>
    public static bool IsKnown(string? name) => Find(name) is not null;

    /// <summary>Codecs known to mux into a container, whether or not this build
    /// has them. Used for "that container accepts: ..." messages.</summary>
    public static IReadOnlyList<string> KnownFor(string containerFormat) =>
        ContainerCodecs.TryGetValue(containerFormat, out string[]? list) ? list : [];

    public static bool SupportsContainer(string? codec, string containerFormat)
    {
        if (codec is null) return false;
        if (!ContainerCodecs.TryGetValue(containerFormat, out string[]? allowed)) return true;
        return allowed.Contains(codec, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Audio encoders that both exist in this FFmpeg build and mux into
    /// <paramref name="containerFormat"/>. The container's preference list comes
    /// first so the automatic pick keeps behaving as it always did.
    /// </summary>
    public static List<string> Compatible(string containerFormat, ICollection<string> available)
    {
        if (Constants.NoAudioFormats.Contains(containerFormat)) return [];

        var ordered = new List<string>();
        void Add(string name)
        {
            if (available.Contains(name) && !ordered.Contains(name, StringComparer.OrdinalIgnoreCase))
                ordered.Add(name);
        }

        if (Constants.DefaultAudioByContainer.TryGetValue(containerFormat, out string[]? preferred))
        {
            foreach (string name in preferred) Add(name);
        }
        if (ContainerCodecs.TryGetValue(containerFormat, out string[]? allowed))
        {
            foreach (string name in allowed) Add(name);
        }
        // Anything this build has that we do not model still has to be reachable:
        // refusing to encode with an encoder the user can see would be worse
        // than offering it unchecked.
        if (ordered.Count == 0)
        {
            foreach (string name in available.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) Add(name);
        }
        return ordered;
    }

    /// <summary>Rate list as text for menus, e.g. "32000, 44100, 48000".</summary>
    public static string DescribeRates(string? codec)
    {
        AudioCodecInfo? info = Find(codec);
        if (info is null || info.SampleRates.Length == 0) return "auto";
        int[] rates = info.SampleRates;
        if (rates.Length > 6)
        {
            // A dozen rates would wreck the menu column and say less than the
            // range does; the restriction worth knowing is a short list, like
            // ac3's three.
            return $"{rates[0]}-{rates[^1]}";
        }
        return string.Join(", ", rates.Select(x => x.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Closest supported rate, or the requested one when unconstrained.</summary>
    public static int? NearestRate(string? codec, int? rate)
    {
        AudioCodecInfo? info = Find(codec);
        if (info is null || info.SampleRates.Length == 0 || rate is null) return rate;
        if (info.SampleRates.Contains(rate.Value)) return rate.Value;

        int best = info.SampleRates[0];
        foreach (int candidate in info.SampleRates)
        {
            if (Math.Abs(candidate - rate.Value) < Math.Abs(best - rate.Value)) best = candidate;
        }
        return best;
    }

    public static int ClampChannels(string? codec, int channels)
    {
        AudioCodecInfo? info = Find(codec);
        if (info is null) return channels;
        return Math.Clamp(channels, info.MinChannels, info.MaxChannels);
    }

    /// <summary>
    /// The settings the encoder will really be started with. An unsupported rate
    /// is snapped to the nearest supported one rather than allowed to abort
    /// FFmpeg mid-run.
    /// </summary>
    public static (int? Rate, int Channels) Effective(string? codec, int? rate, int channels)
    {
        if (codec == "roq_dpcm")
        {
            // RoQ predates this table and is enforced separately (22050 Hz, mono
            // or stereo); keep that rule authoritative.
            return (22050, Math.Clamp(channels, 1, 2));
        }
        return (NearestRate(codec, rate), ClampChannels(codec, channels));
    }

    /// <summary>
    /// Reject what FFmpeg would reject. Returns a message, or null when the
    /// combination is fine. An unknown codec is never rejected: it is one this
    /// build has and this table does not model, so guessing would only block
    /// working encodes.
    /// </summary>
    public static string? Validate(
        string? codec, string containerFormat, int? rate, int channels, int kbps)
    {
        if (codec is null) return null;

        if (Constants.NoAudioFormats.Contains(containerFormat))
            return $"the '{containerFormat}' container carries no audio stream";

        // An unmodelled codec (one this build has and this table does not) is
        // left alone entirely: guessing at its limits would only refuse a
        // working encode.
        AudioCodecInfo? info = Find(codec);
        if (info is null) return null;

        if (!SupportsContainer(codec, containerFormat))
        {
            string allowed = ContainerCodecs.TryGetValue(containerFormat, out string[]? list)
                ? string.Join(", ", list) : "(none)";
            return $"audio encoder '{codec}' cannot be muxed into '{containerFormat}'; "
                   + $"that container accepts: {allowed}";
        }

        if (rate is not null && info.SampleRates.Length > 0 && !info.SampleRates.Contains(rate.Value))
        {
            return $"'{codec}' does not support {rate.Value} Hz; supported sample rates: "
                   + string.Join(", ", info.SampleRates.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        }
        if (channels < info.MinChannels || channels > info.MaxChannels)
        {
            return $"'{codec}' supports {info.MinChannels}-{info.MaxChannels} channel(s), not {channels}";
        }
        if (kbps < info.MinKbps || kbps > info.MaxKbps)
        {
            return $"'{codec}' bitrate must be between {info.MinKbps} and {info.MaxKbps} kbps, not {kbps}";
        }
        return null;
    }
}
