using System.Globalization;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.Core;

/// <summary>Selection/derivation helpers shared by CUI, CLI and the encoder.</summary>
public static class EncoderMath
{
    public static int DefaultQuality(string name) =>
        Constants.DefaultQuality.TryGetValue(name, out int value) ? value : 10;

    /// <summary>
    /// Which scale a quality number is written in. v7.9.8.2.
    ///
    /// <para>There were two of these before and they were treated as one: an
    /// MPEG-family <c>-q:v 9</c> and x264's <c>-crf 9</c> are not the same
    /// picture, they are not even the same order of magnitude. Measured on
    /// <c>tests/media/test_src.mp4</c> at 352x264@15 (2026-10-27): mpeg4
    /// <c>-q:v 9</c> is 375 kbps, libx264 <c>-crf 9</c> is 935 kbps -- 2.5x the
    /// ceiling of the 2007 preset that asked for it, which drove the frame-rate
    /// ladder to its last rung within a second.</para>
    ///
    /// <para>Every place that prints, stores or forwards a quality number has to
    /// know which of these it is holding; <c>ArgsBuilder.VideoQualityArgs</c> is
    /// the only place that turns it into flags.</para>
    /// </summary>
    public enum QualityScale
    {
        /// <summary>MPEG-style quantiser, 2..31, lower is better. mpeg4, flv, wmv2, rv10/20, h263, mjpeg.</summary>
        Qv = 0,

        /// <summary>x264/x265/VP9/AV1 constant quality, 0..51, lower is better.</summary>
        Crf = 1,

        /// <summary>No quality knob at all: the bitrate is the only dial (libopenh264).</summary>
        Bitrate = 2,
    }

    /// <summary>The scale <paramref name="name"/>'s quality number lives in.</summary>
    public static QualityScale ScaleOf(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        // OpenH264 has no -crf and no usable -q:v: given either it falls back to
        // its default buffer mode and overshoots by 3x (measured 2026-10-26:
        // 700 kbps asked, 2.1 Mbps delivered). Bitrate is its only dial.
        "libopenh264" => QualityScale.Bitrate,
        "libx264" or "libx264rgb" or "libx265" or "libvpx-vp9" or "libvpx"
            or "libaom-av1" or "libsvtav1" => QualityScale.Crf,
        _ => QualityScale.Qv,
    };

    /// <summary>
    /// The same picture expressed in another encoder's scale. Used when the
    /// encoder changes under a quality number that was written for the previous
    /// one -- a 2007 preset's <c>q:v</c> handed to libx264, or a user flipping
    /// the encoder in the menu.
    ///
    /// <para>Calibrated by equal bitrate rather than by taste: for every
    /// geometry in the built-in table, the mpeg4 quantiser and the x264 CRF that
    /// produced the same rate were paired up (2026-10-27, 13 pairs between
    /// 45 and 2500 kbps); the fit is crf = 7.5 + 1.17 * q:v, i.e. q:v 9 is
    /// crf 18 and q:v 12 is crf 22. Converting to or from a bitrate encoder is
    /// not a conversion -- its dial is elsewhere -- so the number passes
    /// through unchanged and the caller uses the ceiling.</para>
    /// </summary>
    public static int ConvertQuality(string? from, string? to, int value) =>
        ConvertQuality(ScaleOf(from), ScaleOf(to), value);

    /// <summary>Scale-to-scale form of <see cref="ConvertQuality(string?, string?, int)"/>.</summary>
    public static int ConvertQuality(QualityScale source, QualityScale target, int value)
    {
        if (source == target) return value;

        // Bitrate has no number to translate; the ceiling is what it needs.
        if (source == QualityScale.Bitrate || target == QualityScale.Bitrate) return value;

        return source == QualityScale.Qv
            ? Math.Clamp((int)Math.Round(7.5 + 1.17 * value), 0, 51)
            : Math.Clamp((int)Math.Round((value - 7.5) / 1.17), 2, 31);
    }

    /// <summary>
    /// One number, expressed in both of the scales a row has to keep: the
    /// caller asks in whichever one the user typed, and both are stored so a
    /// later encoder switch needs no second question. A bitrate-only encoder is
    /// treated as the CRF one it replaces -- its number is never used anyway.
    /// </summary>
    public static (int Qv, int Crf) QualityPair(string? encoderName, int value)
    {
        QualityScale scale = ScaleOf(encoderName);
        if (scale == QualityScale.Bitrate) scale = QualityScale.Crf;
        return scale == QualityScale.Crf
            ? (ConvertQuality(QualityScale.Crf, QualityScale.Qv, value), value)
            : (value, ConvertQuality(QualityScale.Qv, QualityScale.Crf, value));
    }

    /// <summary>Half-open range a quality number may take in this scale, for prompts and validation.</summary>
    public static (int Min, int Max) QualityRange(QualityScale scale) => scale switch
    {
        QualityScale.Crf => (0, 51),
        QualityScale.Bitrate => (0, 0),
        _ => (2, 31),
    };

    public static (double Fps, bool Exact) ChooseBaseFps(double requested, EncoderCaps caps)
    {
        // v7.9.5: RealMedia and RoQ store the rate as an integer in the file
        // header, so a fractional request is silently retimed on write --
        // measured (2026-10-23): rv20 at 23.976 probes back as 23/1, 29.97 as
        // 29/1, 59.94 as 59/1, and roqvideo refuses those three outright.
        // Rounding here means the run encodes at the rate the file will hold.
        if (UsesWholeFps(caps.Name))
        {
            double whole = Math.Clamp(Math.Round(requested), 1.0, 120.0);
            return (whole, Math.Abs(whole - requested) < 0.01);
        }

        List<double> rates = UsableRates(caps);
        if (rates.Count == 0) return (requested, true);

        double nearest = rates[0];
        foreach (double value in rates)
        {
            if (Math.Abs(value - requested) < Math.Abs(nearest - requested)) nearest = value;
        }
        return (nearest, Math.Abs(nearest - requested) < 0.01);
    }

    /// <summary>
    /// True for encoders whose container cannot express a fractional frame rate.
    ///
    /// v7.9.5, measured with <c>tools/capability_sweep.py</c>:
    /// <list type="bullet">
    /// <item>rv10/rv20 -- the RealMedia header has an integer fps field; 23.976
    /// is written as 23.000, 29.97 as 29.000, 59.94 as 59.000;</item>
    /// <item>roqvideo -- the RoQ header is worse: a fractional rate is refused
    /// ("Error while opening encoder"), 23.976 / 29.97 / 59.94 all fail.</item>
    /// </list>
    /// Their <c>-h encoder</c> output declares no rate list at all, which is why
    /// nothing snapped these before: an empty declared list means "no
    /// constraint" to the generic path.
    /// </summary>
    public static bool UsesWholeFps(string name) => name is "rv10" or "rv20" or "roqvideo";

    /// <summary>
    /// The frame rates an encoder <b>really</b> accepts, as opposed to the ones
    /// it declares in <c>ffmpeg -h encoder=NAME</c>.
    ///
    /// v7.9.4: mpeg1video declares 5/1, 10/1, 12/1 and 15/1 in its
    /// "Supported framerates" line, and then refuses every one of them when the
    /// encoder actually opens -- measured against this build (2026-10-22):
    ///
    ///   mpeg1video 15 -> "MPEG-1/2 does not support 15/1 fps" (also 5, 10, 12,
    ///   and 13/14/16/18, which are nearest to one of those unofficial rates)
    ///   mpeg1video 20/22/45 -> encodes, retimed onto 23.976/24/50
    ///   mpeg2video 5/10/12/15/20/75 -> all encode (frame rate extension)
    ///
    /// The extra rates are the "libmpeg3 unofficial economy rates": libavcodec
    /// only allows them below the default strictness, so the honest list for
    /// MPEG-1 is the eight real frame_rate_code values. Snapping here means a
    /// requested 15 becomes 23.976 instead of failing a whole sharded run.
    ///
    /// AMV has no declared list and its fps must divide 22050, so its table is
    /// mandatory rather than a preference.
    /// </summary>
    public static List<double> UsableRates(EncoderCaps caps) => caps.Name switch
    {
        "amv" => [.. Constants.AmvFps],
        "mpeg1video" => [.. Constants.Mpeg1RealFps],
        // The declared list (when a build prints one) can only be trusted up to
        // the whole numbers, which is what the header can hold.
        "rv10" or "rv20" or "roqvideo" =>
            [.. caps.SupportedRates.Where(r => Math.Abs(r - Math.Round(r)) < 1e-6)],
        _ => caps.SupportedRates,
    };

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
        if (!requested.HasText) return encoder;
        IReadOnlyList<(string Ext, string Format)> allowed = AllowedContainers(encoder);

        string wanted = requested.Trim().ToLowerInvariant();
        foreach ((string ext, string format) in allowed)
        {
            if (format == wanted || "." + format == "." + wanted || ext == wanted.TrimStart('.'))
                return format == encoder.FormatName ? encoder : encoder.WithContainer(ext, format);
        }
        // The muxer name and the extension are both accepted ("matroska"/"mkv").
        if (Constants.FormatExtensions.TryGetValue(wanted, out IReadOnlySet<string>? exts))
        {
            foreach ((string ext, string format) in allowed)
            {
                if (exts.Contains("." + ext)) return encoder.WithContainer(ext, format);
            }
        }
        return null;
    }

    /// <summary>
    /// Take the container from the output file name when nobody named one.
    ///
    /// v7.9.4: <c>mpeg1video out.avi</c> was rejected with "Output extension
    /// '.avi' does not match selected muxer 'mpeg'" even though AVI has been a
    /// legal target for mpeg1video/mpeg2video since v7.8 -- the extension check
    /// only ever looked at the encoder's <em>default</em> container. The typed
    /// extension is the request: if the encoder can write it, write it. An
    /// extension no container of this encoder carries (say mkv for rv20) is
    /// left alone, so the caller still reports the mismatch instead of guessing.
    /// </summary>
    public static EncoderInfo ResolveByExtension(EncoderInfo encoder, string? path)
    {
        if (!path.HasText) return encoder;

        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0) return encoder;

        // Already the encoder's own container: nothing to do, and returning the
        // same instance keeps callers able to test for "unchanged" cheaply.
        if (Constants.FormatExtensions.TryGetValue(encoder.FormatName,
                out IReadOnlySet<string>? current) && current.Contains("." + extension))
        {
            return encoder;
        }

        foreach ((string ext, string format) in AllowedContainers(encoder))
        {
            if (format == encoder.FormatName) continue;
            if (!Constants.FormatExtensions.TryGetValue(format, out IReadOnlySet<string>? exts)) continue;
            if (exts.Contains("." + extension)) return encoder.WithContainer(ext, format);
        }
        return encoder;
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
}
