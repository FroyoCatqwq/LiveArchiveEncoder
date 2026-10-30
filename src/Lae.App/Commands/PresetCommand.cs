using System.CommandLine;
using System.Globalization;
using Lae.App.Ui;
using Lae.Core;
using Lae.Core.Presets;
using Lae.Core.Settings;

namespace Lae.App.Commands;

/// <summary>
/// <c>lae preset</c> — the user's own presets, and the only ones there are.
///
/// <para>v8 deliberately ships no catalogue. v7 had 32 built-in rows and a
/// <c>--era</c> option that took one of their ids; the rows were the part of the
/// program that aged worst, and a user who disagreed with one of them had no way
/// to say so. Here a preset is a name the user gives to a bundle of settings,
/// created with <c>preset add</c>, used with <c>encode --preset</c>, and deleted
/// with <c>preset remove</c>.</para>
///
/// <para>Every subcommand prints its own answer and returns an exit code; the
/// store is written only when something actually changed, so a listing can never
/// create a settings file as a side effect.</para>
/// </summary>
public static class PresetCommand
{
    public static Command Build(ISettingsStore store, ITerminal terminal)
    {
        var command = new Command("preset", "List, inspect, add or remove your own encode presets.");
        command.Subcommands.Add(ListCommand(store, terminal));
        command.Subcommands.Add(ShowCommand(store, terminal));
        command.Subcommands.Add(AddCommand(store, terminal));
        command.Subcommands.Add(RemoveCommand(store, terminal));
        return command;
    }

    private static Command ListCommand(ISettingsStore store, ITerminal terminal)
    {
        var command = new Command("list", "Print every stored preset.");

        command.SetAction(_ =>
        {
            IReadOnlyList<CustomPreset> presets = PresetCatalog.All(store.Load());
            if (presets.Count == 0)
            {
                terminal.Line("no presets; 'lae preset add <id> --encoder mpeg4 --size 320x240' makes one");
                return ExitCodes.Ok;
            }

            // Padded by column so a copy-paste of the first field is the id.
            int idWidth = presets.Max(p => p.Id.Length);
            foreach (CustomPreset preset in presets)
            {
                string label = preset.Label is { Length: > 0 } text ? text : "";
                terminal.Line($"{preset.Id.PadRight(idWidth)}  {label}".TrimEnd()
                            + $"  {PresetCatalog.Describe(preset)}");
            }

            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command ShowCommand(ISettingsStore store, ITerminal terminal)
    {
        var idArgument = new Argument<string>("id") { Description = "Preset id." };

        var command = new Command("show", "Print one preset, field by field.") { idArgument };

        command.SetAction((ParseResult parseResult) =>
        {
            SettingsDocument document = store.Load();
            if (PresetCatalog.Find(document, parseResult.GetValue(idArgument)) is not { } preset)
            {
                terminal.ErrorLine($"ERROR: there is no preset named '{parseResult.GetValue(idArgument)}'.");
                terminal.ErrorLine("Run 'lae preset list' to see the ones you have.");
                return ExitCodes.Usage;
            }

            terminal.Line($"id         {preset.Id}");
            if (preset.Label is { Length: > 0 } label) terminal.Line($"label      {label}");
            if (preset.Note is { Length: > 0 } note) terminal.Line($"note       {note}");

            // Only the fields the preset actually carries: printing a wall of
            // blanks would suggest it overrides everything.
            Emit(terminal, "encoder", preset.Encoder);
            Emit(terminal, "container", preset.Container);
            if (preset.Width is { } w && preset.Height is { } h) terminal.Line($"size       {w}x{h}");
            if (preset.Fps is { } fps) Emit(terminal, "fps", Number(fps));
            if (preset.FpsLevels is { Count: > 0 } levels)
            {
                terminal.Line("ladder     " + string.Join(" > ", levels.Select(Number)) + " fps");
            }

            if (preset.LimitKbps is { } limit) Emit(terminal, "ceiling", Number(limit) + " kbps");
            if (preset.WindowSeconds is { } window) Emit(terminal, "window", Number(window) + " s");
            if (preset.BufferFrames is { } buffer) Emit(terminal, "buffer", buffer.ToString(CultureInfo.InvariantCulture) + " frames");
            if (preset.LiveQv is { } qv && preset.LiveCrf is { } crf)
            {
                // Both are stored so the preset can be applied to either kind of
                // encoder without a second question; showing both is also the
                // reminder that they are different pictures.
                Emit(terminal, "quality", $"q:v {qv} (crf {crf})");
            }
            else if (preset.LiveQv is { } onlyQv)
            {
                Emit(terminal, "quality", $"q:v {onlyQv}");
            }
            else if (preset.LiveCrf is { } onlyCrf)
            {
                Emit(terminal, "quality", $"crf {onlyCrf}");
            }
            Emit(terminal, "audio", preset.AudioEncoder);
            if (preset.AudioKbps is { } audioKbps) Emit(terminal, "audioKbps", audioKbps.ToString(CultureInfo.InvariantCulture));
            Emit(terminal, "deinterlace", preset.Deinterlace);
            Emit(terminal, "scale", preset.ScaleAlgo);

            // What it would change if applied right now -- which is the question
            // the values above are really about.
            EncodeDefaults applied = PresetCatalog.Apply(preset, document.Defaults);
            terminal.Line();
            terminal.Line($"applies    {applied.Encoder ?? "mpeg4"} {applied.Width}x{applied.Height}"
                        + $" {Number(applied.Fps)} fps, ceiling {Number(applied.LimitKbps)} kbps,"
                        + $" quality {applied.Quality}");

            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command AddCommand(ISettingsStore store, ITerminal terminal)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Id for the preset: letters, digits, '-', '_' or '.'.",
        };

        var labelOption = new Option<string?>("--label") { Description = "Human name to show in a list." };
        var noteOption = new Option<string?>("--note") { Description = "Free-form reminder." };
        var encoderOption = new Option<string?>("--encoder") { Description = "Video encoder, e.g. mpeg4." };
        var containerOption = new Option<string?>("--container") { Description = "Container extension, e.g. avi." };
        var sizeOption = new Option<string?>("--size") { Description = "Frame size as WIDTHxHEIGHT." };
        var fpsOption = new Option<double?>("--fps") { Description = "Base frame rate." };
        var limitOption = new Option<double?>("--limit") { Description = "Bitrate ceiling in kbps." };
        var windowOption = new Option<double?>("--window") { Description = "Bitrate averaging window in seconds." };
        var qualityOption = new Option<int?>("--quality")
        {
            Description = "Quality in the encoder's own scale (2..31 for the mpeg family, 0..51 for x264).",
        };
        var keepOption = new Option<bool>("--keep")
        {
            Description = "Fill every unspecified field from the current defaults instead of leaving it open.",
        };

        var command = new Command("add", "Store a preset, or replace the one with this id.")
        {
            idArgument, labelOption, noteOption, encoderOption, containerOption,
            sizeOption, fpsOption, limitOption, windowOption, qualityOption, keepOption,
        };

        command.SetAction((ParseResult parseResult) =>
        {
            SettingsDocument document = store.Load();

            string? encoder = parseResult.GetValue(encoderOption) ?? document.Defaults.Encoder ?? "mpeg4";

            int? width = null;
            int? height = null;
            if (parseResult.GetValue(sizeOption) is { } size)
            {
                if (!EncodeSession.TryParseSize(size, out int w, out int h))
                {
                    terminal.ErrorLine($"ERROR: --size wants WIDTHxHEIGHT, got '{size}'");
                    return ExitCodes.Usage;
                }

                width = w;
                height = h;
            }

            int? quality = parseResult.GetValue(qualityOption);
            (int qv, int crf) = quality is { } value
                ? EncoderMath.QualityPair(encoder, value)
                : (0, 0);

            // --keep is the "freeze what I have now" switch: a preset captured
            // from the current defaults is reproducible, one made of only three
            // overrides follows every later change to the defaults. Both are
            // legitimate, so the choice is explicit rather than guessed.
            bool keep = parseResult.GetValue(keepOption);
            EncodeDefaults now = document.Defaults;

            CustomPreset preset = new()
            {
                Id = parseResult.GetValue(idArgument)!,
                Label = parseResult.GetValue(labelOption),
                Note = parseResult.GetValue(noteOption),
                Encoder = parseResult.GetValue(encoderOption) ?? (keep ? now.Encoder : null),
                Container = parseResult.GetValue(containerOption) ?? (keep ? now.Container : null),
                Width = width ?? (keep ? now.Width : null),
                Height = height ?? (keep ? now.Height : null),
                Fps = parseResult.GetValue(fpsOption) ?? (keep ? now.Fps : null),
                LimitKbps = parseResult.GetValue(limitOption) ?? (keep ? now.LimitKbps : null),
                WindowSeconds = parseResult.GetValue(windowOption) ?? (keep ? now.WindowSeconds : null),
                LiveQv = quality is null ? null : qv,
                LiveCrf = quality is null ? null : crf,
            };

            PresetChange change = PresetCatalog.Put(document, preset);
            if (!change.Ok)
            {
                terminal.ErrorLine($"ERROR: {change.Error}");
                return ExitCodes.Usage;
            }

            if (Persist(store, terminal, change.Document!) is { } failed) return failed;

            terminal.Line($"stored {PresetCatalog.NormaliseId(preset.Id)}: {PresetCatalog.Describe(preset)}");
            terminal.Line($"use it with: lae encode <source> --preset {PresetCatalog.NormaliseId(preset.Id)}");
            return ExitCodes.Ok;
        });

        return command;
    }

    private static Command RemoveCommand(ISettingsStore store, ITerminal terminal)
    {
        var idArgument = new Argument<string>("id") { Description = "Preset id." };
        var command = new Command("remove", "Delete a preset.") { idArgument };

        command.SetAction((ParseResult parseResult) =>
        {
            SettingsDocument document = store.Load();
            PresetChange change = PresetCatalog.Remove(document, parseResult.GetValue(idArgument));
            if (!change.Ok)
            {
                terminal.ErrorLine($"ERROR: {change.Error}");
                return ExitCodes.Usage;
            }

            if (Persist(store, terminal, change.Document!) is { } failed) return failed;

            terminal.Line($"removed {parseResult.GetValue(idArgument)}");
            return ExitCodes.Ok;
        });

        return command;
    }

    /// <summary>
    /// Write the document, or say why not. Null means it landed.
    ///
    /// <para>A failed write that still printed "stored" would be the worst of
    /// both worlds: the user believes the preset exists, and the next run has
    /// never heard of it.</para>
    /// </summary>
    private static int? Persist(ISettingsStore store, ITerminal terminal, SettingsDocument document)
    {
        if (store.TrySave(document) is not { } error) return null;

        terminal.ErrorLine($"ERROR: could not write {store.Location}: {error}");
        return ExitCodes.Failed;
    }

    private static string Number(double value) => value.ToString("G6", CultureInfo.InvariantCulture);

    private static void Emit(ITerminal terminal, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) terminal.Line($"{key,-10} {value}");
    }
}
