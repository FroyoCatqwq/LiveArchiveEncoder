using System.CommandLine;
using Lae.App.Ui;
using Lae.Core;
using Lae.Core.History;
using Lae.Core.Settings;

namespace Lae.App.Commands;

/// <summary>
/// <c>lae encode</c> — the arguments, and nothing else.
///
/// <para>This type used to hold the whole command: toolchain discovery, probing,
/// encoder resolution, planning, reporting and the run itself, in one closure
/// that could only be tested by starting the program. Now it declares nine
/// options and translates them into an <see cref="EncodeOverrides"/>; every
/// decision moved to <see cref="EncodeSession"/>, which takes a terminal and a
/// set of dependencies and can therefore be driven by a test.</para>
/// </summary>
public static class EncodeCommand
{
    public static Command Build(ISettingsStore store, ITerminal terminal, IRunHistory history)
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "The source to re-encode.",
        };

        var outOption = new Option<string?>("--out")
        {
            Description = "Output path. Defaults to the source name with the encoder's extension.",
        };
        var encoderOption = new Option<string?>("--encoder")
        {
            Description = "Video encoder, e.g. mpeg4, libx264, flv. Defaults to the stored one.",
        };
        var containerOption = new Option<string?>("--container")
        {
            Description = "Container extension, e.g. avi, mkv, mp4. Defaults to the encoder's own.",
        };
        var fromOption = new Option<double?>("--from")
        {
            Description = "Start offset in seconds.",
        };
        var toOption = new Option<double?>("--to")
        {
            Description = "End offset in seconds.",
        };
        var qualityOption = new Option<int?>("--quality")
        {
            Description = "Quality in the encoder's own scale (2..31 for the mpeg family, 0..51 for x264).",
        };
        var sizeOption = new Option<string?>("--size")
        {
            Description = "Output frame size as WIDTHxHEIGHT.",
        };
        var presetOption = new Option<string?>("--preset")
        {
            Description = "Start from one of your stored presets; 'lae preset list' shows them.",
        };
        var limitOption = new Option<double?>("--limit")
        {
            Description = "Bitrate ceiling in kbps. The ladder drops frames rather than exceed it.",
        };
        var recoverOption = new Option<double?>("--recover")
        {
            Description = "Bitrate the ladder climbs back from; defaults to 75% of the ceiling.",
        };
        var levelsOption = new Option<string?>("--fps-levels")
        {
            Description = "Ladder as FPS[,FPS[:drop[:rise]]...], e.g. 13,8,5,3 or 13,8:150:110.",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Print the two FFmpeg command lines and stop.",
        };
        var subtitleOption = new Option<string[]>("--subtitle-file")
        {
            Description = "Burn this .srt/.ass into the picture; repeat for more than one.",
        };
        var subtitleTrackOption = new Option<int?>("--subtitle-track")
        {
            Description = "Burn the embedded subtitle track with this absolute index.",
        };
        var subtitleCopyOption = new Option<int[]>("--subtitle-copy")
        {
            Description = "Carry this subtitle track into the output as a stream; repeat for more than one.",
        };
        var subtitleBoxOption = new Option<string?>("--subtitle-box")
        {
            Description = "Shape the text is laid out in: source, 4:3 or 16:9.",
        };
        var subtitleFontOption = new Option<string?>("--subtitle-font")
        {
            Description = "Font family forced over whatever the subtitle file asks for.",
        };
        var fontsDirOption = new Option<string?>("--fonts-dir")
        {
            Description = "Extra folder libass may look in for fonts.",
        };
        var recompressOption = new Option<string[]>("--recompress")
        {
            Description = "One FFmpeg fragment for a post-encode pass; repeat for a chain "
                        + "(a 2007 portal first, then the 2011 one).",
        };
        var recompressContainerOption = new Option<string[]>("--recompress-container")
        {
            Description = "Container for a post-encode pass, paired by position with --recompress.",
        };
        var recompressOnlyOption = new Option<bool>("--recompress-only")
        {
            Description = "Run the chain against the existing file and skip the capture entirely.",
        };

        var command = new Command("encode", "Re-encode one source at archive pace.")
        {
            inputArgument,
            outOption,
            encoderOption,
            containerOption,
            fromOption,
            toOption,
            qualityOption,
            sizeOption,
            presetOption,
            limitOption,
            recoverOption,
            levelsOption,
            subtitleOption,
            subtitleTrackOption,
            subtitleCopyOption,
            subtitleBoxOption,
            subtitleFontOption,
            fontsDirOption,
            recompressOption,
            recompressContainerOption,
            recompressOnlyOption,
            dryRunOption,
        };

        command.SetAction((ParseResult parseResult) =>
        {
            string? levelsText = parseResult.GetValue(levelsOption);
            List<double>? levels = null;
            List<FpsThreshold>? thresholds = null;

            if (levelsText is not null)
            {
                (List<double> parsed, List<FpsRungSetting> settings, string? error) =
                    EncoderMath.ParseLevelSpec(levelsText);

                if (error is not null)
                {
                    terminal.ErrorLine($"ERROR: --fps-levels: {error}");
                    return ExitCodes.Usage;
                }

                levels = parsed;
                thresholds =
                [
                    .. settings.Select(s => new FpsThreshold(s.Fps, s.DropAboveKbps, s.RiseBelowKbps)),
                ];
            }

            var overrides = new EncodeOverrides
            {
                Output = parseResult.GetValue(outOption),
                Encoder = parseResult.GetValue(encoderOption),
                Container = parseResult.GetValue(containerOption),
                From = parseResult.GetValue(fromOption),
                To = parseResult.GetValue(toOption),
                Quality = parseResult.GetValue(qualityOption),
                Size = parseResult.GetValue(sizeOption),
                Preset = parseResult.GetValue(presetOption),
                LimitKbps = parseResult.GetValue(limitOption),
                RecoverKbps = parseResult.GetValue(recoverOption),
                FpsLevels = levels,
                FpsThresholds = thresholds,
                SubtitleFiles = parseResult.GetValue(subtitleOption),
                SubtitleTrack = parseResult.GetValue(subtitleTrackOption),
                SubtitleCopyTracks = parseResult.GetValue(subtitleCopyOption),
                SubtitleBox = parseResult.GetValue(subtitleBoxOption),
                SubtitleFont = parseResult.GetValue(subtitleFontOption),
                FontsDir = parseResult.GetValue(fontsDirOption),
                RecompressArgs = parseResult.GetValue(recompressOption),
                RecompressContainers = parseResult.GetValue(recompressContainerOption),
                RecompressOnly = parseResult.GetValue(recompressOnlyOption),
            };

            return EncodeSession.Run(
                parseResult.GetValue(inputArgument)!,
                overrides,
                store.Load(),
                EncodeDependencies.Default with { History = history },
                terminal,
                parseResult.GetValue(dryRunOption),
                Interrupt.Token);
        });

        return command;
    }
}
