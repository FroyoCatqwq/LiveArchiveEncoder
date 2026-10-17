using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.Core;

/// <summary>Selection/derivation helpers shared by CUI, CLI and the encoder.</summary>
public static class EncoderMath
{
    private static readonly string[] PreferredTransportFallback = ["yuv420p", "yuv422p", "yuv444p", "rgb24", "gray"];

    public static int DefaultQuality(string name) =>
        Constants.DefaultQuality.TryGetValue(name, out int value) ? value : 10;

    public static (double Fps, bool Exact) ChooseBaseFps(double requested, EncoderCaps caps)
    {
        List<double> rates = caps.SupportedRates;
        // AMV has no declared rate list of its own, and any fps that does not
        // divide 22050 makes the muxer reject the header, so its ladder is
        // mandatory rather than a fallback.
        if (caps.Name == "amv") rates = [.. Constants.AmvFps];
        if (caps.Name == "mpeg1video") rates = [.. Constants.Mpeg1RealFps];
        if (rates.Count == 0) return (requested, true);

        double nearest = rates[0];
        foreach (double value in rates)
        {
            if (Math.Abs(value - requested) < Math.Abs(nearest - requested)) nearest = value;
        }
        return (nearest, Math.Abs(nearest - requested) < 0.01);
    }

    public static List<double> SelectLevels(double baseFps, IEnumerable<double>? raw)
    {
        IEnumerable<double> rawValues = raw is null
            ? [baseFps, 8.0, 5.0, 3.0]
            : raw.Append(baseFps);

        var values = new List<double>();
        foreach (double item in rawValues)
        {
            if (double.IsFinite(item) && item > 0 && item <= baseFps + 1e-9)
                values.Add(item);
        }

        List<double> result = [.. values.Select(v => Math.Round(v, 6)).Distinct().OrderDescending()];
        return result.Count > 0 ? result : [baseFps];
    }

    public static int TargetLevel(double rate, double limit, List<double> levels)
    {
        if (levels.Count <= 1 || limit <= 0) return 0;
        double ratio = rate / limit;
        if (ratio >= 1.60) return levels.Count - 1;
        if (ratio >= 1.25) return Math.Min(2, levels.Count - 1);
        if (ratio >= 1.00) return Math.Min(1, levels.Count - 1);
        return 0;
    }

    /// <summary>
    /// Audio encoders with no rate control at all: FFmpeg accepts <c>-b:a</c> and
    /// then silently ignores it, so writing it only misleads whoever reads the
    /// command. Verified against this build with <c>ffmpeg -h encoder=NAME</c> --
    /// unlike libmp3lame / aac / ac3 / mp2 / wmav2, these expose no AVOptions.
    /// </summary>
    public static bool AudioIgnoresBitrate(string? encoder) =>
        encoder is not null
        && (encoder.StartsWith("adpcm", StringComparison.OrdinalIgnoreCase)
            || encoder.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(encoder, "roq_dpcm", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// ADPCM block size the AMV muxer demands: exactly one audio frame per video
    /// frame, i.e. <c>22050 / fps</c> samples.
    /// </summary>
    public static int AmvAudioBlockSize(double fps)
    {
        double exact = fps > 0 ? Constants.AmvAudioSampleRate / fps : 0.0;
        if (!double.IsFinite(exact) || exact <= 0) return 1024;
        return Math.Clamp((int)Math.Round(exact), 32, 8192);
    }

    public static (int W, int H, bool Exact) NormalizeDimensions(string name, int width, int height)
    {
        if (name == "amv") return AmvDimensions(width, height);

        if (!Constants.SpecialSizes.TryGetValue(name, out (int W, int H)[]? allowed) || allowed.Length == 0)
            return (width, height, true);
        if (allowed.Contains((width, height))) return (width, height, true);

        List<(int W, int H)> candidates = [.. allowed.Where(s => s.W <= width && s.H <= height)];
        if (candidates.Count == 0) candidates = [.. allowed];
        (int W, int H) chosen = candidates[0];
        foreach (var size in candidates)
        {
            if (Math.Abs(size.W - width) + Math.Abs(size.H - height)
                < Math.Abs(chosen.W - width) + Math.Abs(chosen.H - height))
            {
                chosen = size;
            }
        }
        return (chosen.W, chosen.H, false);
    }

    /// <summary>
    /// Snap a requested size down to the 16-pixel grid AMV decoders expect.
    /// FFmpeg happily writes other sizes while only warning, and the result is
    /// unplayable on the hardware this format exists for.
    /// </summary>
    private static (int W, int H, bool Exact) AmvDimensions(int width, int height)
    {
        int m = Constants.AmvDimensionMultiple;
        int w = Math.Max(m, width - width % m);
        int h = Math.Max(m, height - height % m);
        return (w, h, w == width && h == height);
    }

    public static string? ValidateRoqDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width % 16 != 0 || height % 16 != 0)
        {
            throw new ArgumentException(
                $"roqvideo requires width and height to be divisible by 16; got {width}x{height}");
        }
        static bool IsPow2(int v) => v > 0 && (v & (v - 1)) == 0;
        if (!IsPow2(width) || !IsPow2(height))
        {
            return $"RoQ output is {width}x{height}. FFmpeg can encode this size, but it is not "
                + "power-of-two and may not be compatible with Quake's RoQ decoder.";
        }
        return null;
    }

    public static string CanonicalScaleAlgo(string value)
    {
        string key = value.Trim().ToLowerInvariant();
        if (Constants.ScaleAlgorithmAliases.TryGetValue(key, out string? canonical)) return canonical;
        throw new ArgumentException(
            $"unknown scale algorithm '{value}'; choose: {string.Join(", ", Constants.ScaleAlgorithms)}");
    }

    public static string? ChooseAudioEncoder(string container, ICollection<string> available)
    {
        if (Constants.NoAudioFormats.Contains(container)) return null;
        if (!Constants.DefaultAudioByContainer.TryGetValue(container, out string[]? candidates))
        {
            candidates = ["aac", "libmp3lame"];
        }
        foreach (string candidate in candidates)
        {
            if (available.Contains(candidate)) return candidate;
        }
        return null;
    }

    // Kept for parity with the Python fallback chain used when no encoder declares
    // any usable raw transport format.
    public static string FallbackTransportFormat() => PreferredTransportFallback[0];
}
