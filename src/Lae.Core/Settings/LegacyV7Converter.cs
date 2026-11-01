using System.Text.Json;

namespace Lae.Core.Settings;

/// <summary>
/// The result of reading a v7 settings file.
/// </summary>
/// <param name="Document">The migrated document.</param>
/// <param name="Notes">
/// Everything the migration changed or deliberately dropped, in the user's
/// terms. Printed by <c>lae config migrate</c>: a migration that silently
/// discards half a file is worse than one that refuses.
/// </param>
public sealed record MigrationOutcome(SettingsDocument Document, IReadOnlyList<string> Notes);

/// <summary>
/// Turns a v7 settings file into a v8 document.
///
/// <para>v8 is allowed to be incompatible with v7, and it is: the shapes differ,
/// a few v7 fields move, and a few are dropped. It is not allowed to be
/// <em>silently</em> incompatible -- so every drop is reported, the original file
/// is left alone, and a copy is written beside it.</para>
///
/// <para>The four dropped fields are all per-run state that v7 persisted by
/// accident: <c>InputPath</c>, <c>OutputPath</c>, <c>StartTimeSeconds</c>,
/// <c>EndTimeSeconds</c>, <c>VideoStream</c> and <c>AudioStream</c>. Keeping
/// them made a scripted run inherit the previous session's trim range and stream
/// picks, which is exactly the class of surprise a command-line tool must not
/// have.</para>
/// </summary>
public static class LegacyV7Converter
{
    /// <summary>
    /// True when the text is a v7 file rather than a v8 one.
    ///
    /// <para>Detected by the presence of a <c>Version</c> member and the absence
    /// of <c>schemaVersion</c>. Both markers are checked: a v8 file starts with
    /// <c>schemaVersion</c>, and v7 always wrote <c>Version</c> -- so neither a
    /// truncated file nor a hand-written one can be mistaken for the other.</para>
    /// </summary>
    public static bool LooksLikeV7(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

            bool hasLegacyVersion = false;
            bool hasSchemaVersion = false;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "Version", StringComparison.Ordinal)) hasLegacyVersion = true;
                if (string.Equals(property.Name, "schemaVersion", StringComparison.Ordinal)) hasSchemaVersion = true;
            }

            return hasLegacyVersion && !hasSchemaVersion;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses the text as a v7 file. Returns null when it cannot be read at all;
    /// a caller must not treat that as "empty settings", because overwriting the
    /// file afterwards would destroy the only copy.
    /// </summary>
    public static LegacyV7Settings? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, LegacyV7JsonContext.Default.LegacyV7Settings);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps a v7 file onto a v8 document, reporting every deliberate change.
    ///
    /// <para>Forgiving in the same places v7 was: an unparsable ladder, an
    /// out-of-range number or a null list becomes the documented default instead
    /// of an exception. A settings file that refuses to load is worse than one
    /// that loads with a value repaired -- and the repair is listed in the
    /// notes.</para>
    ///
    /// <para><paramref name="messages"/> because the notes are read by the person
    /// whose file was migrated, at the moment they upgrade. An English list in a
    /// Chinese session is the one place where "it is only a warning" is not good
    /// enough: these sentences say what was thrown away.</para>
    /// </summary>
    public static MigrationOutcome Convert(LegacyV7Settings old, Localization.IMessages messages)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(messages);

        var notes = new List<string>();

        var defaults = new EncodeDefaults
        {
            Encoder = Clean(old.Encoder),
            Container = Clean(old.Container),
            AudioEncoder = Clean(old.AudioEncoder) ?? "libmp3lame",
            Fps = Positive(old.Fps, Constants.DefaultFps),
            FpsLevels = Ladder(old.FpsLevels, notes, messages),
            FpsThresholds = Thresholds(old.FpsLevelKbps),
            LimitKbps = Positive(old.Limit, 200.0),
            RecoverKbps = Positive(old.Recover, 150.0),
            WindowSeconds = Positive(old.Window, 2.0),
            Quality = Clamp(old.Qv, 2, 31, 8),
            Threads = Clamp(old.Threads, 1, 512, 16),
            BufferFrames = Clamp(old.BufferFrames, 2, 64, 6),
            AudioKbps = Clamp(old.AudioKbps, 8, 512, 32),
            AudioChannels = Clamp(old.AudioChannels, 1, 8, 1),
            AudioRate = old.AudioRate is > 0 ? old.AudioRate : null,
            Width = Positive(old.Width, Constants.DefaultWidth),
            Height = Positive(old.Height, Constants.DefaultHeight),
            FitMode = FitMode(old, notes, messages),
            CropAnchor = Clean(old.CropAnchor) ?? "center",
            PadColor = Clean(old.PadColor) ?? "black",
            ScaleAlgo = Clean(old.ScaleAlgo) ?? "neighbor",
            PixFmt = Clean(old.PixFmt),
            Color = new ColorTags
            {
                Range = Clean(old.ColorRange),
                Space = Clean(old.ColorSpace),
                Primaries = Clean(old.ColorPrimaries),
                Transfer = Clean(old.ColorTransfer),
            },
            Dither = Clean(old.Dither) ?? "auto",
            Deinterlace = new DeinterlaceSettings
            {
                Filter = Clean(old.Deinterlace) ?? "off",
                FieldMode = Clean(old.DeintMode) ?? "send_frame",
                Parity = Clean(old.DeintParity) ?? "auto",
                MarkedOnly = old.DeintMarkedOnly ?? false,
            },
            Hwaccel = Clean(old.Hwaccel) ?? "off",
            X264 = new X264Settings
            {
                Preset = Clean(old.X264Preset) ?? "medium",
                Tune = Clean(old.X264Tune) ?? "",
                Profile = Clean(old.X264Profile) ?? "",
                Level = Clean(old.X264Level) ?? "",
            },
            // Never restore a nonsensical shard count from an older file.
            Parallel = Math.Max(1, old.Parallel ?? 1),
            Subtitles = Subtitles(old.Subtitles),
            Recompress = Chain(old.Recompress),
        };

        // v7 stored which H.264 implementation a preset should use, because its
        // presets asked for "H.264" and something had to choose between libx264
        // and libopenh264. v8 has no such presets: a run names the encoder, so
        // there is nothing left for the hint to switch between. Saying so beats
        // migrating a field nothing reads.
        if (Clean(old.H264Impl) is { } impl)
        {
            notes.Add(messages.Format("migrate.h264Impl", new { impl }));
        }

        ReportPerRunState(old, notes, messages);

        var document = new SettingsDocument
        {
            SchemaVersion = SettingsDocument.CurrentSchemaVersion,
            Toolchain = new ToolchainSettings
            {
                FfmpegSource = Clean(old.FfmpegSource) ?? "path",
                NnediWeights = Clean(old.NnediWeights),
            },
            // "off" is a decision, not a URL, and has to survive the migration as
            // one: null means "never asked", which is what lets a download probe
            // and pick a route for itself.
            Network = new NetworkSettings
            {
                Proxy = Clean(old.FfmpegProxy),
                GithubFront = Clean(old.GhProxy),
            },
            Ui = new UiSettings
            {
                Language = Clean(old.Language) ?? "en",
                PlayMode = Clean(old.PlayMode) ?? "off",
                PlayerPath = Clean(old.PlayerPath),
                CheckForUpdates = old.AutoUpdateCheck ?? true,
            },
            Defaults = defaults,
            CustomPresets = CustomPresets(old.CustomEras, notes, messages),
        };

        return new MigrationOutcome(document.Normalised(), notes);
    }

    /// <summary>
    /// Lists the per-run state a v7 file carried and a v8 file does not. Silent
    /// removal here would look like a bug to anyone upgrading with a trim range
    /// they actually wanted.
    /// </summary>
    private static void ReportPerRunState(
        LegacyV7Settings old, List<string> notes, Localization.IMessages messages)
    {
        if (Clean(old.InputPath) is { } input)
        {
            notes.Add(messages.Format("migrate.inputPath", new { path = input }));
        }

        if (Clean(old.OutputPath) is { } output)
        {
            notes.Add(messages.Format("migrate.outputPath", new { path = output }));
        }

        if (old.StartTimeSeconds is > 0 || old.EndTimeSeconds is > 0)
        {
            notes.Add(messages["migrate.trim"]);
        }

        if (old.VideoStream is not null || old.AudioStream is not null)
        {
            notes.Add(messages["migrate.streams"]);
        }
    }

    private static string FitMode(
        LegacyV7Settings old, List<string> notes, Localization.IMessages messages)
    {
        // v7.7 and earlier: "crop 4:3 = no" meant no crop at all, i.e. a stretch.
        // Read before FitMode so a file carrying both keeps the older meaning.
        if (old.Crop43 is false)
        {
            notes.Add(messages["migrate.crop43"]);
            return "stretch";
        }

        string mode = Clean(old.FitMode) ?? "crop";
        return mode is "crop" or "fit" or "stretch" ? mode : "crop";
    }

    private static SubtitleDefaults Subtitles(LegacySubtitles? old)
    {
        if (old is null) return new SubtitleDefaults();

        return new SubtitleDefaults
        {
            Mode = Clean(old.Mode) ?? "burn",
            Files = old.Files is { } files ? [.. files.Select(Clean).Where(f => f is not null).Select(f => f!)] : [],
            Stream = old.Stream is >= 0 ? old.Stream : null,
            Codec = Clean(old.Codec),
            FontDir = Clean(old.FontDir),
            FallbackFont = Clean(old.FallbackFont),
            BoxAspect = Clean(old.BoxAspect) ?? "source",
        };
    }

    private static RecompressChain Chain(LegacyRecompress? old)
    {
        if (old is null) return new RecompressChain();

        var passes = new List<RecompressPass>();

        // v7.9.8.3 moved the first pass into the same shape as the rest, but kept
        // Args/Container at the top level so every earlier file still loaded.
        // Reading both here is that compatibility, in one place.
        if (Clean(old.Args) is { } first)
            passes.Add(new RecompressPass(first, Clean(old.Container) ?? "mp4"));

        if (old.Extra is { } extra)
        {
            foreach (LegacyPass? pass in extra)
            {
                if (pass is null) continue;
                if (Clean(pass.Args) is not { } args) continue;
                passes.Add(new RecompressPass(args, Clean(pass.Container) ?? "mp4"));
            }
        }

        return new RecompressChain { Enabled = old.Enabled, Passes = passes };
    }

    /// <summary>
    /// v7's user presets, carried over as names rather than as rows.
    ///
    /// <para>A v7 preset could point at a built-in era (<c>site</c> plus a
    /// <c>kind</c> of "era" or "site"); v8 has no built-in table to point at, so
    /// the two layers become one record and the site name becomes the label. The
    /// numbers survive untouched -- those are the user's own, and dropping them
    /// would lose real work -- but nothing else about the old shape does.</para>
    /// </summary>
    private static IReadOnlyList<CustomPreset> CustomPresets(
        List<LegacyCustomEra?>? rows, List<string> notes, Localization.IMessages messages)
    {
        if (rows is not { Count: > 0 }) return [];

        var presets = new List<CustomPreset>(rows.Count);
        foreach (LegacyCustomEra? row in rows)
        {
            if (row is null) continue;

            string id = Slug(row.Id);
            if (id.Length == 0)
            {
                notes.Add(messages["migrate.presetNoId"]);
                continue;
            }

            presets.Add(new CustomPreset
            {
                Id = id,
                Label = LegacyLabel(row),
                Encoder = Clean(row.Encoder),
                Width = row.Width is > 0 ? row.Width : null,
                Height = row.Height is > 0 ? row.Height : null,
                Fps = row.Fps is > 0 ? row.Fps : null,
                FpsLevels = row.FpsLevels is { Count: > 0 } levels
                    ? [.. levels.Where(v => v is > 0 and <= 120).Select(v => v!.Value)]
                    : null,
                LimitKbps = row.LimitKbps is > 0 ? row.LimitKbps : null,
                LiveQv = row.LiveQv,
                LiveCrf = row.LiveCrf,
                WindowSeconds = row.WindowSeconds,
                BufferFrames = row.BufferFrames,
                Deinterlace = Clean(row.Deinterlace),
                ScaleAlgo = Clean(row.ScaleAlgo),
                AudioEncoder = Clean(row.AudioEncoder),
                AudioKbps = row.AudioKbps,
                AudioChannels = row.AudioChannels,
                AudioRate = row.AudioRate,
                RcWidth = Positive(row.RcWidth, 320),
                RcHeight = Positive(row.RcHeight, 240),
                RcFps = Positive(row.RcFps, 15),
                RcVideoKbps = Positive(row.RcVideoKbps, 300),
                RcCrf = Clamp(row.RcCrf, 1, 51, 28),
                RcProfile = Clean(row.RcProfile) ?? "baseline",
                RcAudioKbps = Positive(row.RcAudioKbps, 32),
                RcAudioRate = Positive(row.RcAudioRate, 22050),
                RcAudioChannels = Clamp(row.RcAudioChannels, 1, 8, 1),
                VideoCodec = Clean(row.VideoCodec) ?? "libx264",
                AudioCodec = Clean(row.AudioCodec) ?? "aac",
                Container = Clean(row.Container) ?? "mp4",
                Note = Clean(row.Note),
            });
        }

        return presets;
    }

    /// <summary>
    /// The old row's human name, rebuilt from what v7 stored: a site, a year, or
    /// both. It is only a label -- the preset's identity is the id -- so an
    /// unnamed row simply has none.
    /// </summary>
    private static string? LegacyLabel(LegacyCustomEra row)
    {
        string? site = Clean(row.Site);
        int year = row.Year is > 0 and < 2200 ? row.Year!.Value : 0;

        if (site is { Length: > 0 } && year > 0) return $"{site} {year}";
        if (site is { Length: > 0 }) return site;
        return year > 0 ? year.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// The ladder, read honestly: finite, positive, descending, at most eight
    /// rungs. A hand-edited file can hold anything, and a non-monotonic ladder
    /// only shows up later as a rung that can never be reached. Sorting is the
    /// forgiving reading of "these are my rungs, I typed them in a hurry".
    /// </summary>
    private static IReadOnlyList<double> Ladder(
        List<double?>? levels, List<string> notes, Localization.IMessages messages)
    {
        if (levels is not { Count: > 0 }) return [.. Constants.DefaultFpsLevels];

        var values = new List<double>(levels.Count);
        foreach (double? raw in levels)
        {
            if (raw is not { } value) continue;
            if (!double.IsFinite(value) || value <= 0 || value > 120) continue;
            values.Add(Math.Round(value, 6));
        }

        values.Sort((a, b) => b.CompareTo(a));

        var ladder = new List<double>(values.Count);
        foreach (double value in values)
        {
            if (ladder.Count == 0 || value < ladder[^1] - 1e-9) ladder.Add(value);
            if (ladder.Count == 8) break;
        }

        if (ladder.Count == 0)
        {
            notes.Add(messages["migrate.ladderEmpty"]);
            return [.. Constants.DefaultFpsLevels];
        }

        if (ladder.Count != values.Count)
        {
            notes.Add(messages.Format("migrate.ladderCleaned", new { given = values.Count, kept = ladder.Count }));
        }

        return ladder;
    }

    private static IReadOnlyList<FpsThreshold> Thresholds(List<LegacyRung?>? rungs)
    {
        if (rungs is not { Count: > 0 }) return [];

        var list = new List<FpsThreshold>(rungs.Count);
        foreach (LegacyRung? rung in rungs)
        {
            if (rung is null || !double.IsFinite(rung.Fps) || rung.Fps <= 0) continue;
            list.Add(new FpsThreshold(rung.Fps, rung.DropAboveKbps, rung.RiseBelowKbps));
        }

        return list;
    }

    /// <summary>
    /// Ids are lower-case slugs so a command line can carry them unquoted. One
    /// slash survives, because a custom row may be written as
    /// <c>mysite/2012</c> to sit beside the built-in <c>site/year</c> rows.
    /// </summary>
    private static string Slug(string? raw)
    {
        if (Clean(raw) is not { } text) return "";

        var builder = new System.Text.StringBuilder(text.Length);
        bool slash = false;
        foreach (char ch in text.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(ch);
            else if (ch == '/' && !slash && builder.Length > 0)
            {
                builder.Append('/');
                slash = true;
            }
            else if (ch is '_' or '-' or ' ' or '.') builder.Append('_');
        }

        string id = builder.ToString().Trim('_').Trim('/');
        return id.Length > 24 ? id[..24] : id;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }

    private static int Clamp(int? value, int min, int max, int fallback)
        => value is { } v ? Math.Clamp(v, min, max) : fallback;

    private static int Positive(int? value, int fallback) => value is > 0 ? value.Value : fallback;

    private static double Positive(double? value, double fallback)
        => value is { } v && double.IsFinite(v) && v > 0 ? v : fallback;
}

