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
                // v7.9.4: the usable list, not the declared one. mpeg1video
                // declares 15 fps and then refuses it when the encoder opens.
                List<double> rates = EncoderMath.UsableRates(c.Encoder.Caps);
                string rateText = rates.Count > 0
                    ? string.Join(", ", rates.Take(16).Select(x => x.ToString("G6")))
                    : L.T("value.notdeclared");
                Line(L.T("report.fpssupport"), rateText);
            }
        }
        string qualityLabel = c.Encoder is not null && c.Encoder.Name == "libx264" ? "CRF" : "q:v";
        Line(L.T("report.quality"), $"{qualityLabel} {c.Qv}");
        Line(L.T("report.fps"),
            $"{c.Fps:G6} -> {EncoderMath.FormatLadder(c.Fps, c.FpsLevels, c.FpsLevelKbps)}");
        // Only shown when the user actually pinned a threshold: the default
        // ladder is derived from the ceiling and would just repeat the numbers.
        if (c.FpsLevelKbps.Count > 0)
        {
            List<FpsRung> ladder = EncoderMath.BuildLadder(
                c.Fps, c.FpsLevels, c.FpsLevelKbps, c.Limit, c.Recover);
            Line(L.T("param.levelkbps"), string.Join(", ", ladder.Skip(1).Select(
                r => $"{r.Fps:G6}:{r.DropAboveKbps:G6}/{r.RiseBelowKbps:G6}")));
        }
        Line(L.T("report.limit"), L.T("report.kbps", $"{c.Limit:G6}"));
        Line(L.T("report.recover"), L.T("report.kbps", $"{c.Recover:G6}"));
        Line(L.T("report.window"), L.T("report.seconds", $"{c.Window:G6}"));
        Line(L.T("report.resolution"),
            $"{c.Width}x{c.Height} ({AspectLabel(c.Width, c.Height)})");
        Line(L.T("report.fitmode"), c.FitMode switch
        {
            "fit" => L.T("report.fit.fit", c.PadColor),
            "stretch" => L.T("report.fit.stretch"),
            _ => L.T("report.fit.crop", c.CropAnchor),
        });
        Line(L.T("report.scalealgo"), EncoderMath.CanonicalScaleAlgo(c.ScaleAlgo));
        Line(L.T("report.deinterlace"), c.Deinterlace);
        Line(L.T("report.deintmode"),
            L.T("report.deintparams", c.DeintMode, c.DeintParity, c.DeintMarkedOnly));
        Line(L.T("report.audio"), L.T("report.audioline",
            c.AudioEncoder ?? L.T("value.none"),
            c.AudioKbps.ToString(CultureInfo.InvariantCulture),
            c.AudioChannels.ToString(CultureInfo.InvariantCulture),
            c.AudioRate?.ToString(CultureInfo.InvariantCulture) ?? L.T("value.auto")));
        if (c.AudioEncoder is { Length: > 0 } codec)
            Line(L.T("report.audiorates"), AudioCatalog.DescribeRates(codec));
        // Subtitles change what the run produces as much as the shard count
        // does, so they belong in the dump -- shown only when active, since
        // "off" is the default and would just repeat the menu.
        if (c.Subtitles.Active)
        {
            string what = c.Subtitles.Files.Count > 0
                ? string.Join(", ", c.Subtitles.Files.Select(f => Path.GetFileName(f)))
                : L.T("sub.streamn", c.Subtitles.Stream!.Value);
            Line(L.T("sub.menu"), $"{c.Subtitles.Mode}: {what}"
                + (c.Subtitles.Codec is { Length: > 0 } sc ? $" ({sc})" : ""));
        }
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
        // The shard count changes what the run actually does, so it belongs in
        // the configuration dump even though it is not encoder state.
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

    /// <summary>"4:3" / "16:9" / a rounded ratio, so a 16:9 master stored in a
    /// 4:3 frame is obvious from the configuration dump.</summary>
    private static string AspectLabel(int width, int height)
    {
        if (width <= 0 || height <= 0) return "?";
        double ratio = (double)width / height;
        if (Math.Abs(ratio - 4.0 / 3.0) < 0.02) return "4:3";
        if (Math.Abs(ratio - 16.0 / 9.0) < 0.02) return "16:9";
        return $"{ratio:F2}:1";
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

    /// <summary>
    /// The current settings as one command line (v7.9.3).
    ///
    /// Built by <see cref="CliArgs.FromConfig"/>, i.e. the same emitter that
    /// backs <c>--emit-args</c> and the round-trip test, so what the menu shows
    /// is what a script would actually have to type -- not a paraphrase.
    /// </summary>
    public static void PrintCommandLine(Config c)
    {
        Console.WriteLine($"\n{L.T("args.heading")}");
        Console.WriteLine($"  {CliArgs.Join(CliArgs.FromConfig(c))}");
        Console.WriteLine($"  {L.T("args.hint")}");
    }

    /// <summary>"label               : value", padded by display width.</summary>
    private static void Line(string? label, string value) =>
        Console.WriteLine(label is null ? value : $"{L.Pad(label, 17)}: {value}");
}
