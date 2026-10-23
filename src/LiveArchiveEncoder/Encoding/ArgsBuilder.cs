using System.Globalization;
using LiveArchiveEncoder.Configuration;

namespace LiveArchiveEncoder.Encoding;

/// <summary>FFmpeg argument construction, ported from the Python builders.</summary>
public static class ArgsBuilder
{
    public static List<string> VideoQualityArgs(string name, int quality) =>
        name == "libx264" ? ["-crf", quality.ToString(CultureInfo.InvariantCulture)]
                          : ["-q:v", quality.ToString(CultureInfo.InvariantCulture)];

    public static List<string> X264Args(string? preset, string? tune, string? profile, string? level)
    {
        var args = new List<string>();
        if (!string.IsNullOrEmpty(preset)) args.AddRange(["-preset", preset]);
        if (!string.IsNullOrEmpty(tune)) args.AddRange(["-tune", tune]);
        if (!string.IsNullOrEmpty(profile)) args.AddRange(["-profile:v", profile]);
        if (!string.IsNullOrEmpty(level)) args.AddRange(["-level:v", level]);
        return args;
    }

    /// <summary>
    /// Colour metadata for the output stream.
    ///
    /// v7.9.6: an explicit value wins over what the source declared, so a DVD
    /// rip that says "unknown" can be told it is bt470bg + tv range without
    /// re-encoding the picture twice. Only the field the user named changes;
    /// the rest still follow the source.
    ///
    /// These are tags, not conversions: FFmpeg writes the numbers into the
    /// stream header and leaves the samples alone. A wrong tag therefore looks
    /// worse than no tag -- which is why the CLI only accepts values from a
    /// known list rather than passing arbitrary text through.
    /// </summary>
    public static List<string> ColorArgs(StreamInfo info, ColorOverride? over = null)
    {
        var args = new List<string>();
        string? range = over?.Range ?? info.ColorRange;
        string? space = over?.Space ?? info.ColorSpace;
        string? primaries = over?.Primaries ?? info.ColorPrimaries;
        string? transfer = over?.Transfer ?? info.ColorTransfer;

        if (!string.IsNullOrEmpty(range)) args.AddRange(["-color_range", range]);
        if (!string.IsNullOrEmpty(space) && space != "unknown")
            args.AddRange(["-colorspace", space]);
        if (!string.IsNullOrEmpty(primaries) && primaries != "unknown")
            args.AddRange(["-color_primaries", primaries]);
        if (!string.IsNullOrEmpty(transfer) && transfer != "unknown")
            args.AddRange(["-color_trc", transfer]);
        return args;
    }

    /// <summary>
    /// The colour tags, expressed the only way libx264 actually writes them.
    ///
    /// <para>Measured 2026-10-24 against this build: <c>-c:v libx264
    /// -color_primaries bt2020 -color_trc smpte2084</c> produces a file whose
    /// primaries and transfer are both "unknown", while
    /// <c>-x264-params colorprim=bt2020:transfer=smpte2084</c> writes them. The
    /// AVCodecContext fields never reach x264's VUI in this wrapper, so the
    /// standard flags alone would make the two options look implemented while
    /// changing nothing -- the same "declared vs real" trap as the capability
    /// table.</para>
    ///
    /// <para><c>-color_range</c> and <c>-colorspace</c> are left to the normal
    /// flags: they do land (range in the MP4 colr atom and the Matroska Colour
    /// element, matrix in both), and x264's own "range" parameter is not worth
    /// the risk of an unknown-parameter abort for something already working.</para>
    /// </summary>
    public static List<string> X264ColorArgs(ColorOverride? over)
    {
        if (over is null) return [];
        var parts = new List<string>();
        if (over.Primaries is { } primaries && X264Primaries.TryGetValue(primaries, out string? prim))
            parts.Add($"colorprim={prim}");
        if (over.Transfer is { } transfer && X264Transfers.TryGetValue(transfer, out string? trc))
            parts.Add($"transfer={trc}");
        if (over.Space is { } space && X264Spaces.TryGetValue(space, out string? matrix))
            parts.Add($"colmatrix={matrix}");
        return parts.Count > 0 ? ["-x264-params", string.Join(':', parts)] : [];
    }

    // FFmpeg's names and x264's names differ for exactly four of the values this
    // tool offers; the rest are spelled identically. Values x264 does not know
    // are dropped rather than passed on, because an unknown x264 parameter
    // aborts the encode instead of warning.
    private static readonly Dictionary<string, string> X264Primaries = new(StringComparer.Ordinal)
    {
        ["bt709"] = "bt709", ["bt470m"] = "bt470m", ["bt470bg"] = "bt470bg",
        ["smpte170m"] = "smpte170m", ["smpte240m"] = "smpte240m", ["film"] = "film",
        ["bt2020"] = "bt2020", ["smpte428"] = "smpte428", ["smpte431"] = "smpte431",
        ["smpte432"] = "smpte432", ["jedec-p22"] = "jedec-p22", ["ebu3213"] = "ebu3213",
    };

    private static readonly Dictionary<string, string> X264Transfers = new(StringComparer.Ordinal)
    {
        ["bt709"] = "bt709", ["gamma22"] = "bt470m", ["gamma28"] = "bt470bg",
        ["smpte170m"] = "smpte170m", ["smpte240m"] = "smpte240m", ["linear"] = "linear",
        ["log"] = "log100", ["log_sqrt"] = "log316", ["iec61966-2-4"] = "iec61966-2-4",
        ["bt1361"] = "bt1361e", ["iec61966-2-1"] = "iec61966-2-1",
        ["bt2020-10"] = "bt2020-10", ["bt2020-12"] = "bt2020-12",
        ["smpte2084"] = "smpte2084", ["smpte428"] = "smpte428",
        ["arib-std-b67"] = "arib-std-b67",
    };

    private static readonly Dictionary<string, string> X264Spaces = new(StringComparer.Ordinal)
    {
        ["bt709"] = "bt709", ["fcc"] = "fcc", ["bt470bg"] = "bt470bg",
        ["smpte170m"] = "smpte170m", ["smpte240m"] = "smpte240m", ["ycgco"] = "ycgco",
        ["bt2020nc"] = "bt2020nc", ["bt2020c"] = "bt2020c", ["smpte2085"] = "smpte2085",
        ["chroma-derived-nc"] = "chroma-derived-nc", ["chroma-derived-c"] = "chroma-derived-c",
        ["ictcp"] = "ictcp",
    };

    /// <summary>
    /// The dither flag for a conversion into a low-colour format, or nothing.
    ///
    /// It is emitted only when the user asked for something other than "auto"
    /// AND the target format can actually lose colour that way: passing
    /// <c>-sws_dither</c> on a yuv420p run would be a no-op with a log line that
    /// suggests otherwise.
    /// </summary>
    public static List<string> DitherArgs(string dither, string transportFmt)
    {
        string mode = (dither ?? "").Trim().ToLowerInvariant();
        if (mode.Length == 0 || mode == "auto") return [];
        if (!Constants.DitherModes.Contains(mode) || !Core.PixelFormatUtil.WantsDither(transportFmt))
            return [];
        return ["-sws_dither", mode];
    }

    public static string DeinterlaceFilterExpr(
        string filterName, string mode, string parity, bool markedOnly)
    {
        if (filterName == "off") return "";
        if (!Constants.DeinterlaceFilters.Contains(filterName))
            throw new ArgumentException($"unsupported deinterlace filter: {filterName}");

        string parityValue = parity is "auto" or "tff" or "bff" ? parity : "auto";
        string deintValue = markedOnly ? "interlaced" : "all";
        string modeValue = filterName == "w3fdif"
            ? mode == "send_field" ? "field" : "frame"
            : mode is "send_frame" or "send_field" ? mode : "send_frame";

        return $"{filterName}=mode={modeValue}:parity={parityValue}:deint={deintValue}";
    }

    /// <summary>
    /// Build the filter chain.
    ///
    /// <paramref name="fitMode"/> decides how the source fills the output:
    /// crop    -- crop to the output aspect ratio, then scale (fills the frame,
    ///            discards the overflowing edge)
    /// fit     -- scale down until both edges fit, then pad (keeps the whole
    ///            picture: a 16:9 master in a 4:3 frame gains top/bottom bars)
    /// stretch -- scale straight to the output size (fills the frame, distorts)
    /// </summary>
    public static string BuildFilters(
        int outW,
        int outH,
        double baseFps,
        string fitMode,
        string cropAnchor,
        string padColor,
        string scaleAlgo,
        string transportFmt,
        string deinterlace,
        string deintMode,
        string deintParity,
        bool deintMarkedOnly)
    {
        string mode = Core.EncoderMath.CanonicalFitMode(fitMode);
        var filters = new List<string>();
        if (mode == "crop") filters.Add(Core.EncoderMath.CropFilterExpr(outW, outH, cropAnchor));

        string deintExpr = DeinterlaceFilterExpr(deinterlace, deintMode, deintParity, deintMarkedOnly);
        if (deintExpr.Length > 0) filters.Add(deintExpr);

        // FPS is applied after deinterlacing: in send_field/bob mode putting fps
        // first would discard field-derived pictures before the cadence controller.
        filters.Add(string.Create(CultureInfo.InvariantCulture, $"fps={baseFps:G6}:round=down"));

        if (mode == "fit")
        {
            filters.Add(Core.EncoderMath.FitScaleFilterExpr(outW, outH, scaleAlgo));
            filters.Add(Core.EncoderMath.PadFilterExpr(outW, outH, padColor));
        }
        else
        {
            filters.Add($"scale={outW}:{outH}:flags={Core.EncoderMath.CanonicalScaleAlgo(scaleAlgo)}");
        }

        filters.Add($"format={transportFmt}");
        return string.Join(',', filters);
    }

    /// <summary>
    /// Python's <c>f"{fps:g}"</c> uses 6 significant digits; C#'s bare "G" is a
    /// round-trip format and would emit far more digits, changing the FFmpeg
    /// command line. "G6" reproduces the Python output exactly.
    /// </summary>
    public static string FpsArg(double fps) => fps.ToString("G6", CultureInfo.InvariantCulture);

    public static string FloatArg(double value) => value.ToString("F6", CultureInfo.InvariantCulture);
}
