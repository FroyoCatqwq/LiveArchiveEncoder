using System.Globalization;
using System.Text;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.UI;

/// <summary>
/// v7.9.1: the config-file half of the CLI. Turns a settings snapshot into the
/// command line that reproduces it, so a run can be shared as text and a
/// command line can be saved as a settings file.
///
/// Two rules keep the output honest:
///
///   1. Only values that differ from the built-in defaults are emitted. The
///      defaults are the ones <see cref="CliOptions"/> carries, so the line
///      stays as short as the run allows while still reproducing it exactly.
///   2. <c>--era</c> is never emitted. A preset is a starting point, not a
///      setting: the encoder resolves it into plain values, and emitting the
///      label back would make the preset override the very values this line
///      is meant to pin. The resolved numbers are emitted instead.
///
/// The reverse direction (command line to settings file) is --save-config.
/// </summary>
public static class CliArgs
{
    /// <summary>
    /// argv form, ready for <see cref="CliParser.Parse(string[])"/>: every token
    /// separate, nothing quoted. This is the testable half.
    /// </summary>
    public static List<string> FromUserConfig(UserConfig c)
    {
        var argv = new List<string>();

        // Positionals first, exactly where a user would type them.
        if (c.InputPath is { Length: > 0 } input) argv.Add(input);
        if (c.OutputPath is { Length: > 0 } output) argv.Add(output);

        if (c.Encoder is { Length: > 0 } enc && !IsDefaultEncoder(enc)) argv.AddRange(["--encoder", enc]);
        if (c.AudioEncoder is { Length: > 0 } audio) argv.AddRange(["--audio-encoder", audio]);
        if (c.Container is { Length: > 0 } container) argv.AddRange(["--container", container]);
        if (c.StartTimeSeconds > 0)
            argv.AddRange(["--start-time", Timecode.Format(c.StartTimeSeconds)]);
        if (c.EndTimeSeconds is { } end)
            argv.AddRange(["--end-time", Timecode.Format(end)]);
        if (!Same(c.Fps, Constants.DefaultFps)) argv.AddRange(["--fps", Num(c.Fps)]);

        string? ladder = LadderSpec(c);
        if (ladder is not null) argv.AddRange(["--fps-levels", ladder]);

        if (!Same(c.Limit, 200.0)) argv.AddRange(["--limit", Num(c.Limit)]);
        // Recover defaults to limit * 0.75, so only a value that breaks that
        // relation is a setting worth carrying.
        if (!Same(c.Recover, c.Limit * 0.75)) argv.AddRange(["--recover", Num(c.Recover)]);
        if (!Same(c.Window, 2.0)) argv.AddRange(["--window", Num(c.Window)]);
        if (c.Qv != EncoderMath.DefaultQuality(c.Encoder ?? "rv20"))
            argv.AddRange(["--qv", c.Qv.ToString(CultureInfo.InvariantCulture)]);
        if (c.Threads != 16) argv.AddRange(["--threads", c.Threads.ToString(CultureInfo.InvariantCulture)]);
        if (c.BufferFrames != 6)
            argv.AddRange(["--buffer-frames", c.BufferFrames.ToString(CultureInfo.InvariantCulture)]);
        if (c.AudioKbps != 32)
            argv.AddRange(["--audio-kbps", c.AudioKbps.ToString(CultureInfo.InvariantCulture)]);
        if (c.AudioChannels != 1)
            argv.AddRange(["--audio-channels", c.AudioChannels.ToString(CultureInfo.InvariantCulture)]);
        if (c.AudioRate is { } rate) argv.AddRange(["--audio-rate", rate.ToString(CultureInfo.InvariantCulture)]);
        if (c.VideoStream is { } vs)
            argv.AddRange(["--video-stream", vs.ToString(CultureInfo.InvariantCulture)]);
        if (c.AudioStream is { } asx)
            argv.AddRange(["--audio-stream", asx.ToString(CultureInfo.InvariantCulture)]);
        if (c.Width != Constants.DefaultWidth)
            argv.AddRange(["--width", c.Width.ToString(CultureInfo.InvariantCulture)]);
        if (c.Height != Constants.DefaultHeight)
            argv.AddRange(["--height", c.Height.ToString(CultureInfo.InvariantCulture)]);
        if (!string.Equals(c.FitMode, "crop", StringComparison.OrdinalIgnoreCase))
            argv.AddRange(["--fit", c.FitMode]);
        if (!string.Equals(c.CropAnchor, "center", StringComparison.OrdinalIgnoreCase))
            argv.AddRange(["--crop-anchor", c.CropAnchor]);
        if (!string.Equals(c.PadColor, "black", StringComparison.OrdinalIgnoreCase))
            argv.AddRange(["--pad-color", c.PadColor]);
        if (!string.Equals(c.ScaleAlgo, "neighbor", StringComparison.OrdinalIgnoreCase))
            argv.AddRange(["--scale-algo", c.ScaleAlgo]);
        if (!string.Equals(c.Deinterlace, "off", StringComparison.OrdinalIgnoreCase))
            argv.AddRange(["--deinterlace", c.Deinterlace]);
        if (!string.Equals(c.DeintMode, "send_frame", StringComparison.OrdinalIgnoreCase))
            argv.AddRange(["--deint-mode", c.DeintMode]);
        if (!string.Equals(c.DeintParity, "auto", StringComparison.OrdinalIgnoreCase))
            argv.AddRange(["--deint-parity", c.DeintParity]);
        if (c.DeintMarkedOnly) argv.Add("--deint-marked-only");
        if (!string.Equals(c.Hwaccel, "off", StringComparison.OrdinalIgnoreCase))
            argv.AddRange(["--hwaccel", c.Hwaccel]);
        if (!string.Equals(c.X264Preset, "medium", StringComparison.OrdinalIgnoreCase))
            argv.AddRange(["--x264-preset", c.X264Preset]);
        if (c.X264Tune is { Length: > 0 } tune) argv.AddRange(["--x264-tune", tune]);
        if (c.X264Profile is { Length: > 0 } profile) argv.AddRange(["--x264-profile", profile]);
        if (c.X264Level is { Length: > 0 } level) argv.AddRange(["--x264-level", level]);
        if (c.Parallel > 1)
            argv.AddRange(["--parallel", c.Parallel.ToString(CultureInfo.InvariantCulture)]);
        if (!string.Equals(c.Language, "en", StringComparison.OrdinalIgnoreCase))
            argv.AddRange(["--lang", c.Language]);
        if (!string.Equals(c.FfmpegSource, FFmpeg.FfmpegCatalog.DefaultId, StringComparison.OrdinalIgnoreCase))
            argv.AddRange(["--ffmpeg-source", c.FfmpegSource]);
        if (c.FfmpegProxy is { Length: > 0 } proxy) argv.AddRange(["--proxy", proxy]);
        if (!string.Equals(c.PlayMode, "off", StringComparison.OrdinalIgnoreCase))
            argv.AddRange(["--play", c.PlayMode]);
        if (c.PlayerPath is { Length: > 0 } player) argv.AddRange(["--player", player]);

        if (c.Recompress.Enabled)
        {
            argv.Add("--recompress");
            if (c.Recompress.Args is { Length: > 0 } rcArgs) argv.AddRange(["--recompress-args", rcArgs]);
            if (!string.Equals(c.Recompress.Container, "mp4", StringComparison.OrdinalIgnoreCase))
                argv.AddRange(["--recompress-container", c.Recompress.Container]);
        }

        // Subtitles: nothing is emitted for an inactive request, because
        // "burn with nothing picked" is exactly what the defaults already mean.
        if (c.Subtitles.Active)
        {
            foreach (string file in c.Subtitles.Files) argv.AddRange(["--subtitle", file]);
            if (c.Subtitles.Stream is { } stream)
                argv.AddRange(["--subtitle-stream", stream.ToString(CultureInfo.InvariantCulture)]);
            if (!string.Equals(c.Subtitles.Mode, "burn", StringComparison.OrdinalIgnoreCase))
                argv.AddRange(["--subtitle-mode", c.Subtitles.Mode]);
            if (c.Subtitles.Codec is { Length: > 0 } codec) argv.AddRange(["--subtitle-codec", codec]);
        }

        return argv;
    }

    /// <summary>Convenience for a resolved run: capture first, then emit.</summary>
    public static List<string> FromConfig(Config c) => FromUserConfig(ConfigStore.Capture(c));

    /// <summary>
    /// One line, shell-friendly: tokens with spaces or quotes are wrapped and
    /// their quotes doubled, the Windows convention every shell here reads.
    /// </summary>
    public static string Join(IReadOnlyList<string> argv)
    {
        var sb = new StringBuilder();
        foreach (string token in argv)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Quote(token));
        }
        return sb.ToString();
    }

    /// <summary>Quote one token when it needs it.</summary>
    public static string Quote(string token)
    {
        if (token.Length == 0) return "\"\"";
        bool needsQuotes = token.AsSpan().ContainsAny(' ', '\t', '"');
        return needsQuotes ? "\"" + token.Replace("\"", "\"\"") + "\"" : token;
    }

    /// <summary>
    /// The ladder as a --fps-levels spec, or null when it is the default
    /// "13,8,5,3" with derived thresholds (i.e. nothing the user chose).
    ///
    /// The level list is not the user's ladder: the resolver prepends the base
    /// rate (the --fps value) as its first entry, so a run at 25 fps with the
    /// default ladder stores [25, 13, 8, 5, 3]. The rungs are what the user
    /// actually typed, and only entries that have one are emitted -- that is
    /// what keeps the derived base out of the line.
    /// </summary>
    private static string? LadderSpec(UserConfig c)
    {
        var chosen = new List<(double Fps, FpsRungSetting? Rung)>();
        foreach (double fps in c.FpsLevels)
        {
            FpsRungSetting? rung = c.FpsLevelKbps.FirstOrDefault(r => Same(r.Fps, fps));
            if (rung is not null) chosen.Add((fps, rung));
        }
        // A file with no rungs at all (hand-written, or a pre-v7.7 one): fall
        // back to the level list so the ladder still survives the trip.
        if (chosen.Count == 0)
            chosen.AddRange(c.FpsLevels.Select(f => (f, (FpsRungSetting?)null)));

        bool isDefault = chosen.Count == 4
            && Same(chosen[0].Fps, 13.0) && Same(chosen[1].Fps, 8.0)
            && Same(chosen[2].Fps, 5.0) && Same(chosen[3].Fps, 3.0)
            && chosen.All(e => e.Rung?.DropAboveKbps is null && e.Rung?.RiseBelowKbps is null);
        if (isDefault) return null;

        var sb = new StringBuilder();
        foreach ((double fps, FpsRungSetting? rung) in chosen)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(Num(fps));
            if (rung?.DropAboveKbps is { } drop)
            {
                sb.Append(':').Append(Num(drop));
                if (rung.RiseBelowKbps is { } rise) sb.Append(':').Append(Num(rise));
            }
        }
        return sb.ToString();
    }

    private static bool IsDefaultEncoder(string name) =>
        string.Equals(name, "rv20", StringComparison.OrdinalIgnoreCase);

    private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-9;

    /// <summary>Invariant, no trailing zeros: 13.0 prints as "13", 12.5 as "12.5".</summary>
    private static string Num(double value)
    {
        string text = value.ToString("G6", CultureInfo.InvariantCulture);
        return text;
    }
}
