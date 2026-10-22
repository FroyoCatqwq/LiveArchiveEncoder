// Retired members, kept for reference. NOT COMPILED: this file lives outside
// every project on purpose (see archive/README.md).
//
// How each one was found: v7.9.3.1 cross-referenced every declaration in src/
// against src/, tests/ and the e2e suite; what is below had no call site at all
// (the two Probe helpers had exactly one -- each other's sibling API).
//
// Restoring one is a copy-paste into the file it came from.

using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Encoding;

namespace LiveArchiveEncoder.Archive;

// ---------------------------------------------------------------------------
// FFmpeg/Probe.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Retired in v7.9.3.1.
///
/// Audio presence as its own ffprobe run. Superseded by
/// <c>Probe.Source(...).HasAudio</c>, which reads streams and format in one
/// invocation -- the whole point of that method (see its comment: three
/// separate probes cost ~0.6 s of process startup on Windows).
///
/// Kept because the CSV spelling of the check is the shortest thing that works
/// if a future build ever needs just this one fact.
/// </summary>
internal static class RetiredProbeAudioPresence
{
    public static bool HasAudio(string ffprobe, FileInfo src)
    {
        CaptureResult p = ProcessRunner.Run([
            ffprobe, "-v", "error", "-select_streams", "a:0",
            "-show_entries", "stream=index", "-of", "csv=p=0", src.FullName,
        ]);
        return p.ExitCode == 0 && p.Stdout.Trim().Length > 0;
    }
}

/// <summary>
/// Retired in v7.9.3.1.
///
/// Video metadata for the <em>first</em> video stream. Superseded twice over:
/// <c>Probe.Source</c> carries the same fields inside its single run, and
/// <c>StreamSelection.Resolve</c> re-points them at the stream the user picked
/// (this version could only ever describe stream 0, which is what made
/// <c>--video-stream</c> impossible before v7.9).
/// </summary>
internal static class RetiredProbeVideoStream
{
    public static StreamInfo Stream(string ffprobe, FileInfo src)
    {
        CaptureResult p = ProcessRunner.Run([
            ffprobe, "-v", "error", "-select_streams", "v:0",
            "-show_entries",
            "stream=width,height,pix_fmt,color_range,color_space,color_primaries,color_transfer",
            "-of", "json", src.FullName,
        ]);
        if (p.ExitCode != 0)
            throw new InvalidOperationException(p.Stderr.Trim() is { Length: > 0 } err ? err : "cannot probe video stream");

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(p.Stdout);
        System.Text.Json.JsonElement stream = doc.RootElement.GetProperty("streams")[0];

        static string? Get(System.Text.Json.JsonElement e, string name)
            => e.TryGetProperty(name, out System.Text.Json.JsonElement v) ? v.GetString() : null;

        return new StreamInfo
        {
            Width = int.Parse(stream.GetProperty("width").ToString(),
                System.Globalization.CultureInfo.InvariantCulture),
            Height = int.Parse(stream.GetProperty("height").ToString(),
                System.Globalization.CultureInfo.InvariantCulture),
            PixFmt = Get(stream, "pix_fmt") ?? "yuv420p",
            ColorRange = Get(stream, "color_range"),
            ColorSpace = Get(stream, "color_space"),
            ColorPrimaries = Get(stream, "color_primaries"),
            ColorTransfer = Get(stream, "color_transfer"),
        };
    }
}

// ---------------------------------------------------------------------------
// Encoding/SubtitleTrack.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Retired in v7.9.3.1.
///
/// <c>SubtitleMode</c> as the string the settings file spells. Nothing needed
/// it: the config already stores the string, and the menu prints
/// <c>SubtitleConfig.Mode</c> directly. The reverse direction
/// (<c>ParseMode</c>) is very much alive in both halves of the UI.
/// </summary>
internal static class RetiredSubtitleModeName
{
    public static string FormatMode(SubtitleMode mode) => mode switch
    {
        SubtitleMode.Burn => "burn",
        SubtitleMode.Copy => "copy",
        _ => "off",
    };
}

// ---------------------------------------------------------------------------
// Core/EncoderMath.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Retired in v7.9.3.1.
///
/// Last-resort transport pixel format. Moved wholesale into
/// <c>PixelFormatUtil.ChooseTransportPixFmt</c> when that helper took over the
/// decision; the array here kept its name and was left unreferenced, so two
/// copies of the same preference order were one edit away from disagreeing.
/// </summary>
internal static class RetiredTransportFallback
{
    private static readonly string[] PreferredTransportFallback =
        ["yuv420p", "yuv422p", "yuv444p", "rgb24", "gray"];
}

// ---------------------------------------------------------------------------
// Encoding/ArgsBuilder.cs
// ---------------------------------------------------------------------------

/// <summary>
/// Retired in v7.9.3.1.
///
/// The boolean-shaped filter chain from v7.7 ("crop 4:3 = yes/no"). v7.8 gave
/// every caller the explicit fit mode, and this overload only forwarded its
/// boolean into it -- a compatibility shim for a call site that no longer
/// exists. Its two magic strings are the mapping worth remembering: a v7.7
/// "yes" was a crop, "no" was a plain stretch.
/// </summary>
internal static class RetiredBooleanFilterChain
{
    public static string BuildFilters(
        int outW, int outH, double baseFps, bool crop43, string scaleAlgo,
        string transportFmt, string deinterlace, string deintMode,
        string deintParity, bool deintMarkedOnly) =>
        ArgsBuilder.BuildFilters(outW, outH, baseFps,
            crop43 ? "crop" : "stretch", "center", "black", scaleAlgo, transportFmt,
            deinterlace, deintMode, deintParity, deintMarkedOnly);
}
