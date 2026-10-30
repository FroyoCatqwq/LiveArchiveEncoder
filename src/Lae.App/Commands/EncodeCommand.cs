using System.CommandLine;
using Lae.App.Ui;
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
    public static Command Build(ISettingsStore store, ITerminal terminal)
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
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Print the two FFmpeg command lines and stop.",
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
            dryRunOption,
        };

        command.SetAction((ParseResult parseResult) =>
        {
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
            };

            return EncodeSession.Run(
                parseResult.GetValue(inputArgument)!,
                overrides,
                store.Load(),
                EncodeDependencies.Default,
                terminal,
                parseResult.GetValue(dryRunOption),
                Interrupt.Token);
        });

        return command;
    }
}
