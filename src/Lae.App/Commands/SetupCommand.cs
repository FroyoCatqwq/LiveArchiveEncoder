using System.CommandLine;
using Lae.App.Ui;
using Lae.Core.Localization;
using Lae.Core.Settings;
using Lae.Core.Toolchain;
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
    public static Command Build(ISettingsStore store, ITerminal terminal, IMessages messages)
    {
        var command = new Command("setup", messages["setup.desc"]);

        command.SetAction(_ =>
        {
            SettingsDocument settings = store.Load();
            string? directory = EncodeSession.ToolchainDirectory(settings, store.DataDirectory);
            ToolchainProbeResult probe = ToolchainProbe.Probe(directory);
            return SetupReport.Run(settings, store.Location, directory, probe, terminal, messages);
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
///
/// <para>The first four labels are left alone: they are field names, aligned into
/// a column, and a translated <c>ffmpeg</c> would no longer name the program the
/// user is being told to install.</para>
/// </summary>
public static class SetupReport
{
    public static int Run(
        SettingsDocument settings, string settingsPath, string? directory, ToolchainProbeResult probe,
        ITerminal terminal, IMessages messages)
    {
        terminal.Line($"Settings   {settingsPath}");

        // The configured source wins over PATH, because that is what an encode
        // will use; reporting a PATH copy that the run ignores would be worse
        // than saying nothing.
        terminal.Line($"Source     {settings.Toolchain.FfmpegSource}"
            + (directory is null ? "" : $" ({directory})"));
        terminal.Line($"ffmpeg     {probe.Ffmpeg ?? messages["setup.notFound"]}");
        terminal.Line($"ffprobe    {probe.Ffprobe ?? messages["setup.notFound"]}");

        if (probe.Complete)
        {
            terminal.Line();
            terminal.Line(messages["setup.ready"]);
            return ExitCodes.Ok;
        }

        terminal.Line();
        terminal.Line(messages["setup.missing"]);
        foreach (string line in probe.Missing()) terminal.Line($"  - {line}");

        // "Nothing is installed" and "you have no FFmpeg" are different problems
        // with different fixes, and only this line tells them apart: the source is
        // named in the settings, the machine just does not have it yet.
        if (directory is null
            && settings.Toolchain.FfmpegPath is not { Length: > 0 }
            && FfmpegCatalog.Find(settings.Toolchain.FfmpegSource) is { } dist)
        {
            terminal.Line();
            terminal.Line(messages.Format("setup.sourceNotInstalled", new { id = dist.Id }));
        }

        // The advice is one sentence broken over two lines in the resource file:
        // the break belongs to the layout, not to the sentence, so it is kept
        // where a translator can move it.
        terminal.Line();
        foreach (string line in messages["setup.fix"].Split('\n')) terminal.Line(line);
        terminal.Line();
        foreach (string line in messages["setup.noDownload"].Split('\n')) terminal.Line(line);

        return ExitCodes.Failed;
    }
}
