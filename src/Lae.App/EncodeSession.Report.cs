using System.Globalization;
using Lae.App.Ui;
using Lae.Core;
using Lae.Core.Localization;
using Lae.Encoding;

namespace Lae.App;

/// <summary>
/// Everything <c>lae encode</c> prints, in one place.
///
/// <para>Split out because reporting is where a CLI is easiest to get quietly
/// wrong: the plan has to be complete enough to be pasted into a shell, the
/// result has to use the terms the user asked the question in, and a dry run
/// must put the command lines on stdout while every comment about them goes to
/// stderr. Keeping the three together is what makes that rule visible.</para>
///
/// <para><b>Numbers and paths are not translated; labels and sentences are.</b>
/// The numbers are formatted here, before the sentence sees them, because a
/// placeholder is a hole in a sentence and alignment rules are not portable --
/// <c>{{seconds}}</c> arrives already padded to the width the column needs, and
/// the translator never has to know that.</para>
/// </summary>
public static partial class EncodeSession
{
    /// <summary>
    /// The width every label in the plan and in the result occupies, so the
    /// values form a column. A label longer than this -- which a translation can
    /// well be -- gets a single space instead of being cut.
    /// </summary>
    private const int LabelWidth = 11;

    private static void Field(ITerminal terminal, IMessages messages, string key, string value)
        => terminal.Line(Label(messages[key]) + value);

    private static string Label(string text) => text.Length >= LabelWidth ? text + " " : text.PadRight(LabelWidth);

    private static string Number(double value, string format)
        => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>The plan, in the terms the user asked the question in.</summary>
    private static void PrintPlan(
        EncodePlan plan, SourceInfo source, string ffmpeg, ITerminal terminal, IMessages messages)
    {
        Field(terminal, messages, "encode.plan.source", source.HasAudio
            ? messages.Format("encode.plan.sourceValue", new
            {
                width = source.Width,
                height = source.Height,
                pixFmt = source.PixelFormat,
                seconds = Number(source.DurationSeconds, "G6"),
                rate = source.AudioRate,
                channels = source.AudioChannels,
            })
            : messages.Format("encode.plan.sourceValueSilent", new
            {
                width = source.Width,
                height = source.Height,
                pixFmt = source.PixelFormat,
                seconds = Number(source.DurationSeconds, "G6"),
            }));

        Field(terminal, messages, "encode.plan.output", messages.Format("encode.plan.outputValue", new
        {
            width = plan.Width,
            height = plan.Height,
            pixFmt = plan.TransportPixelFormat,
            container = plan.Container,
            muxer = plan.Muxer,
            fps = Number(plan.Fps, "G6"),
        }));

        Field(terminal, messages, "encode.plan.span", messages.Format("encode.plan.spanValue", new
        {
            seconds = Number(plan.SpanSeconds, "G6"),
            frames = plan.TargetFrames,
        }));

        // The quality number is the one setting whose meaning changes with the
        // encoder -- the same 8 is "quite good" in the mpeg family and "nearly
        // lossless" in x264 -- so a dry run names the flag and the scale rather
        // than a bare number the reader has to interpret.
        string quality = EncoderMath.ScaleOf(plan.Encode.Encoder) switch
        {
            EncoderMath.QualityScale.Crf
                => messages.Format("encode.plan.qualityCrf", new { value = plan.Encode.Quality }),
            EncoderMath.QualityScale.Bitrate
                => messages["encode.plan.qualityBitrate"],
            _
                => messages.Format("encode.plan.qualityQv", new { value = plan.Encode.Quality }),
        };
        Field(terminal, messages, "encode.plan.quality", quality);

        // The ladder is the one part of the plan the user cannot see in the
        // command lines -- it changes what goes down the pipe, not what FFmpeg is
        // told -- so a dry run has to name it or the two commands are not the
        // whole story.
        if (plan.BuildLadder() is { } ladder)
        {
            Field(terminal, messages, "encode.plan.ladder", messages.Format("encode.plan.ladderValue", new
            {
                rungs = string.Join(" > ", ladder.Rungs.Select(r => Number(r.Fps, "G4"))),
                down = Number(plan.Encode.LimitKbps, "G4"),
                up = Number(LadderPolicy.RecoverKbps(plan.Encode), "G4"),
                window = Number(plan.Encode.WindowSeconds, "G4"),
            }));
        }
        else
        {
            Field(terminal, messages, "encode.plan.ladder", messages["encode.plan.ladderOff"]);
        }

        Field(terminal, messages, "encode.plan.frame",
            messages.Format("encode.plan.frameValue", new { bytes = plan.FrameSize }));

        terminal.Line();
        terminal.Line(messages["encode.plan.decoder"]);
        terminal.Line("  " + Quote(plan.DecodeCommand(ffmpeg)));
        terminal.Line(messages["encode.plan.encoder"]);
        terminal.Line("  " + Quote(plan.EncodeCommand(ffmpeg)));
    }

    /// <summary>
    /// The post-encode hops, one command line each. A dry run exists to be
    /// pasted into a shell, so the chain has to be in it: a plan that shows the
    /// capture and hides three transcodes is not the plan.
    /// </summary>
    private static void PrintChain(RecompressPlan plan, string ffmpeg, ITerminal terminal, IMessages messages)
    {
        terminal.Line();
        terminal.Line(messages["encode.chain.header"]);
        foreach (RecompressStep step in plan.Steps)
            terminal.Line($"  {Quote(step.Command(ffmpeg, step.OutputPath))}");
    }

    /// <summary>
    /// What came out, in the terms the archive cares about: how long it ran
    /// against the span it covered, and what bitrate the result actually is.
    /// </summary>
    private static void PrintResult(LiveRun run, ITerminal terminal, IMessages messages)
    {
        string size = run.OutputBytes >= 1024 * 1024
            ? Number(run.OutputBytes / 1024.0 / 1024.0, "F2") + " MiB"
            : Number(run.OutputBytes / 1024.0, "F0") + " KiB";

        terminal.Line();
        Field(terminal, messages, "encode.result.written", run.OutputPath);
        Field(terminal, messages, "encode.result.size", messages.Format("encode.result.sizeValue", new
        {
            size,
            kbps = Number(run.AverageKbps, "F0"),
            seconds = Number(run.SpanSeconds, "G4"),
        }));
        Field(terminal, messages, "encode.result.pacing", messages.Format("encode.result.pacingValue", new
        {
            seconds = Number(run.Pump.Seconds, "F2"),
            frames = run.Pump.Frames,
            fps = Number(run.Pump.EffectiveFps, "F2"),
            fresh = run.Pump.Fresh,
            repeated = run.Pump.Repeated,
        }));

        // Two different things, said separately.
        //
        // The ladder moving is a decision the program made: it saw the bitrate
        // over the ceiling and slowed the picture down. Frames skipped because
        // the encoder was still starting is not a decision, it is the queue
        // absorbing startup -- and reporting both with one word ("motion") told
        // the user the ladder had throttled a source it never touched. Found by
        // the end-to-end case that encodes a static picture and asserts the
        // report says nothing about motion.
        if (run.Pump.FinalRung > 0)
        {
            Field(terminal, messages, "encode.result.motion", messages.Format("encode.result.motionValue", new
            {
                fps = Number(run.Pump.MotionFps, "G4"),
                rung = messages.Format("encode.result.motionRung", new { rung = run.Pump.FinalRung }),
                dropped = run.Pump.Dropped,
            }));
        }
        else if (run.Pump.Dropped > 0)
        {
            Field(terminal, messages, "encode.result.skipped", messages.Format("encode.result.skippedValue", new
            {
                dropped = run.Pump.Dropped,
            }));
        }
    }

    /// <summary>
    /// Renders a command line the way it would be typed. A dry run exists to be
    /// pasted into a shell and tried, so arguments with spaces get quoted -- and
    /// an argument that already contains a double quote gets the inner ones
    /// escaped, or the pasted command would be truncated at the first one.
    /// </summary>
    private static string Quote(IReadOnlyList<string> command)
        => string.Join(' ', command.Select(a =>
            !a.Any(char.IsWhiteSpace) && !a.Contains('"')
                ? a
                : "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));
}
