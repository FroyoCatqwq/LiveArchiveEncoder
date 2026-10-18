using System.Globalization;
using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Localization;

namespace LiveArchiveEncoder.UI;

/// <summary>Configuration / filter reporting shared by CUI and CLI.</summary>
public static class Report
{
    public static void PrintConfig(
        Config c, string ffmpeg, bool btbn, int encoderCount,
        HashSet<string> availableFilters, HashSet<string> availableScaleAlgorithms)
    {
        Console.WriteLine($"\n{L.T("report.heading")}");
        Line(L.T("report.ffmpeg"), ffmpeg);
        Line(L.T("report.source"), btbn ? L.T("report.sourcebtbn") : L.T("report.sourcepath"));
        Line(L.T("report.legacy"), encoderCount.ToString(CultureInfo.InvariantCulture));
        Line(L.T("report.input"), c.InputPath?.FullName ?? L.T("value.notselected"));
        Line(L.T("report.timerange"),
            $"{Timecode.Format(c.StartTimeSeconds)} - {Timecode.Format(c.EndTimeSeconds)}");
        Line(L.T("report.output"), c.OutputPath?.FullName ?? L.T("menu.auto"));
        Line(L.T("report.outputmode"), c.OutputIsAuto ? L.T("value.auto") : L.T("value.custom"));
        Line(L.T("report.videocodec"), c.Encoder?.Name ?? L.T("value.notselected"));
        if (c.Encoder is not null)
        {
            Line(L.T("report.container"), $"{c.Encoder.FormatName} (.{c.Encoder.Extension})");
            if (c.Encoder.Caps is not null)
            {
                string pixText = c.Encoder.Caps.SupportedPixFmts.Count > 0
                    ? string.Join(", ", c.Encoder.Caps.SupportedPixFmts)
                    : L.T("value.auto");
                Line(L.T("report.pixfmts"), pixText);
                string rateText = c.Encoder.Caps.SupportedRates.Count > 0
                    ? string.Join(", ", c.Encoder.Caps.SupportedRates.Take(16).Select(x => x.ToString("G6")))
                    : L.T("value.notdeclared");
                Line(L.T("report.fpssupport"), rateText);
            }
        }
        string qualityLabel = c.Encoder is not null && c.Encoder.Name == "libx264" ? "CRF" : "q:v";
        Line(L.T("report.quality"), $"{qualityLabel} {c.Qv}");
        Line(L.T("report.fps"),
            $"{c.Fps:G6} -> {string.Join(",", c.FpsLevels.Select(x => x.ToString("G6")))}");
        Line(L.T("report.limit"), L.T("report.kbps", $"{c.Limit:G6}"));
        Line(L.T("report.recover"), L.T("report.kbps", $"{c.Recover:G6}"));
        Line(L.T("report.window"), L.T("report.seconds", $"{c.Window:G6}"));
        Line(L.T("report.resolution"), $"{c.Width}x{c.Height}");
        Line(L.T("report.crop43"), L.YesNo(c.Crop43));
        Line(L.T("report.scalealgo"), EncoderMath.CanonicalScaleAlgo(c.ScaleAlgo));
        Line(L.T("report.deinterlace"), c.Deinterlace);
        Line(L.T("report.deintmode"),
            L.T("report.deintparams", c.DeintMode, c.DeintParity, c.DeintMarkedOnly));
        Line(L.T("report.audio"), L.T("report.audioline",
            c.AudioEncoder ?? L.T("value.none"),
            c.AudioKbps.ToString(CultureInfo.InvariantCulture),
            c.AudioChannels.ToString(CultureInfo.InvariantCulture),
            c.AudioRate?.ToString(CultureInfo.InvariantCulture) ?? L.T("value.auto")));
        if (c.Encoder is not null && c.Encoder.Name == "roqvideo")
        {
            string? warning = EncoderMath.ValidateRoqDimensions(c.Width, c.Height);
            if (warning is not null) Line(L.T("report.roqnote"), warning);
            if (c.AudioEncoder == "roq_dpcm") Line(L.T("report.roqaudio"), L.T("report.roqaudio.value"));
        }
        Line(L.T("report.threads"), c.Threads.ToString(CultureInfo.InvariantCulture));
        Line(L.T("report.buffer"), $"{c.BufferFrames} {L.T("param.buffer.frames")}");
        Line(L.T("report.hwaccel"), c.Hwaccel);
        string deintAvailable = string.Join(", ",
            Constants.DeinterlaceFilters.Where(f => availableFilters.Contains(f)));
        Line(L.T("report.deintavail"), deintAvailable.Length == 0 ? L.T("value.none") : deintAvailable);
        Line(L.T("report.scaleavail"),
            string.Join(", ", availableScaleAlgorithms.OrderBy(x => x, StringComparer.Ordinal)));
        Line(L.T("report.impairments"), "use frame_damage.py (separate post-processing tool)");
        // Turbo and the shard count change what the run actually does, so they
        // belong in the configuration dump even though they are not encoder state.
        Line(L.T("menu.turbo"), c.Turbo ? L.T("turbo.on") : L.T("turbo.off"));
        Line(L.T("menu.parallel"),
            c.Parallel > 1 ? c.Parallel.ToString(CultureInfo.InvariantCulture) : L.T("menu.parallel.off"));
        Line(L.T("menu.language"), c.Language);
        Line(L.T("banner.source"), c.FfmpegSource);
        Line(L.T("menu.play"), Player.Describe(c.Play, c.PlayerPath, FFmpegDiscovery.FindFFplay(ffmpeg)));
        if (c.Encoder is not null && c.Encoder.Name == "libx264")
        {
            Line(L.T("param.x264preset"), c.X264Preset);
            Line(L.T("param.x264tune"), c.X264Tune.Length == 0 ? L.T("value.none") : c.X264Tune);
            Line(L.T("param.x264profile"), c.X264Profile.Length == 0 ? L.T("value.auto") : c.X264Profile);
            Line(L.T("param.x264level"), c.X264Level.Length == 0 ? L.T("value.auto") : c.X264Level);
        }
    }

    public static void PrintFilterList(
        string ffmpeg, HashSet<string> availableFilters, HashSet<string> availableScaleAlgorithms)
    {
        Console.WriteLine($"\n{L.T("filters.heading")}");
        Console.WriteLine($"FFmpeg: {ffmpeg}");
        foreach (string name in Constants.DeinterlaceFilters)
        {
            Console.WriteLine($" {(availableFilters.Contains(name) ? "OK " : "---")} {name}");
        }
        Console.WriteLine($"\n{L.T("filters.scalers")}");
        Console.WriteLine(" " + string.Join(", ", availableScaleAlgorithms.OrderBy(x => x, StringComparer.Ordinal)));
    }

    public static void PrintResult(string outputPath, Encoding.EncodeResult result)
    {
        Console.WriteLine(L.T("result.done", outputPath));
        Line(L.T("result.timerange"),
            $"{Timecode.Format(result.StartTimeSeconds)} - {Timecode.Format(result.EndTimeSeconds)}");
        Line(L.T("result.duration"), $"{result.Duration:F2}s");
        Line(L.T("result.sourcepix"), result.SourcePixFmt);
        Line(L.T("result.transportpix"), result.TransportPixFmt);
        Line(L.T("result.resolution"), $"{result.OutputWidth}x{result.OutputHeight}");
        Line(L.T("result.videoavg"), L.T("result.kbps", $"{result.AvgVideoKbps:F1}"));
        Line(L.T("result.totalavg"), L.T("result.kbps", $"{result.AvgTotalKbps:F1}"));
        Line(L.T("result.fresh"), result.Fresh.ToString(CultureInfo.InvariantCulture));
        Line(L.T("result.repeated"), result.Repeated.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>"label               : value", padded by display width.</summary>
    private static void Line(string? label, string value) =>
        Console.WriteLine(label is null ? value : $"{L.Pad(label, 17)}: {value}");
}
