using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.Core;

/// <summary>Config validation rules ported from validate_common_config / related helpers.</summary>
public static class Validation
{
    public static void ValidateOutputExtension(FileInfo dst, EncoderInfo encoder)
    {
        if (!Constants.FormatExtensions.TryGetValue(encoder.FormatName, out IReadOnlySet<string>? allowed))
            return;

        string suffix = dst.Extension.ToLowerInvariant();
        if (allowed.Contains(suffix)) return;

        string expected = string.Join(", ", allowed.OrderBy(x => x, StringComparer.Ordinal));
        string hint = encoder.Name == "roqvideo"
            ? "RoQ video is not an AVI codec/container combination; use a .roq output or choose an "
                + "AVI-compatible encoder such as mpeg4/libxvid."
            : $"Use one of: {expected}, or choose an encoder/container that matches the requested extension.";

        string suffixText = suffix.Length == 0 ? "(none)" : suffix;
        throw new ArgumentException(
            $"Output extension '{suffixText}' does not match selected muxer "
            + $"'{encoder.FormatName}' for encoder '{encoder.Name}'. {hint}");
    }

    public static (int? AudioRate, int AudioChannels, int AudioKbps) EffectiveAudioSettings(
        EncoderInfo encoder, string? audioEncoder, int? audioRate, int audioChannels, int audioKbps)
    {
        if (audioEncoder == "roq_dpcm")
        {
            if (encoder.FormatName != "roq")
                throw new ArgumentException("roq_dpcm audio is only valid in the RoQ (.roq) container");
            if (audioChannels is not (1 or 2))
                throw new ArgumentException("roq_dpcm supports mono or stereo audio only; choose 1 or 2 channels");
            // FFmpeg's roq_dpcm encoder rejects every sample rate except 22050 Hz.
            return (22050, audioChannels, audioKbps);
        }

        // Everything else is snapped instead of rejected: an unsupported rate is
        // a value the encoder cannot honour, not a decision to refuse the run.
        // (dialup98 used to ask ac3 for 16 kHz and got a zero-byte file.) The
        // bitrate is clamped the same way -- mp2 at 44.1 kHz starts at 32 kbps
        // and wmav2 at 24 -- and the container is passed along because the
        // muxer can be the stricter side (FLV takes 11025/22050/44100/48000 for
        // MP3, MP4 refuses MP3 below 16 kHz).
        return AudioCatalog.Effective(
            audioEncoder, audioRate, audioChannels, audioKbps, encoder.FormatName);
    }

    /// <summary>
    /// Hard rules for an audio track: what cannot be fixed by snapping a value
    /// and therefore has to stop the run. Returns a message, or null.
    /// </summary>
    public static string? ValidateAudio(Config c)
    {
        if (c.Encoder is null || c.AudioEncoder is null) return null;
        return AudioCatalog.Validate(
            c.AudioEncoder, c.Encoder.FormatName, c.AudioRate, c.AudioChannels, c.AudioKbps);
    }

    /// <summary>
    /// The container the encoder will actually write: null keeps its default.
    /// Rejected when the encoder cannot write it, which is a real error rather
    /// than something to fall back from -- a silent fallback would produce a
    /// file with an extension the user did not ask for.
    /// </summary>
    public static string? ValidateContainer(EncoderInfo encoder, string? requested)
    {
        if (!requested.HasText) return null;
        if (EncoderMath.ResolveContainer(encoder, requested) is not null) return null;

        string allowed = string.Join(", ",
            EncoderMath.AllowedContainers(encoder).Select(x => x.Format));
        return $"encoder '{encoder.Name}' cannot write '{requested.Trim()}'; it supports: {allowed}";
    }

    public static void ValidateCommonConfig(Config c)
    {
        var numeric = new Dictionary<string, double>
        {
            ["limit"] = c.Limit,
            ["recover"] = c.Recover,
            ["window"] = c.Window,
            ["fps"] = c.Fps,
            ["start time"] = c.StartTimeSeconds,
        };
        if (c.EndTimeSeconds is not null) numeric["end time"] = c.EndTimeSeconds.Value;

        foreach ((string name, double value) in numeric)
        {
            if (!double.IsFinite(value)) throw new ArgumentException($"{name} must be finite");
        }
        if (c.StartTimeSeconds < 0) throw new ArgumentException("start time must be >= 00:00:00");
        if (c.EndTimeSeconds is not null && c.EndTimeSeconds <= c.StartTimeSeconds)
            throw new ArgumentException("end time must be later than start time");
        if (c.Limit <= 0) throw new ArgumentException("limit must be > 0");
        if (c.Recover <= 0 || c.Recover >= c.Limit)
            throw new ArgumentException("recover must be > 0 and < limit");
        if (c.Window <= 0) throw new ArgumentException("window must be > 0");
        if (c.Fps <= 0) throw new ArgumentException("FPS must be > 0");
        if (c.Threads <= 0) throw new ArgumentException("threads must be > 0");
        if (c.BufferFrames <= 0) throw new ArgumentException("capture buffer must be > 0");
        if (c.Width <= 0 || c.Height <= 0) throw new ArgumentException("resolution must be positive");
        if (c.Qv < 0) throw new ArgumentException("quality must be >= 0");
        if (c.AudioKbps <= 0) throw new ArgumentException("audio bitrate must be > 0");
        if (c.AudioChannels <= 0) throw new ArgumentException("audio channels must be > 0");
        if (c.AudioRate is not null && c.AudioRate <= 0)
            throw new ArgumentException("audio sample rate must be > 0");

        if (c.Encoder is not null)
        {
            string? containerError = ValidateContainer(c.Encoder, c.Container);
            if (containerError is not null) throw new ArgumentException(containerError);
        }

        string? audioError = ValidateAudio(c);
        if (audioError is not null) throw new ArgumentException(audioError);

        c.ScaleAlgo = EncoderMath.CanonicalScaleAlgo(c.ScaleAlgo);
        c.FitMode = EncoderMath.CanonicalFitMode(c.FitMode);
        c.CropAnchor = EncoderMath.CanonicalCropAnchor(c.CropAnchor);
        if (c.Deinterlace != "off" && !Constants.DeinterlaceFilters.Contains(c.Deinterlace))
            throw new ArgumentException("unsupported deinterlace filter");
        if (c.DeintMode is not ("send_frame" or "send_field"))
            throw new ArgumentException("deinterlace mode must be send_frame or send_field");
        if (c.DeintParity is not ("auto" or "tff" or "bff"))
            throw new ArgumentException("deinterlace parity must be auto, tff, or bff");
    }
}
