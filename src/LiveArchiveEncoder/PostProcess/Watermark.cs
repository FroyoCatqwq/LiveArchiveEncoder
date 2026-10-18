using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.PostProcess;

/// <summary>
/// The watermark stage: burn static text into the live recording.
///
/// Runs between the live pass and the recompress pass, which is where a video
/// site of the era put its logo -- after capture, before (or as part of) the
/// upload transcode.
///
/// The stage re-encodes, because a filter forces it to. It writes MP4/H.264
/// rather than trying to reproduce the legacy container: a watermarked
/// intermediate has to be something every later stage can read, and guessing a
/// legacy encoder's constraints (roq needs 16-pixel multiples, amv needs an
/// exact stream count) would make this stage fail on precisely the formats the
/// tool exists to produce.
/// </summary>
public static class Watermark
{
    /// <summary>Generous: a re-encode is offline work, not the paced live pass.</summary>
    private const int TimeoutSeconds = 3600;

    /// <summary>
    /// Build the <c>drawtext</c> filter expression.
    ///
    /// Exposed separately so it can be asserted on without running FFmpeg.
    /// </summary>
    public static string BuildFilter(WatermarkSettings settings, int width, int height, string? fontFile)
    {
        // Scale the mark to the picture: a 16 px watermark on a 160x120 clip
        // would cover a sixth of the frame, and nobody's 1998 player did that.
        int fontSize = settings.FontSize > 0 ? settings.FontSize : Math.Max(8, height / 15);
        int margin = settings.Margin > 0 ? settings.Margin : Math.Max(4, height / 40);

        (string x, string y) = WatermarkSettings.CanonicalPosition(settings.Position) switch
        {
            "top-left" => ($"{margin}", $"{margin}"),
            "top-right" => ($"W-tw-{margin}", $"{margin}"),
            "bottom-left" => ($"{margin}", $"H-th-{margin}"),
            "bottom-right" => ($"W-tw-{margin}", $"H-th-{margin}"),
            _ => ("(W-tw)/2", "(H-th)/2"),
        };

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(fontFile))
            parts.Add($"fontfile='{FontResolver.ToFilterPath(fontFile)}'");
        parts.Add($"text='{PostArgs.EscapeFilterValue(settings.Text)}'");
        parts.Add($"x={x}");
        parts.Add($"y={y}");
        parts.Add($"fontsize={fontSize}");
        parts.Add($"fontcolor={PostArgs.EscapeFilterValue(settings.FontColor)}");

        return "drawtext=" + string.Join(":", parts);
    }

    /// <summary>
    /// Run the stage. Returns an error message rather than throwing, because a
    /// failed watermark must never lose the live product -- the caller reports
    /// it and keeps the file that did get written.
    /// </summary>
    public static (bool Ok, string? Error) Apply(
        string ffmpeg, string input, string output,
        WatermarkSettings settings, int width, int height)
    {
        if (!settings.Enabled) return (true, null);

        if (string.IsNullOrWhiteSpace(settings.Text))
            return (false, "watermark is enabled but the text is empty");

        string? font = string.IsNullOrWhiteSpace(settings.FontFile) ? FontResolver.Find() : settings.FontFile;
        if (font is null || !File.Exists(font))
            return (false, "no usable font found; install one or set WatermarkFontFile "
                         + $"(or ${FontResolver.EnvironmentVariable}) to a .ttf/.ttc file");

        string filter = BuildFilter(settings, width, height, font);

        string? dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        CaptureResult result;
        try
        {
            result = ProcessRunner.Run(
                [ffmpeg, "-y", "-hide_banner", "-loglevel", "error",
                 "-i", input,
                 "-vf", filter,
                 "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                 "-c:a", "aac", "-b:a", "64k",
                 "-movflags", "+faststart",
                 output],
                TimeoutSeconds);
        }
        // Same reasoning as the recompress stage: a bad executable or a missing
        // input arrives as an OS exception and must become a message, not an
        // unhandled crash that takes the live product's report with it.
        catch (Exception exc) when (exc is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or TimeoutException
                                       or IOException)
        {
            return (false, exc.Message);
        }

        if (result.ExitCode != 0)
            return (false, $"ffmpeg drawtext failed (exit {result.ExitCode}): {Tail(result.Stderr)}");

        if (!File.Exists(output) || new FileInfo(output).Length == 0)
            return (false, "the watermarked file was not produced");

        return (true, null);
    }

    /// <summary>
    /// Insert "_wm" before the extension and switch the container to mp4.
    /// <c>clip_live.rm</c> becomes <c>clip_live_wm.mp4</c>.
    /// </summary>
    public static string DeriveOutputPath(string livePath)
    {
        string dir = Path.GetDirectoryName(livePath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(livePath);
        return Path.Combine(dir, stem + "_wm.mp4");
    }

    private static string Tail(string text)
    {
        string flat = text.Trim().Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= 300 ? flat : flat[^300..];
    }
}
