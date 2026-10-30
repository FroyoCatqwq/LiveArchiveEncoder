using System.CommandLine;
using Lae.App.Ui;
using Lae.Core;
using Lae.Core.History;
using Lae.Core.Localization;
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
///
/// <para><b>Option text is translated at build time.</b> That is why the
/// <paramref name="messages"/> parameter exists: <c>--help</c> is fixed when the
/// tree is built, so the language has to be known before then. It is, because the
/// host reads the settings once for exactly this reason.</para>
/// </summary>
public static class EncodeCommand
{
    public static Command Build(ISettingsStore store, ITerminal terminal, IRunHistory history, IMessages messages)
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = messages["encode.arg.input"],
        };

        var outOption = new Option<string?>("--out")
        {
            Description = messages["encode.opt.out"],
        };
        var encoderOption = new Option<string?>("--encoder")
        {
            Description = messages["encode.opt.encoder"],
        };
        var containerOption = new Option<string?>("--container")
        {
            Description = messages["encode.opt.container"],
        };
        var fromOption = new Option<double?>("--from")
        {
            Description = messages["encode.opt.from"],
        };
        var toOption = new Option<double?>("--to")
        {
            Description = messages["encode.opt.to"],
        };
        var qualityOption = new Option<int?>("--quality")
        {
            Description = messages["encode.opt.quality"],
        };
        var sizeOption = new Option<string?>("--size")
        {
            Description = messages["encode.opt.size"],
        };
        var presetOption = new Option<string?>("--preset")
        {
            Description = messages["encode.opt.preset"],
        };
        var limitOption = new Option<double?>("--limit")
        {
            Description = messages["encode.opt.limit"],
        };
        var recoverOption = new Option<double?>("--recover")
        {
            Description = messages["encode.opt.recover"],
        };
        var levelsOption = new Option<string?>("--fps-levels")
        {
            Description = messages["encode.opt.fpsLevels"],
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = messages["encode.opt.dryRun"],
        };
        var subtitleOption = new Option<string[]>("--subtitle-file")
        {
            Description = messages["encode.opt.subtitleFile"],
        };
        var subtitleTrackOption = new Option<int?>("--subtitle-track")
        {
            Description = messages["encode.opt.subtitleTrack"],
        };
        var subtitleCopyOption = new Option<int[]>("--subtitle-copy")
        {
            Description = messages["encode.opt.subtitleCopy"],
        };
        var subtitleBoxOption = new Option<string?>("--subtitle-box")
        {
            Description = messages["encode.opt.subtitleBox"],
        };
        var subtitleFontOption = new Option<string?>("--subtitle-font")
        {
            Description = messages["encode.opt.subtitleFont"],
        };
        var fontsDirOption = new Option<string?>("--fonts-dir")
        {
            Description = messages["encode.opt.fontsDir"],
        };
        var recompressOption = new Option<string[]>("--recompress")
        {
            Description = messages["encode.opt.recompress"],
        };
        var recompressContainerOption = new Option<string[]>("--recompress-container")
        {
            Description = messages["encode.opt.recompressContainer"],
        };
        var recompressOnlyOption = new Option<bool>("--recompress-only")
        {
            Description = messages["encode.opt.recompressOnly"],
        };

        var command = new Command("encode", messages["encode.desc"])
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
                    terminal.ErrorLine(messages.Format("encode.error.fpsLevels", new { detail = error }));
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
                EncodeDependencies.Default with { History = history, Messages = messages },
                terminal,
                parseResult.GetValue(dryRunOption),
                Interrupt.Token);
        });

        return command;
    }
}
