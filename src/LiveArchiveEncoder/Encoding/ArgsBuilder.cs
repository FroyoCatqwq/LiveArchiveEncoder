using System.Globalization;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.Encoding;

/// <summary>FFmpeg argument construction, ported from the Python builders.</summary>
public static class ArgsBuilder
{
    /// <summary>
    /// The quality knob of the live pass, in whichever scale the encoder reads.
    ///
    /// <para>v7.9.7: OpenH264 has no <c>-crf</c> and no <c>-q:v</c>; handing it
    /// either leaves it in its default buffer mode, which overshoots the target
    /// by 3x (measured: 700 kbps asked, 2.1 Mbps delivered). It wants a bitrate
    /// and a rate-control mode, so the preset's ceiling becomes exactly
    /// that.</para>
    ///
    /// <para>v7.9.8.8: the mode is a choice now instead of always
    /// <c>bitrate</c>, because OpenH264's own <c>quality</c> mode is the
    /// closest thing it has to a quality knob -- it spends about 20% over
    /// <c>-b:v</c> to keep the picture (<paramref name="rcMode"/>, see
    /// <see cref="OpenH264RateControl"/>). <paramref name="allowSkipFrames"/>
    /// is what makes the bitrate true rather than approximate: the wrapper
    /// passes it to <c>bEnableFrameSkip</c>, and without it OpenH264 says so
    /// itself -- "bitrate can't be controlled ... without enabling skip
    /// frame". Measured at 300 kbps: 354 kbps off, 302 kbps on.</para>
    ///
    /// <para>v7.9.8.2: libx264 used to be the only CRF encoder here, and a
    /// preset's MPEG quantiser went straight into <c>-crf</c> -- the 2007 row's
    /// <c>q:v 9</c> became <c>crf 9</c>, 935 kbps into a 320 kbps stream, and the
    /// frame-rate ladder dropped to its last rung before the first second was
    /// out. Two things changed: the number now arrives already converted to the
    /// encoder's scale (<see cref="EncoderMath.ConvertQuality"/>), and a CRF
    /// encoder is additionally pinned to the ceiling with VBV, so a wrong number
    /// costs picture quality instead of collapsing the ladder. Measured with the
    /// cap at the ceiling: <c>crf 9</c> at 320 kbps went from 935 to 330 kbps
    /// and x264 emitted no "buffer underflow" noise (the MPEG-family encoders
    /// do -- a fixed quantiser and a tight VBV are contradictory requests, which
    /// is why this cap is CRF-only).</para>
    /// </summary>
    public static List<string> VideoQualityArgs(
        string name, int quality, double? limitKbps = null,
        string? rcMode = null, bool allowSkipFrames = false, bool supportsRcMode = true,
        bool preferBitrate = false)
    {
        double ceiling = limitKbps is { } kbps && kbps > 0 ? kbps : FallbackCeilingKbps;

        switch (EncoderMath.ScaleOf(name))
        {
            case EncoderMath.QualityScale.Bitrate:
                // -maxrate is deliberately absent: the wrapper computes
                // iMaxBitrate = FFMAX(rc_max_rate, bit_rate), so with no maxrate
                // the two are the same number and the flag would only restate
                // -b:v. (The comment here used to claim OpenH264 "reads
                // neither", which was wrong -- it reads maxrate, it just cannot
                // use a second ceiling that equals the first.)
                var rate = new List<string>();
                if (supportsRcMode)
                    rate.AddRange(["-rc_mode", OpenH264RateControl.Normalize(rcMode)]);
                rate.AddRange(["-b:v", $"{ceiling.ToString("G6", CultureInfo.InvariantCulture)}k"]);
                if (allowSkipFrames) rate.AddRange(["-allow_skip_frames", "1"]);
                return rate;

            case EncoderMath.QualityScale.Crf:
                // The buffer is two seconds of the ceiling: long enough that a
                // burst is not starved, short enough that a whole scene cannot
                // hide in it.
                return
                [
                    "-crf", quality.ToString(CultureInfo.InvariantCulture),
                    "-maxrate", $"{ceiling.ToString("G6", CultureInfo.InvariantCulture)}k",
                    "-bufsize", $"{(ceiling * 2).ToString("G6", CultureInfo.InvariantCulture)}k",
                ];

            default:
                // v7.9.9.10: -q:v WINS over -b:v inside FFmpeg -- the encoder
                // silently ignores the target bitrate. A caller that pinned one
                // (a disc target, or --extra-encoder-args "-b:v 3000k") gets
                // the bitrate form, which is what its preset actually asked
                // for. This is why --era dvd used to encode at 2.1 Mbps while
                // its preset said 6000.
                return preferBitrate
                    ? ["-b:v", $"{ceiling.ToString("G6", CultureInfo.InvariantCulture)}k"]
                    : ["-q:v", quality.ToString(CultureInfo.InvariantCulture)];
        }
    }

    /// <summary>
    /// What OpenH264 is told when a caller forgot the ceiling. It is the same
    /// 200 kbps the settings file starts at, and it exists only so that a
    /// bitrate-only encoder is never left on its own default -- which is
    /// 1.1 Mbps at 352x264, i.e. every frame-rate rung at once.
    /// </summary>
    private const double FallbackCeilingKbps = 200.0;

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
    /// v7.9.8.11: FFmpeg's MJPEG encoder refuses a limited-range picture at the
    /// default strictness. <c>libavcodec/mjpegenc_common.c:569</c>
    /// (<c>ff_mjpeg_encode_check_pix_fmt</c>) returns EINVAL when
    /// <c>color_range != AVCOL_RANGE_JPEG</c> and either the pix_fmt is a
    /// limited-range YUV or <c>color_range == AVCOL_RANGE_MPEG</c> -- unless
    /// <c>-strict unofficial</c> is used. This tool deliberately never passes
    /// <c>-strict</c>, so the picture itself has to be full range.
    ///
    /// <para><c>mjpegenc.c:666</c> (<c>mjpeg_get_supported_config</c>) confirms
    /// why: at the default strictness the encoder advertises only
    /// <c>AVCOL_RANGE_JPEG</c>; <c>AVCOL_RANGE_MPEG</c> is exposed solely when
    /// strictness is lowered. <c>mjpegenc.c:699</c> lists the six declared
    /// formats -- three <c>yuvj*</c> full-range ones and three limited-range
    /// ones -- so declaring support is not the same as accepting them.</para>
    /// </summary>
    public static bool NeedsJpegRange(string? encoder) =>
        string.Equals(encoder, "mjpeg", StringComparison.OrdinalIgnoreCase);

    /// <summary>The full-range counterpart of a raw transport format.</summary>
    public static string JpegRangePixFmt(string transportFmt) => transportFmt switch
    {
        "yuv422p" or "yuvj422p" => "yuvj422p",
        "yuv444p" or "yuvj444p" => "yuvj444p",
        _ => "yuvj420p",
    };

    /// <summary>
    /// Pixel-format flags on the encoder side. An ordinary encoder only gets
    /// one when the user asked for it; MJPEG always does, because the transport
    /// format follows the source and a limited-range source fails the moment
    /// FFmpeg opens the encoder. The second pass has always done this
    /// (<c>RecompressCodecs</c>: <c>-pix_fmt yuvj420p</c>); the live pass did not.
    /// </summary>
    public static List<string> EncoderPixFmtArgs(
        string encoder, string transportFmt, bool explicitRequest)
    {
        if (NeedsJpegRange(encoder))
            return ["-pix_fmt", JpegRangePixFmt(transportFmt)];
        return explicitRequest ? ["-pix_fmt", transportFmt] : [];
    }

    /// <summary>
    /// v7.9.8.11.1: the audio track is read from the source a second time while
    /// the video arrives over a rawvideo pipe whose timestamps start at zero by
    /// construction. Normalise the audio's first PTS so a container offset, an
    /// encoder delay or an imprecise input seek cannot leave the two streams
    /// shifted. Applied to the output audio stream, and mirrored by
    /// <see cref="Preflight"/> so the trial still proves the real command.
    /// </summary>
    public static List<string> AudioAlignArgs() => ["-af", "asetpts=PTS-STARTPTS"];

    /// <summary>
    /// Colour metadata for the output stream. These are tags, not conversions:
    /// an explicit value wins over what the source declared, but the samples
    /// themselves are left alone. A MJPEG output is pinned to <c>pc</c> by
    /// <see cref="NeedsJpegRange"/>.
    /// </summary>
    public static List<string> ColorArgs(StreamInfo info, ColorOverride? over = null, string? encoder = null)
    {
        var args = new List<string>();
        string? range = over?.Range ?? info.ColorRange;
        // Even with a full-range pix_fmt, an inherited "tv" tag is enough to
        // fail ff_mjpeg_encode_check_pix_fmt: color_range == AVCOL_RANGE_MPEG
        // is a condition of its own, not merely a consequence of the pix_fmt.
        if (NeedsJpegRange(encoder)) range = "pc";
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

    /// <summary>
    /// The deinterlacer's filter expression, or "" for <c>off</c>.
    ///
    /// <para>v7.9.8.3: <c>nnedi</c> is not shaped like the others. It has no
    /// <c>mode</c> or <c>parity</c> option -- it takes <c>weights</c> (the
    /// NNEDI3 neural network, a file, so this method needs its path) and
    /// <c>field</c>, which is the nearest thing it has to a parity switch:
    /// <c>af</c> trusts the bitstream's frame flags, <c>tf</c>/<c>bf</c> force a
    /// polarity. <c>deint</c> keeps the same meaning as everywhere else.</para>
    /// </summary>
    public static string DeinterlaceFilterExpr(
        string filterName, string mode, string parity, bool markedOnly, string? nnediWeights = null)
    {
        if (filterName == "off") return "";
        if (!Constants.DeinterlaceFilters.Contains(filterName))
            throw new ArgumentException($"unsupported deinterlace filter: {filterName}");

        string deintValue = markedOnly ? "interlaced" : "all";

        if (filterName == "nnedi")
        {
            if (!nnediWeights.HasText)
            {
                throw new ArgumentException(
                    "the nnedi filter needs its NNEDI3 weights file; run "
                    + "`LiveArchiveEncoder --nnedi-update` (or pass --nnedi-weights PATH)");
            }
            string fieldValue = parity switch { "tff" => "tf", "bff" => "bf", _ => "af" };
            return $"nnedi=weights={Core.FilterArgs.EscapePath(nnediWeights)}:field={fieldValue}:deint={deintValue}";
        }

        string parityValue = parity is "auto" or "tff" or "bff" ? parity : "auto";
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
        bool deintMarkedOnly,
        string? nnediWeights = null,
        string? tearing = null)
    {
        string mode = Core.EncoderMath.CanonicalFitMode(fitMode);
        var filters = new List<string>();
        if (mode == "crop") filters.Add(Core.EncoderMath.CropFilterExpr(outW, outH, cropAnchor));

        string deintExpr = DeinterlaceFilterExpr(
            deinterlace, deintMode, deintParity, deintMarkedOnly, nnediWeights);
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

        // v7.9.9.10: the optional tear, applied to the finished frame so the
        // split lands on the delivered picture rather than on an intermediate
        // one. Appended last: tblend needs two frames of the same format, and
        // everything before this point has already produced exactly that.
        string tear = TearingChain(tearing);
        if (tear.Length > 0) filters.Add(tear);
        return string.Join(',', filters);
    }

    /// <summary>
    /// The filter that reproduces a dropped vertical sync: <c>tblend</c> mixes
    /// the previous frame into this one, and the expression decides per row
    /// which of the two survives, so the top of the picture is one frame behind
    /// the bottom.
    ///
    /// <para>An empty string means "no tear", which is the default: the tool is
    /// a faithful encoder unless the user asks for the artefact on purpose.
    /// Note that <c>tblend</c> holds one frame back, so a torn run is short by
    /// one frame at the head -- expected for a filter that needs a predecessor.
    /// </para>
    /// </summary>
    /// <summary>
    /// True when the free-form extras already pin a bitrate, in which case
    /// <c>-q:v</c> must not be emitted: FFmpeg prefers the quantiser and would
    /// silently ignore the bitrate the preset asked for.
    /// </summary>
    public static bool PinsBitrate(string? extras) =>
        extras is { Length: > 0 }
        && (extras.Contains("-b:v", StringComparison.Ordinal)
            || extras.Contains("-maxrate", StringComparison.Ordinal));

    public static string TearingChain(string? mode) => mode switch
    {
        // A fixed split half way down.
        "horizontal" => "tblend=all_expr='if(lt(Y,H*0.5),B,A)'",
        // The split walks down the frame as N grows, so consecutive frames tear
        // at different heights -- closer to a signal fault than a clean splice.
        // The comma is escaped because FFmpeg's expression parser splits there.
        "random" => "tblend=all_expr='if(lt(Y,H*mod(N*37\\,100)/100),B,A)'",
        _ => "",
    };

    /// <summary>
    /// Python's <c>f"{fps:g}"</c> uses 6 significant digits; C#'s bare "G" is a
    /// round-trip format and would emit far more digits, changing the FFmpeg
    /// command line. "G6" reproduces the Python output exactly.
    /// </summary>
    public static string FpsArg(double fps) => fps.ToString("G6", CultureInfo.InvariantCulture);

    public static string FloatArg(double value) => value.ToString("F6", CultureInfo.InvariantCulture);
}
