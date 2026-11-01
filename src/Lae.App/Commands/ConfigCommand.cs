using System.CommandLine;
using Lae.App.Ui;
using Lae.Core.Localization;
using Lae.Core.Settings;
using Lae.Media.Settings;

namespace Lae.App.Commands;

/// <summary>
/// <c>lae config</c> — read, locate and migrate the settings file.
///
/// <para>This replaces v7's <c>--save-config</c> / <c>--emit-args</c> pair, which
/// printed a JSON document to stdout and left the user to work out where it was
/// supposed to go. <c>path</c> answers "where is it", <c>show</c> answers "what
/// is in it", <c>migrate</c> answers "what happened to my v7 file".</para>
///
/// <para><b>The keys are not translated.</b> <c>defaults.encoder</c> is the name
/// the TOML file uses and the name <c>grep</c> looks for; showing a Chinese label
/// there would break the one property this output has. Only the remarks around
/// them are translated.</para>
/// </summary>
public static class ConfigCommand
{
    public static Command Build(ISettingsStore store, ITerminal terminal, IMessages messages)
    {
        var command = new Command("config", messages["config.desc"]);
        command.Subcommands.Add(PathCommand(store, terminal, messages));
        command.Subcommands.Add(ShowCommand(store, terminal, messages));
        command.Subcommands.Add(MigrateCommand(store, terminal, messages));
        return command;
    }

    private static Command PathCommand(ISettingsStore store, ITerminal terminal, IMessages messages)
    {
        var command = new Command("path", messages["config.path.desc"]);
        command.SetAction(_ =>
        {
            terminal.Line(store.Location);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command ShowCommand(ISettingsStore store, ITerminal terminal, IMessages messages)
    {
        var command = new Command("show", messages["config.show.desc"]);

        var tomlOption = new Option<bool>("--toml")
        {
            Description = messages["config.show.toml"],
        };
        command.Options.Add(tomlOption);

        command.SetAction((ParseResult parseResult) =>
        {
            SettingsDocument document = store.Load();

            if (parseResult.GetValue(tomlOption))
            {
                // The point of this switch is "give me a file I could save": it is
                // the same renderer the store uses for a new file, so what is
                // printed is what the program would have written.
                terminal.Line(TomlSettingsStore.Render(document));
                return ExitCodes.Ok;
            }

            // One flat list rather than nested blocks: this output is meant to be
            // greppable, and `lae config show | grep quality` is a more likely
            // question than `| jq .defaults.quality`.
            Emit(terminal, messages, "schemaVersion", document.SchemaVersion);
            Emit(terminal, messages, "toolchain.ffmpegSource", document.Toolchain.FfmpegSource);
            Emit(terminal, messages, "toolchain.ffmpegPath", document.Toolchain.FfmpegPath);
            Emit(terminal, messages, "toolchain.nnediWeights", document.Toolchain.NnediWeights);
            Emit(terminal, messages, "network.proxy", document.Network.Proxy);
            Emit(terminal, messages, "network.githubFront", document.Network.GithubFront);
            Emit(terminal, messages, "ui.language", document.Ui.Language);
            Emit(terminal, messages, "ui.playMode", document.Ui.PlayMode);
            Emit(terminal, messages, "ui.playerPath", document.Ui.PlayerPath);
            Emit(terminal, messages, "ui.checkForUpdates", document.Ui.CheckForUpdates);

            EncodeDefaults d = document.Defaults;
            Emit(terminal, messages, "defaults.encoder", d.Encoder);
            Emit(terminal, messages, "defaults.container", d.Container);
            Emit(terminal, messages, "defaults.audioEncoder", d.AudioEncoder);
            Emit(terminal, messages, "defaults.fps", d.Fps);
            Emit(terminal, messages, "defaults.fpsLevels", string.Join(',', d.FpsLevels));
            Emit(terminal, messages, "defaults.limitKbps", d.LimitKbps);
            Emit(terminal, messages, "defaults.recoverKbps", d.RecoverKbps);
            Emit(terminal, messages, "defaults.windowSeconds", d.WindowSeconds);
            Emit(terminal, messages, "defaults.quality", d.Quality);
            Emit(terminal, messages, "defaults.threads", d.Threads);
            Emit(terminal, messages, "defaults.bufferFrames", d.BufferFrames);
            Emit(terminal, messages, "defaults.audioKbps", d.AudioKbps);
            Emit(terminal, messages, "defaults.audioChannels", d.AudioChannels);
            Emit(terminal, messages, "defaults.audioRate", d.AudioRate);
            Emit(terminal, messages, "defaults.size", $"{d.Width}x{d.Height}");
            Emit(terminal, messages, "defaults.fitMode", d.FitMode);
            Emit(terminal, messages, "defaults.cropAnchor", d.CropAnchor);
            Emit(terminal, messages, "defaults.padColor", d.PadColor);
            Emit(terminal, messages, "defaults.scaleAlgo", d.ScaleAlgo);
            Emit(terminal, messages, "defaults.pixFmt", d.PixFmt);
            Emit(terminal, messages, "defaults.dither", d.Dither);
            Emit(terminal, messages, "defaults.deinterlace", d.Deinterlace.Filter);
            Emit(terminal, messages, "defaults.hwaccel", d.Hwaccel);
            Emit(terminal, messages, "defaults.parallel", d.Parallel);
            Emit(terminal, messages, "defaults.subtitles.mode", d.Subtitles.Mode);
            Emit(terminal, messages, "defaults.subtitles.files", d.Subtitles.Files.Count);
            Emit(terminal, messages, "defaults.recompress.enabled", d.Recompress.Enabled);
            Emit(terminal, messages, "defaults.recompress.passes", d.Recompress.Active().Count);
            Emit(terminal, messages, "customPresets", document.CustomPresets.Count);
            Emit(terminal, messages, "plugins", document.Plugins.Count);

            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command MigrateCommand(ISettingsStore store, ITerminal terminal, IMessages messages)
    {
        var command = new Command("migrate", messages["config.migrate.desc"]);

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = messages["config.migrate.dryRun"],
        };
        command.Options.Add(dryRunOption);

        command.SetAction((ParseResult parseResult) =>
        {
            // Migration only means anything for the file-backed store; an
            // in-memory store has nothing older to convert.
            if (store is not TomlSettingsStore files)
            {
                terminal.ErrorLine(messages["config.migrate.noFileStore"]);
                return ExitCodes.Failed;
            }

            SettingsLocation location = SettingsLocation.Resolve();
            if (!File.Exists(location.LegacyFile))
            {
                // The v8 JSON file needs no command: the store converts it on the
                // first read, because leaving it unread would silently reset the
                // user's settings. Saying so here is cheaper than a second code
                // path that could disagree with the store.
                terminal.Line(File.Exists(location.PreviousFile)
                    ? messages.Format("config.migrate.previousAuto", new { path = location.PreviousFile })
                    : messages.Format("config.migrate.nothing", new { path = location.LegacyFile }));
                return ExitCodes.Ok;
            }

            bool dryRun = parseResult.GetValue(dryRunOption);
            if (!dryRun)
            {
                // Load() performs the conversion, writes the new file and records
                // the notes; asking it to do the work here keeps one code path for
                // "read a v7 file", which is the only way the two cannot drift.
                _ = files.Load();
                foreach (string note in files.MigrationNotes) terminal.Line($"  {note}");

                // The write can fail -- a read-only profile, a full disk -- and
                // Load() reports that in its notes rather than throwing. Claiming
                // "Wrote" over a refusal is the one thing a migration command
                // must not do: the user would delete the v7 file.
                if (!File.Exists(location.File))
                {
                    terminal.ErrorLine(messages.Format("config.migrate.notWritten", new { path = location.File }));
                    return ExitCodes.Failed;
                }

                terminal.Line(messages.Format("config.migrate.wrote", new { path = location.File }));
                return ExitCodes.Ok;
            }

            string json = File.ReadAllText(location.LegacyFile);
            if (!LegacyV7Converter.LooksLikeV7(json))
            {
                terminal.ErrorLine(messages.Format("config.migrate.notV7", new { path = location.LegacyFile }));
                return ExitCodes.Usage;
            }

            if (LegacyV7Converter.Parse(json) is not { } old)
            {
                terminal.ErrorLine(messages.Format("config.migrate.unparsable", new { path = location.LegacyFile }));
                return ExitCodes.Usage;
            }

            MigrationOutcome outcome = LegacyV7Converter.Convert(old, messages);
            terminal.Line(messages.Format(
                "config.migrate.wouldWrite",
                new { path = location.File, schema = outcome.Document.SchemaVersion }));
            foreach (string note in outcome.Notes) terminal.Line($"  {note}");
            terminal.Line(messages.Format(
                "config.migrate.presetsCarried", new { count = outcome.Document.CustomPresets.Count }));
            return ExitCodes.Ok;
        });

        return command;
    }

    private static void Emit(ITerminal terminal, IMessages messages, string key, object? value)
        => terminal.Line($"{key} = {value?.ToString() ?? messages["config.unset"]}");
}
