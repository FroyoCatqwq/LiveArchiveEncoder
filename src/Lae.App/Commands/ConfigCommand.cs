using System.CommandLine;
using System.Text.Json;
using Lae.App.Ui;
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
/// </summary>
public static class ConfigCommand
{
    public static Command Build(ISettingsStore store, ITerminal terminal)
    {
        var command = new Command("config", "Show, locate or migrate the settings file.");
        command.Subcommands.Add(PathCommand(store, terminal));
        command.Subcommands.Add(ShowCommand(store, terminal));
        command.Subcommands.Add(MigrateCommand(store, terminal));
        return command;
    }

    private static Command PathCommand(ISettingsStore store, ITerminal terminal)
    {
        var command = new Command("path", "Print the settings file path and exit.");
        command.SetAction(_ =>
        {
            terminal.Line(store.Location);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command ShowCommand(ISettingsStore store, ITerminal terminal)
    {
        var command = new Command("show", "Print the effective settings as key = value lines.");

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Print the stored document as JSON instead.",
        };
        command.Options.Add(jsonOption);

        command.SetAction((ParseResult parseResult) =>
        {
            SettingsDocument document = store.Load();

            if (parseResult.GetValue(jsonOption))
            {
                terminal.Line(JsonSerializer.Serialize(
                    document, SettingsJsonContext.Default.SettingsDocument));
                return ExitCodes.Ok;
            }

            // One flat list rather than nested blocks: this output is meant to be
            // greppable, and `lae config show | grep quality` is a more likely
            // question than `| jq .defaults.quality`.
            Emit(terminal, "schemaVersion", document.SchemaVersion);
            Emit(terminal, "toolchain.ffmpegSource", document.Toolchain.FfmpegSource);
            Emit(terminal, "toolchain.ffmpegPath", document.Toolchain.FfmpegPath);
            Emit(terminal, "toolchain.nnediWeights", document.Toolchain.NnediWeights);
            Emit(terminal, "network.proxy", document.Network.Proxy);
            Emit(terminal, "network.githubFront", document.Network.GithubFront);
            Emit(terminal, "ui.language", document.Ui.Language);
            Emit(terminal, "ui.playMode", document.Ui.PlayMode);
            Emit(terminal, "ui.playerPath", document.Ui.PlayerPath);
            Emit(terminal, "ui.checkForUpdates", document.Ui.CheckForUpdates);

            EncodeDefaults d = document.Defaults;
            Emit(terminal, "defaults.encoder", d.Encoder);
            Emit(terminal, "defaults.container", d.Container);
            Emit(terminal, "defaults.audioEncoder", d.AudioEncoder);
            Emit(terminal, "defaults.fps", d.Fps);
            Emit(terminal, "defaults.fpsLevels", string.Join(',', d.FpsLevels));
            Emit(terminal, "defaults.limitKbps", d.LimitKbps);
            Emit(terminal, "defaults.recoverKbps", d.RecoverKbps);
            Emit(terminal, "defaults.windowSeconds", d.WindowSeconds);
            Emit(terminal, "defaults.quality", d.Quality);
            Emit(terminal, "defaults.threads", d.Threads);
            Emit(terminal, "defaults.bufferFrames", d.BufferFrames);
            Emit(terminal, "defaults.audioKbps", d.AudioKbps);
            Emit(terminal, "defaults.audioChannels", d.AudioChannels);
            Emit(terminal, "defaults.audioRate", d.AudioRate);
            Emit(terminal, "defaults.size", $"{d.Width}x{d.Height}");
            Emit(terminal, "defaults.fitMode", d.FitMode);
            Emit(terminal, "defaults.cropAnchor", d.CropAnchor);
            Emit(terminal, "defaults.padColor", d.PadColor);
            Emit(terminal, "defaults.scaleAlgo", d.ScaleAlgo);
            Emit(terminal, "defaults.pixFmt", d.PixFmt);
            Emit(terminal, "defaults.dither", d.Dither);
            Emit(terminal, "defaults.deinterlace", d.Deinterlace.Filter);
            Emit(terminal, "defaults.hwaccel", d.Hwaccel);
            Emit(terminal, "defaults.parallel", d.Parallel);
            Emit(terminal, "defaults.windowSeconds", d.WindowSeconds);
            Emit(terminal, "defaults.bufferFrames", d.BufferFrames);
            Emit(terminal, "defaults.subtitles.mode", d.Subtitles.Mode);
            Emit(terminal, "defaults.subtitles.files", d.Subtitles.Files.Count);
            Emit(terminal, "defaults.recompress.enabled", d.Recompress.Enabled);
            Emit(terminal, "defaults.recompress.passes", d.Recompress.Active().Count);
            Emit(terminal, "customPresets", document.CustomPresets.Count);
            Emit(terminal, "plugins", document.Plugins.Count);

            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command MigrateCommand(ISettingsStore store, ITerminal terminal)
    {
        var command = new Command("migrate", "Convert a v7 config.json into a v8 settings.json.");

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Report what would change without writing anything.",
        };
        command.Options.Add(dryRunOption);

        command.SetAction((ParseResult parseResult) =>
        {
            // Migration only means anything for the file-backed store; an
            // in-memory store has nothing older to convert.
            if (store is not FileSettingsStore files)
            {
                terminal.ErrorLine("ERROR: this build has no settings file to migrate.");
                return ExitCodes.Failed;
            }

            SettingsLocation location = SettingsLocation.Resolve();
            if (!File.Exists(location.LegacyFile))
            {
                terminal.Line($"No v7 settings found at {location.LegacyFile}; nothing to migrate.");
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
                terminal.Line($"Wrote {location.File}");
                return ExitCodes.Ok;
            }

            string json = File.ReadAllText(location.LegacyFile);
            if (!LegacyV7Converter.LooksLikeV7(json))
            {
                terminal.ErrorLine($"ERROR: {location.LegacyFile} is not a v7 settings file.");
                return ExitCodes.Usage;
            }

            if (LegacyV7Converter.Parse(json) is not { } old)
            {
                terminal.ErrorLine($"ERROR: {location.LegacyFile} could not be parsed.");
                return ExitCodes.Usage;
            }

            MigrationOutcome outcome = LegacyV7Converter.Convert(old);
            terminal.Line($"Would write {location.File} (schema {outcome.Document.SchemaVersion})");
            foreach (string note in outcome.Notes) terminal.Line($"  {note}");
            terminal.Line($"Custom presets carried over: {outcome.Document.CustomPresets.Count}");
            return ExitCodes.Ok;
        });

        return command;
    }

    private static void Emit(ITerminal terminal, string key, object? value)
        => terminal.Line($"{key} = {value?.ToString() ?? "(unset)"}");
}
