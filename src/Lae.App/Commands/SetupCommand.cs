using System.CommandLine;
using Lae.App.Ui;
using Lae.Core.Settings;
using Lae.Media.Toolchain;

namespace Lae.App.Commands;

/// <summary>
/// <c>lae setup</c> — say whether this machine can encode, and what to do if not.
///
/// <para>This exists because v8 dropped the interactive shell. v7's first run
/// noticed there was no FFmpeg and offered to fetch it; without that, a new
/// user's first encode dies with "cannot find ffmpeg" and no next step. v7.9.6.3
/// had already called that dead end out and fixed it, so losing it again would
/// be a regression dressed up as a simplification.</para>
///
/// <para>It reports and does not act. Downloading a build is
/// <c>lae toolchain install</c>, which is explicit; a command named "setup"
/// silently pulling 80 MB over someone's metered link is not a good default, and
/// a diagnostic that can fail halfway is not a diagnostic.</para>
/// </summary>
public static class SetupCommand
{
    public static Command Build(ISettingsStore store, ITerminal terminal)
    {
        var command = new Command(
            "setup",
            "Check that FFmpeg and ffprobe are available. Exits 1 when they are not.");

        command.SetAction(_ =>
        {
            SettingsDocument settings = store.Load();
            ToolchainProbeResult probe = ToolchainProbe.Probe(EncodeSession.ToolchainDirectory(settings));
            return SetupReport.Run(settings, store.Location, probe, terminal);
        });

        return command;
    }
}

/// <summary>
/// What <c>lae setup</c> says, given what the machine has.
///
/// <para>Separated from the command so the report can be asserted: this text is
/// the whole value of the command, and v7 shipped a version of it that pointed at
/// a menu entry that did not exist. A test with a probe result of "nothing found"
/// catches that class of mistake without needing a machine without FFmpeg.</para>
/// </summary>
public static class SetupReport
{
    public static int Run(
        SettingsDocument settings, string settingsPath, ToolchainProbeResult probe, ITerminal terminal)
    {
        terminal.Line($"Settings   {settingsPath}");

        // The configured source wins over PATH, because that is what an encode
        // will use; reporting a PATH copy that the run ignores would be worse
        // than saying nothing.
        string? directory = EncodeSession.ToolchainDirectory(settings);
        terminal.Line($"Source     {settings.Toolchain.FfmpegSource}"
            + (directory is null ? "" : $" ({directory})"));
        terminal.Line($"ffmpeg     {probe.Ffmpeg ?? "(not found)"}");
        terminal.Line($"ffprobe    {probe.Ffprobe ?? "(not found)"}");

        if (probe.Complete)
        {
            terminal.Line();
            terminal.Line("Ready. Try: lae encode <input> --dry-run");
            return ExitCodes.Ok;
        }

        terminal.Line();
        terminal.Line("Missing:");
        foreach (string line in probe.Missing()) terminal.Line($"  - {line}");

        terminal.Line();
        terminal.Line("To fix it: put ffmpeg and ffprobe on PATH (or point toolchain.ffmpegPath at");
        terminal.Line("a build), then run 'lae setup' again.");
        terminal.Line();
        terminal.Line("A download command is planned for the plugin milestone; this build does not");
        terminal.Line("fetch anything.");

        return ExitCodes.Failed;
    }
}
