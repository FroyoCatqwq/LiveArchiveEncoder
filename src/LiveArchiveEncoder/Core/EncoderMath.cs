using System.Globalization;
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

    /// <summary>
    /// Resolve the ladder: one rung per FPS value, with the bitrate that drops
    /// the encoder onto that rung and the bitrate that lets it climb back.
    ///
    /// Rung 0 is always the base rate. Its drop threshold is infinite because
    /// there is no rung above it to descend from.
    /// </summary>
    public static List<FpsRung> BuildLadder(
        double baseFps, IEnumerable<double>? levels,
        IEnumerable<FpsRungSetting>? settings, double limit, double recover)
    {
        List<double> fps = SelectLevels(baseFps, levels);
        List<FpsRungSetting>? rungs = settings?.ToList();

        var ladder = new List<FpsRung>(fps.Count);
        for (int i = 0; i < fps.Count; i++)
        {
            FpsRungSetting? configured = rungs?.FirstOrDefault(x => Math.Abs(x.Fps - fps[i]) < 1e-6);
            double drop = i == 0
                ? double.PositiveInfinity
                : configured?.DropAboveKbps ?? DefaultDropKbps(i, limit);
            double rise = configured?.RiseBelowKbps ?? recover;
            ladder.Add(new FpsRung(fps[i], drop, rise));
        }
        return ladder;
    }

    /// <summary>
    /// Default "descend onto rung N" threshold: the v7.6 multipliers of the
    /// bitrate ceiling, so an unconfigured ladder behaves exactly as before.
    /// </summary>
    public static double DefaultDropKbps(int rung, double limit)
    {
        double[] multipliers = [1.00, 1.25, 1.60];
        int index = rung - 1;   // rung 0 is the base rate and has no threshold
        double factor = index < multipliers.Length
            ? multipliers[index]
            : multipliers[^1] + 0.35 * (index - multipliers.Length + 1);
        return limit * factor;
    }

    /// <summary>
    /// The rung a bitrate belongs on: the deepest rung whose drop threshold the
    /// rate has reached. Taking the maximum rather than the last match keeps a
    /// user-supplied non-monotonic ladder from stranding the controller on a
    /// shallow rung.
    /// </summary>
    public static int TargetLevel(double rate, List<FpsRung> ladder)
    {
        if (ladder.Count <= 1) return 0;
        int target = 0;
        for (int i = 1; i < ladder.Count; i++)
        {
            if (rate >= ladder[i].DropAboveKbps) target = Math.Max(target, i);
        }
        return target;
    }

    /// <summary>
    /// Parse an FPS ladder written as <c>12</c>, <c>12:128</c> or
    /// <c>12:128:115</c> per entry (FPS, drop-above kbps, rise-below kbps),
    /// separated by commas or semicolons. A bare FPS keeps the derived
    /// thresholds, so <c>--fps-levels 13,8,5,3</c> still means what it always did.
    /// </summary>
    public static (List<double> Fps, List<FpsRungSetting> Settings, string? Error) ParseLevelSpec(
        string raw)
    {
        var fps = new List<double>();
        var settings = new List<FpsRungSetting>();

        foreach (string token in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = token.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length > 3)
                return ([], [], $"'{token.Trim()}' has too many fields; use FPS, FPS:drop or FPS:drop:rise");

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value) || value <= 0)
            {
                return ([], [], $"'{parts[0]}' is not a positive FPS value");
            }

            double? drop = null;
            double? rise = null;
            if (parts.Length >= 2 && parts[1].Length > 0)
            {
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    || !double.IsFinite(d) || d <= 0)
                    return ([], [], $"'{parts[1]}' is not a positive bitrate (kbps)");
                drop = d;
            }
            if (parts.Length == 3 && parts[2].Length > 0)
            {
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double r)
                    || !double.IsFinite(r) || r <= 0)
                    return ([], [], $"'{parts[2]}' is not a positive bitrate (kbps)");
                rise = r;
            }
            if (drop is not null && rise is not null && rise >= drop)
            {
                return ([], [], $"rise threshold {rise:G6} must be below the drop threshold {drop:G6}");
            }

            fps.Add(Math.Round(value, 6));
            settings.Add(new FpsRungSetting { Fps = Math.Round(value, 6), DropAboveKbps = drop, RiseBelowKbps = rise });
        }

        return (fps, settings, null);
    }

    /// <summary>
    /// Render a ladder the same way it can be typed back in, e.g.
    /// <c>15,12:128,10:150,5:200</c>. Thresholds are only printed when the user
    /// actually set them.
    /// </summary>
    public static string FormatLadder(
        double baseFps, IEnumerable<double>? levels, IEnumerable<FpsRungSetting>? settings)
    {
        List<FpsRungSetting>? rungs = settings?.ToList();
        return string.Join(",", SelectLevels(baseFps, levels).Select(fps =>
        {
            FpsRungSetting? rung = rungs?.FirstOrDefault(x => Math.Abs(x.Fps - fps) < 1e-6);
            if (rung is null) return fps.ToString("G6");
            string text = rung.DropAboveKbps is { } drop
                ? $"{fps:G6}:{drop:G6}"
                : fps.ToString("G6");
            if (rung.RiseBelowKbps is { } rise) text += $":{rise:G6}";
            return text;
        }));
    }

    /// <summary>
    /// Drop the thresholds of rungs that are no longer on the ladder. Without
    /// this, lowering the base FPS would leave the settings file carrying
    /// thresholds for rates the encoder can never use.
    /// </summary>
    public static List<FpsRungSetting> PruneRungs(
        IEnumerable<FpsRungSetting>? settings, IEnumerable<double> levels)
    {
        List<double> keep = [.. levels];
        return [.. (settings ?? []).Where(s => keep.Any(v => Math.Abs(v - s.Fps) < 1e-6))];
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

    /// <summary>
    /// Containers this encoder may be written to, default first. Most encoders
    /// have exactly one; the ones in
    /// <see cref="Constants.ContainerChoicesByEncoder"/> offer a real choice.
    /// </summary>
    public static IReadOnlyList<(string Ext, string Format)> AllowedContainers(EncoderInfo encoder)
    {
        if (Constants.ContainerChoicesByEncoder.TryGetValue(
                encoder.Name, out (string Ext, string Format)[]? choices) && choices.Length > 0)
        {
            return choices;
        }
        return [(encoder.Extension, encoder.FormatName)];
    }

    /// <summary>
    /// Apply a container request, returning the encoder copy that writes it.
    /// An unknown or empty request keeps the default; a request this encoder
    /// cannot write returns null so the caller can say so instead of producing
    /// a file that no player will open.
    /// </summary>
    public static EncoderInfo? ResolveContainer(EncoderInfo encoder, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return encoder;
        IReadOnlyList<(string Ext, string Format)> allowed = AllowedContainers(encoder);

        string wanted = requested.Trim().ToLowerInvariant();
        foreach ((string ext, string format) in allowed)
        {
            if (format == wanted || "." + format == "." + wanted || ext == wanted.TrimStart('.'))
                return format == encoder.FormatName ? encoder : encoder.WithContainer(ext, format);
        }
        // The muxer name and the extension are both accepted ("matroska"/"mkv").
        if (Constants.FormatExtensions.TryGetValue(wanted, out HashSet<string>? exts))
        {
            foreach ((string ext, string format) in allowed)
            {
                if (exts.Contains("." + ext)) return encoder.WithContainer(ext, format);
            }
        }
        return null;
    }

    public static string? ChooseAudioEncoder(string container, ICollection<string> available)
    {
        if (Constants.NoAudioFormats.Contains(container)) return null;

        // Preferred codecs for this container first, then everything else that
        // muxes into it -- so the automatic choice is unchanged but a build
        // without the preferred codec still gets a working one.
        List<string> compatible = AudioCatalog.Compatible(container, available);
        return compatible.Count > 0 ? compatible[0] : null;
    }

    /// <summary>
    /// v7.8: crop the source down to the output aspect ratio before scaling.
    /// Exactly one dimension is reduced: <c>min(iw, ih*W/H)</c> keeps the
    /// source's own width when it is already the narrower one, and the
    /// companion expression does the same for height -- so a portrait source
    /// crops vertically and a landscape one crops horizontally, without having
    /// to probe the source first.
    /// </summary>
    public static string CropFilterExpr(int outW, int outH, string cropAnchor)
    {
        string cw = $"min(iw\\,ih*{outW}/{outH})";
        string ch = $"min(ih\\,iw*{outH}/{outW})";
        string ow = $"(iw-{cw})";
        string oh = $"(ih-{ch})";

        // Anchoring only matters along the axis that is actually cropped; the
        // other axis is already flush, so its offset expression evaluates to 0.
        string x = CanonicalCropAnchor(cropAnchor) switch { "left" => "0", "right" => ow, _ => $"{ow}/2" };
        string y = CanonicalCropAnchor(cropAnchor) switch { "top" => "0", "bottom" => oh, _ => $"{oh}/2" };
        return $"crop={cw}:{ch}:{x}:{y}";
    }

    /// <summary>
    /// "Keep the whole picture" scaling: shrink until both edges fit, then let
    /// <see cref="PadFilterExpr"/> add the bars. That is how a 16:9 master ends
    /// up inside a 4:3 frame with black top and bottom instead of being cropped
    /// or squashed.
    /// </summary>
    public static string FitScaleFilterExpr(int outW, int outH, string scaleAlgo) =>
        $"scale={outW}:{outH}:force_original_aspect_ratio=decrease:flags={CanonicalScaleAlgo(scaleAlgo)}";

    public static string PadFilterExpr(int outW, int outH, string? padColor) =>
        $"pad={outW}:{outH}:(ow-iw)/2:(oh-ih)/2:{PadColorExpr(padColor)}";

    /// <summary>pad= takes a colour name or 0xRRGGBB; anything else would be
    /// parsed as an option and break the filter graph.</summary>
    private static string PadColorExpr(string? color)
    {
        string value = (color ?? "").Trim();
        return value.Length == 0 ? "black" : value;
    }

    public static string CanonicalFitMode(string? value)
    {
        string key = (value ?? "").Trim().ToLowerInvariant();
        // v7.7's boolean lives on as "crop"/"stretch": an old settings file with
        // "crop 4:3 = yes" is a crop, and "no" is a stretch.
        if (key is "no" or "none" or "off" or "false") return "stretch";
        if (key is "yes" or "true" or "on" or "crop43" or "4:3") return "crop";
        if (key is "pad" or "letterbox" or "pillarbox" or "box") return "fit";
        return Constants.FitModes.Contains(key) ? key : "crop";
    }

    public static string CanonicalCropAnchor(string? value)
    {
        string key = (value ?? "").Trim().ToLowerInvariant();
        return Constants.CropAnchors.Contains(key) ? key : "center";
    }

    // Kept for parity with the Python fallback chain used when no encoder declares
    // any usable raw transport format.
    public static string FallbackTransportFormat() => PreferredTransportFallback[0];
}
