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

    public static List<string> ColorArgs(StreamInfo info)
    {
        var args = new List<string>();
        if (!string.IsNullOrEmpty(info.ColorRange)) args.AddRange(["-color_range", info.ColorRange]);
        if (!string.IsNullOrEmpty(info.ColorSpace) && info.ColorSpace != "unknown")
            args.AddRange(["-colorspace", info.ColorSpace]);
        if (!string.IsNullOrEmpty(info.ColorPrimaries) && info.ColorPrimaries != "unknown")
            args.AddRange(["-color_primaries", info.ColorPrimaries]);
        if (!string.IsNullOrEmpty(info.ColorTransfer) && info.ColorTransfer != "unknown")
            args.AddRange(["-color_trc", info.ColorTransfer]);
        return args;
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

    public static string BuildFilters(
        int outW,
        int outH,
        double baseFps,
        bool crop43,
        string scaleAlgo,
        string transportFmt,
        string deinterlace,
        string deintMode,
        string deintParity,
        bool deintMarkedOnly)
    {
        var filters = new List<string>();
        if (crop43) filters.Add(@"crop=min(iw\,ih*4/3):ih:(iw-min(iw\,ih*4/3))/2:0");

        string deintExpr = DeinterlaceFilterExpr(deinterlace, deintMode, deintParity, deintMarkedOnly);
        if (deintExpr.Length > 0) filters.Add(deintExpr);

        // FPS is applied after deinterlacing: in send_field/bob mode putting fps
        // first would discard field-derived pictures before the cadence controller.
        filters.Add(string.Create(CultureInfo.InvariantCulture, $"fps={baseFps:G6}:round=down"));
        filters.Add($"scale={outW}:{outH}:flags={Core.EncoderMath.CanonicalScaleAlgo(scaleAlgo)}");
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
