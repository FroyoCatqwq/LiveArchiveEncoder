using System.CommandLine;
using System.Text.Json;
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
    public static Command Build(ISettingsStore store)
    {
        var command = new Command("config", "Show, locate or migrate the settings file.");
        command.Subcommands.Add(PathCommand(store));
        command.Subcommands.Add(ShowCommand(store));
        command.Subcommands.Add(MigrateCommand(store));
        return command;
    }

    private static Command PathCommand(ISettingsStore store)
    {
        var command = new Command("path", "Print the settings file path and exit.");
        command.SetAction(_ =>
        {
            Console.WriteLine(store.Location);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command ShowCommand(ISettingsStore store)
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
                Console.WriteLine(JsonSerializer.Serialize(document, SettingsJsonContext.Default.SettingsDocument));
                return ExitCodes.Ok;
            }

            // One flat list rather than nested blocks: this output is meant to be
            // greppable, and `lae config show | grep quality` is a more likely
            // question than `| jq .defaults.quality`.
            Emit("schemaVersion", document.SchemaVersion);
            Emit("toolchain.ffmpegSource", document.Toolchain.FfmpegSource);
            Emit("toolchain.ffmpegPath", document.Toolchain.FfmpegPath);
            Emit("toolchain.nnediWeights", document.Toolchain.NnediWeights);
            Emit("network.proxy", document.Network.Proxy);
            Emit("network.githubFront", document.Network.GithubFront);
            Emit("ui.language", document.Ui.Language);
            Emit("ui.playMode", document.Ui.PlayMode);
            Emit("ui.playerPath", document.Ui.PlayerPath);
            Emit("ui.checkForUpdates", document.Ui.CheckForUpdates);

            EncodeDefaults d = document.Defaults;
            Emit("defaults.encoder", d.Encoder);
            Emit("defaults.container", d.Container);
            Emit("defaults.audioEncoder", d.AudioEncoder);
            Emit("defaults.fps", d.Fps);
            Emit("defaults.fpsLevels", string.Join(',', d.FpsLevels));
            Emit("defaults.limitKbps", d.LimitKbps);
            Emit("defaults.recoverKbps", d.RecoverKbps);
            Emit("defaults.windowSeconds", d.WindowSeconds);
            Emit("defaults.quality", d.Quality);
            Emit("defaults.threads", d.Threads);
            Emit("defaults.bufferFrames", d.BufferFrames);
            Emit("defaults.audioKbps", d.AudioKbps);
            Emit("defaults.audioChannels", d.AudioChannels);
            Emit("defaults.audioRate", d.AudioRate);
            Emit("defaults.size", $"{d.Width}x{d.Height}");
            Emit("defaults.fitMode", d.FitMode);
            Emit("defaults.cropAnchor", d.CropAnchor);
            Emit("defaults.padColor", d.PadColor);
            Emit("defaults.scaleAlgo", d.ScaleAlgo);
            Emit("defaults.pixFmt", d.PixFmt);
            Emit("defaults.dither", d.Dither);
            Emit("defaults.deinterlace", d.Deinterlace.Filter);
            Emit("defaults.hwaccel", d.Hwaccel);
            Emit("defaults.parallel", d.Parallel);
            Emit("defaults.lastEraPreset", d.LastEraPreset);
            Emit("defaults.subtitles.mode", d.Subtitles.Mode);
            Emit("defaults.subtitles.files", d.Subtitles.Files.Count);
            Emit("defaults.recompress.enabled", d.Recompress.Enabled);
            Emit("defaults.recompress.passes", d.Recompress.Active().Count);
            Emit("customPresets", document.CustomPresets.Count);
            Emit("plugins", document.Plugins.Count);

            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command MigrateCommand(ISettingsStore store)
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
                Console.Error.WriteLine("ERROR: this build has no settings file to migrate.");
                return ExitCodes.Failed;
            }

            SettingsLocation location = SettingsLocation.Resolve();
            if (!File.Exists(location.LegacyFile))
            {
                Console.WriteLine($"No v7 settings found at {location.LegacyFile}; nothing to migrate.");
                return ExitCodes.Ok;
            }

            bool dryRun = parseResult.GetValue(dryRunOption);
            if (!dryRun)
            {
                // Load() performs the conversion, writes the new file and records
                // the notes; asking it to do the work here keeps one code path for
                // "read a v7 file", which is the only way the two cannot drift.
                _ = files.Load();
                foreach (string note in files.MigrationNotes) Console.WriteLine($"  {note}");
                Console.WriteLine($"Wrote {location.File}");
                return ExitCodes.Ok;
            }

            string json = File.ReadAllText(location.LegacyFile);
            if (!LegacyV7Converter.LooksLikeV7(json))
            {
                Console.Error.WriteLine($"ERROR: {location.LegacyFile} is not a v7 settings file.");
                return ExitCodes.Usage;
            }

            if (LegacyV7Converter.Parse(json) is not { } old)
            {
                Console.Error.WriteLine($"ERROR: {location.LegacyFile} could not be parsed.");
                return ExitCodes.Usage;
            }

            MigrationOutcome outcome = LegacyV7Converter.Convert(old);
            Console.WriteLine($"Would write {location.File} (schema {outcome.Document.SchemaVersion})");
            foreach (string note in outcome.Notes) Console.WriteLine($"  {note}");
            Console.WriteLine($"Custom presets carried over: {outcome.Document.CustomPresets.Count}");
            return ExitCodes.Ok;
        });

        return command;
    }

    private static void Emit(string key, object? value)
        => Console.WriteLine($"{key} = {value?.ToString() ?? "(unset)"}");
}
