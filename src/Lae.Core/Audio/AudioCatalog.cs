using System.Collections.Frozen;
using System.Globalization;

namespace Lae.Core;

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
///
/// v7.8.2 added the bitrate dimension. Three encoders refuse values that look
/// perfectly reasonable, and each refuses them differently:
///   * <c>mp2</c> only takes the standard Layer II bitrates, and which ones
///     depends on the sample rate -- 8-160 kbps at 16/22.05/24 kHz (MPEG-2),
///     32-384 kbps at 32/44.1/48 kHz (MPEG-1), with nothing in between;
///   * <c>wmav1</c>/<c>wmav2</c> bottom out at 24 kbps;
///   * <c>libvorbis</c> scales both ends of its band with the sample rate
///     (8-40 kbps at 8 kHz, 32-224 kbps at 44.1/48 kHz).
/// Every number below was probed against the rolling FFmpeg master build the
/// toolchain downloads, not taken from a spec sheet.
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

    /// <summary>
    /// Bitrate band per sample rate, ascending by rate. The band with the
    /// largest rate not exceeding the actual one wins; the first entry applies
    /// below all of them. Empty falls back to <see cref="MinKbps"/>/
    /// <see cref="MaxKbps"/> at every rate.
    /// <para>
    /// <see cref="KbpsBand.Values"/> carries the discrete bitrates the encoder
    /// accepts at that rate, for the codecs that have no free range (mp2:
    /// 144 kbps is a valid MPEG-2 Layer II bitrate and an invalid MPEG-1 one,
    /// so the set is per band, not per codec). Empty means every value inside
    /// the band.
    /// </para>
    /// </summary>
    public (int Rate, int MinKbps, int MaxKbps, int[] Values)[] KbpsBands { get; init; } = [];

    /// <summary>
    /// FFmpeg marks this encoder experimental: it only opens with
    /// <c>-strict</c>, which this tool never passes, so selecting it would fail
    /// the encode. Offered by the catalogue but refused by
    /// <see cref="AudioCatalog.Validate"/>.
    /// </summary>
    public bool Experimental { get; init; }

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
            // 16 kHz and up: FFmpeg's mp2 encoder rejects 8/11.025/12 kHz with
            // "Specified sample rate ... is not supported".
            SampleRates = [16000, 22050, 24000, 32000, 44100, 48000],
            MaxChannels = 2,
            Note = "16 kHz and up; MPEG-1 rates need 32+ kbps",
            // MPEG-2 Layer II (16/22.05/24 kHz) runs 8-160 kbps, MPEG-1
            // (32/44.1/48 kHz) 32-384 kbps, and only the standard bitrates are
            // accepted: 36 kbps fails at 44.1 kHz even though it is inside the
            // band, 40 kbps fails at 32 kHz and works at 16 kHz, and 144 kbps
            // is the mirror image (valid at 16 kHz, not at 44.1).
            KbpsBands = [
                (16000, 8, 160, [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]),
                (32000, 32, 384, [32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384]),
            ],
        },
        new()
        {
            Name = "mp2fixed",
            SampleRates = [16000, 22050, 24000, 32000, 44100, 48000],
            MaxChannels = 2,
            Note = "16 kHz and up; MPEG-1 rates need 32+ kbps",
            KbpsBands = [
                (16000, 8, 160, [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]),
                (32000, 32, 384, [32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384]),
            ],
        },
        new()
        {
            Name = "wmav1",
            SampleRates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000],
            MaxChannels = 2,
            // WMA bottoms out at 24 kbps at every rate; 16 kbps opens FFmpeg
            // with "Bitrate 16000 is not supported" style failures.
            MinKbps = 24,
            Note = "24 kbps and up",
        },
        new()
        {
            Name = "wmav2",
            SampleRates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000],
            MaxChannels = 2,
            MinKbps = 24,
            Note = "24 kbps and up",
        },
        new()
        {
            Name = "libvorbis",
            SampleRates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000],
            MaxChannels = 8,
            Note = "band scales with rate: 8-40 kbps at 8 kHz ... 32-224 at 48 kHz",
            // Vorbis manages bitrate per rate: the nominal ceiling at 8 kHz is
            // 40 kbps and 224 kbps at 44.1/48 kHz, so a 128 kbps request at
            // 22050 Hz fails with "encoder setup failed" while the same request
            // at 44100 Hz is fine.
            KbpsBands = [
                (8000, 8, 40, []), (11025, 12, 48, []), (12000, 12, 48, []), (16000, 16, 96, []),
                (22050, 16, 80, []), (24000, 16, 80, []), (32000, 32, 160, []),
                (44100, 32, 224, []), (48000, 32, 224, []),
            ],
        },
        new()
        {
            Name = "vorbis",
            SampleRates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000],
            MaxChannels = 8,
            Experimental = true,
        },
        new()
        {
            // v7.9.8.1: RealAudio 1.0, the voice track of the RealMedia years.
            // The encoder has exactly one mode -- 8 kHz, mono, s16 -- and no
            // other rate can be honoured, so anything else is snapped rather
            // than refused (the same rule as ac3 at 16 kHz, and for the same
            // reason: a value it cannot honour is not a reason to lose the run).
            //
            // Its bitrate is not a knob either: FFmpeg accepts -b:a and throws
            // it away (measured 2026-10-27 on N-128100: -b:a 32k produced the
            // same 8 kbps stream as no -b:a at all). Hence the wide band -- a
            // narrow one would reject perfectly valid runs and change nothing.
            Name = "real_144",
            SampleRates = [8000],
            MaxChannels = 1,
            Note = "RealAudio 1.0: 8 kHz mono only, fixed 14.4 kbps",
        },
        // Opus always runs at 48 kHz internally; FFmpeg resamples, but only
        // these input rates are accepted by the encoder's own rate list.
        new()
        {
            Name = "libopus",
            SampleRates = [8000, 12000, 16000, 24000, 48000],
            MaxChannels = 8,
            MaxKbps = 256,
        },
        new() { Name = "opus", SampleRates = [8000, 12000, 16000, 24000, 48000], MaxChannels = 8, Experimental = true },
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
        ["mp4"] = ["aac", "libopus", "libmp3lame"],
        ["matroska"] = ["aac", "ac3", "libmp3lame", "mp2", "libvorbis",
                        "libopus", "flac", "wmav2", "pcm_s16le"],
        ["mpeg"] = ["mp2", "mp3_mf", "libmp3lame", "ac3", "pcm_s16le"],
        ["asf"] = ["wmav2", "wmav1", "libmp3lame", "mp3_mf", "ac3", "pcm_s16le"],
        ["ogg"] = ["libvorbis", "libopus", "flac"],
        // v7.9.8.1: real_144 leads. Until this version the entry was just the
        // muxer's own default (ac3), which is a codec no RealMedia player ever
        // had -- a 2003 .rm that only VLC can open is not an archive of 2003.
        ["rm"] = ["real_144", "ac3", "aac"],
        ["flv"] = ["aac", "libmp3lame", "mp3_mf"],
        ["mov"] = ["aac", "libmp3lame", "ac3", "pcm_s16le"],
        ["amv"] = ["adpcm_ima_amv", "adpcm_ima_wav"],
        ["roq"] = ["roq_dpcm"],
        ["a64"] = [],
        ["h261"] = [],
        ["h263"] = [],
    };

    /// <summary>
    /// Sample rates the <i>muxer</i> accepts, when it is stricter than the
    /// codec. Keyed "container|codec". Two real cases, both probed:
    /// <list type="bullet">
    /// <item>FLV writes a 2-bit SoundRate for MP3: 8/12/16/24/32 kHz abort with
    /// "FLV does not support sample rate ..., choose from (44100, 22050,
    /// 11025)". 48000 is accepted too and stored truthfully.</item>
    /// <item>MP4 refuses MP3 below 16 kHz: "muxing mp3 at 8000hz is not
    /// standard, to mux anyway set strict to -1" -- and this tool never passes
    /// -strict. AVI, MKV, MOV and ASF all take the full codec range.</item>
    /// </list>
    /// </summary>
    private static readonly FrozenDictionary<string, int[]> MuxerRates =
        new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["flv|libmp3lame"] = [11025, 22050, 44100, 48000],
            ["flv|mp3_mf"] = [11025, 22050, 44100, 48000],
            ["mp4|libmp3lame"] = [16000, 22050, 24000, 32000, 44100, 48000],
            ["mp4|mp3_mf"] = [16000, 22050, 24000, 32000, 44100, 48000],
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static AudioCodecInfo? Find(string? name)
    {
        if (!name.HasText) return null;
        return Known.FirstOrDefault(
            x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether this codec can be muxed into <paramref name="containerFormat"/>.
    /// An unmodelled container (absent from <see cref="ContainerCodecs"/>) is
    /// permissive: the codec list in this file cannot know about a muxer it has
    /// never been told about.
    ///
    /// Only <see cref="Compatible"/> is what the picker offers, because
    /// availability in the current build is the other half of the question --
    /// v7.8.1 retired the helpers that answered this one alone
    /// (<c>Names</c>, <c>IsKnown</c>, <c>KnownFor</c>; see
    /// <c>archive/retired/RetiredConstants.cs</c>).
    /// </summary>
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

    /// <summary>
    /// The rates this codec may use in this container: the codec's own list,
    /// narrowed by the muxer when the muxer is the stricter of the two. Empty
    /// means unconstrained.
    /// </summary>
    public static int[] RatesFor(string? codec, string? container = null)
    {
        AudioCodecInfo? info = Find(codec);
        int[] codecRates = info is null || info.SampleRates.Length == 0
            ? []
            : info.SampleRates;

        if (codec.HasText && container.HasText
            && MuxerRates.TryGetValue($"{container}|{codec}", out int[]? muxer))
        {
            return codecRates.Length == 0
                ? muxer
                : codecRates.Where(muxer.Contains).ToArray();
        }
        return codecRates;
    }

    /// <summary>Closest supported rate, or the requested one when unconstrained.</summary>
    public static int? NearestRate(string? codec, int? rate, string? container = null)
    {
        int[] allowed = RatesFor(codec, container);
        if (allowed.Length == 0 || rate is null) return rate;
        if (allowed.Contains(rate.Value)) return rate.Value;

        int best = allowed[0];
        foreach (int candidate in allowed)
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
    /// The bitrate band this codec accepts at this sample rate. Without a rate
    /// (auto) the widest band is returned, which is the permissive reading: the
    /// rate is only known once the source is probed.
    /// </summary>
    public static (int MinKbps, int MaxKbps, int[] Values) KbpsBand(string? codec, int? rate)
    {
        AudioCodecInfo? info = Find(codec);
        if (info is null) return (8, 640, []);
        if (info.KbpsBands.Length == 0) return (info.MinKbps, info.MaxKbps, []);

        (int Rate, int MinKbps, int MaxKbps, int[] Values) chosen = info.KbpsBands[0];
        foreach ((int Rate, int MinKbps, int MaxKbps, int[] Values) band in info.KbpsBands)
        {
            if (rate is null || band.Rate <= rate.Value) chosen = band;
        }
        return (chosen.MinKbps, chosen.MaxKbps, chosen.Values);
    }

    /// <summary>
    /// The bitrate the encoder will really be started with: clamped into the
    /// band for <paramref name="rate"/>, and snapped to the nearest standard
    /// value for codecs that only take discrete ones (mp2). Used for era
    /// presets and menu input, where a value is a suggestion; an explicit
    /// command-line value goes through <see cref="Validate"/> instead.
    /// </summary>
    public static int NearestKbps(string? codec, int kbps, int? rate = null)
    {
        AudioCodecInfo? info = Find(codec);
        if (info is null) return kbps;

        (int min, int max, int[] values) = KbpsBand(codec, rate);
        if (values.Length == 0) return Math.Clamp(kbps, min, max);

        // Discrete band (mp2): only the standard bitrates survive, and which
        // ones depends on the rate through the band.
        int best = values[0];
        foreach (int value in values)
        {
            if (Math.Abs(value - kbps) < Math.Abs(best - kbps)) best = value;
        }
        return best;
    }

    /// <summary>
    /// The settings the encoder will really be started with. An unsupported
    /// rate is snapped to the nearest supported one rather than allowed to
    /// abort FFmpeg mid-run; a bitrate outside the codec's band for that rate
    /// is clamped the same way.
    /// </summary>
    public static (int? Rate, int Channels, int Kbps) Effective(
        string? codec, int? rate, int channels, int kbps, string? container = null)
    {
        if (codec == "roq_dpcm")
        {
            // RoQ predates this table and is enforced separately (22050 Hz, mono
            // or stereo); keep that rule authoritative.
            return (22050, Math.Clamp(channels, 1, 2), kbps);
        }
        int? snappedRate = NearestRate(codec, rate, container);
        int snappedKbps = NearestKbps(codec, kbps, snappedRate);
        return (snappedRate, ClampChannels(codec, channels), snappedKbps);
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

        if (info.Experimental)
        {
            return $"'{codec}' is an experimental FFmpeg encoder: it only opens with "
                   + "-strict, which this tool never passes, so the encode would fail. "
                   + "Use the lib-prefixed encoder instead (libvorbis, libopus).";
        }

        if (!SupportsContainer(codec, containerFormat))
        {
            string allowed = ContainerCodecs.TryGetValue(containerFormat, out string[]? list)
                ? string.Join(", ", list) : "(none)";
            return $"audio encoder '{codec}' cannot be muxed into '{containerFormat}'; "
                   + $"that container accepts: {allowed}";
        }

        int[] rates = RatesFor(codec, containerFormat);
        if (rate is not null && rates.Length > 0 && !rates.Contains(rate.Value))
        {
            string wanted = string.Join(", ", rates.Select(x => x.ToString(CultureInfo.InvariantCulture)));
            // Phrase the stricter side as the one at fault: for FLV and MP4 the
            // muxer is what refuses, for ac3 it is the codec.
            bool muxerIsStricter = codec.HasText
                && MuxerRates.ContainsKey($"{containerFormat}|{codec}");
            return muxerIsStricter
                ? $"the '{containerFormat}' muxer only takes {wanted} Hz for '{codec}', not {rate.Value}"
                : $"'{codec}' does not support {rate.Value} Hz; supported sample rates: {wanted}";
        }
        if (channels < info.MinChannels || channels > info.MaxChannels)
        {
            return $"'{codec}' supports {info.MinChannels}-{info.MaxChannels} channel(s), not {channels}";
        }

        (int minKbps, int maxKbps, int[] values) = KbpsBand(codec, rate);
        if (values.Length > 0)
        {
            if (!values.Contains(kbps))
            {
                string wanted = string.Join(", ", values.Select(x => x.ToString(CultureInfo.InvariantCulture)));
                return $"'{codec}'"
                       + (rate is not null ? $" at {rate.Value} Hz" : "")
                       + $" does not take {kbps} kbps; standard bitrates: {wanted}";
            }
        }
        else if (kbps < minKbps || kbps > maxKbps)
        {
            return $"'{codec}'"
                   + (rate is not null ? $" at {rate.Value} Hz" : "")
                   + $" bitrate must be between {minKbps} and {maxKbps} kbps, not {kbps}";
        }
        return null;
    }
}
