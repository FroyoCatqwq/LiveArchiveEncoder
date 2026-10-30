using System.Globalization;
using Lae.App.Ui;
using Lae.Core;
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
/// </summary>
public static partial class EncodeSession
{
    /// <summary>The plan, in the terms the user asked the question in.</summary>
    private static void PrintPlan(EncodePlan plan, SourceInfo source, string ffmpeg, ITerminal terminal)
    {
        terminal.Line($"source     {source.Width}x{source.Height} {source.PixelFormat}"
                    + $", {source.DurationSeconds:G6}s"
                    + (source.HasAudio ? $", audio {source.AudioRate}Hz x{source.AudioChannels}" : ", no audio"));
        terminal.Line($"output     {plan.Width}x{plan.Height} {plan.TransportPixelFormat}"
                    + $", {plan.Container} ({plan.Muxer}), {plan.Fps:G6} fps");
        terminal.Line($"span       {plan.SpanSeconds:G6}s -> {plan.TargetFrames} frames");

        // The quality number is the one setting whose meaning changes with the
        // encoder -- the same 8 is "quite good" in the mpeg family and "nearly
        // lossless" in x264 -- so a dry run names the flag and the scale rather
        // than a bare number the reader has to interpret.
        string quality = EncoderMath.ScaleOf(plan.Encode.Encoder) switch
        {
            EncoderMath.QualityScale.Crf => $"crf {plan.Encode.Quality}",
            EncoderMath.QualityScale.Bitrate => "no quality knob; the ceiling is the only dial",
            _ => $"q:v {plan.Encode.Quality}",
        };
        terminal.Line($"quality    {quality}");

        // The ladder is the one part of the plan the user cannot see in the
        // command lines -- it changes what goes down the pipe, not what FFmpeg is
        // told -- so a dry run has to name it or the two commands are not the
        // whole story.
        if (plan.BuildLadder() is { } ladder)
        {
            terminal.Line($"ladder     {string.Join(" > ", ladder.Rungs.Select(r => r.Fps.ToString("G4", CultureInfo.InvariantCulture)))} fps"
                        + $", down at {plan.Encode.LimitKbps:G4} kbps"
                        + $", up at {LadderPolicy.RecoverKbps(plan.Encode):G4} kbps"
                        + $", over {plan.Encode.WindowSeconds:G4}s");
        }
        else
        {
            terminal.Line("ladder     off (single rung or no bitrate ceiling)");
        }

        terminal.Line($"frame      {plan.FrameSize} bytes on the pipe");
        terminal.Line();
        terminal.Line("decoder:");
        terminal.Line("  " + Quote(plan.DecodeCommand(ffmpeg)));
        terminal.Line("encoder:");
        terminal.Line("  " + Quote(plan.EncodeCommand(ffmpeg)));
    }

    /// <summary>
    /// The post-encode hops, one command line each. A dry run exists to be
    /// pasted into a shell, so the chain has to be in it: a plan that shows the
    /// capture and hides three transcodes is not the plan.
    /// </summary>
    private static void PrintChain(RecompressPlan plan, string ffmpeg, ITerminal terminal)
    {
        terminal.Line();
        terminal.Line("recompress:");
        foreach (RecompressStep step in plan.Steps)
            terminal.Line($"  {Quote(step.Command(ffmpeg, step.OutputPath))}");
    }

    /// <summary>
    /// What came out, in the terms the archive cares about: how long it ran
    /// against the span it covered, and what bitrate the result actually is.
    /// </summary>
    private static void PrintResult(LiveRun run, ITerminal terminal)
    {
        string size = run.OutputBytes >= 1024 * 1024
            ? $"{run.OutputBytes / 1024.0 / 1024.0:F2} MiB"
            : $"{run.OutputBytes / 1024.0:F0} KiB";

        terminal.Line();
        terminal.Line($"written    {run.OutputPath}");
        terminal.Line($"size       {size}, {run.AverageKbps:F0} kbps over {run.SpanSeconds:G4}s");
        terminal.Line($"pacing     {run.Pump.Frames} slots in {run.Pump.Seconds:F2}s "
                    + $"({run.Pump.EffectiveFps:F2} fps), "
                    + $"fresh {run.Pump.Fresh}, repeated {run.Pump.Repeated}");

        // Only worth a line when it actually happened: a run that stayed on the
        // top rung is the normal case, and saying so on every encode is noise.
        if (run.Pump.FinalRung > 0 || run.Pump.Dropped > 0)
        {
            terminal.Line($"motion     ended at {run.Pump.MotionFps:G4} fps"
                        + (run.Pump.FinalRung > 0 ? $" (rung {run.Pump.FinalRung})" : "")
                        + $", {run.Pump.Dropped} decoded frames dropped");
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
