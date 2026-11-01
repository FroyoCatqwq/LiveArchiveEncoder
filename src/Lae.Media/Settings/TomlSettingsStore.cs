using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lae.Core.Localization;
using Lae.Core.Settings;
using Lae.Media.Localization;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Lae.Media.Settings;

/// <summary>
/// The settings file on disk, as TOML.
///
/// <para><b>Why TOML.</b> The file is written by a person and occasionally edited
/// by the program. TOML keeps comments, needs a quote around every string (so a
/// typo is a syntax error rather than a silent coercion), and has no control
/// flow -- which is what makes the surgical write below possible at all.</para>
///
/// <para><b>Why the write is surgical.</b> Serialising the whole document back
/// would erase every comment the user wrote, and the comments are half the reason
/// for choosing this format. The program only ever owns two things in this
/// document -- the <c>customPresets</c> block and, during migration, the file
/// itself -- so a save replaces exactly that block and leaves every other byte
/// alone. A field the program does not touch cannot be damaged by its writer.</para>
///
/// <para><b>Why unknown keys are fatal.</b> The serializer is STJ-shaped and
/// ignores keys it does not know. In a config file that is a trap: v7 shipped
/// <c>fpsLevesl</c> for a whole release and users who typed it silently got the
/// default ladder. <see cref="SettingsKeySchema"/> closes that hole by deriving
/// the legal key set from the document type.</para>
/// </summary>
public sealed class TomlSettingsStore : ISettingsStore
{
    /// <summary>
    /// The whole file is camelCase and omits nulls, so a stored document reads
    /// like the sample in the docs rather than like a dump of every field.
    /// </summary>
    private static readonly TomlSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Replace,
        WriteIndented = true,
        IndentSize = 2,
        DefaultIgnoreCondition = TomlIgnoreCondition.WhenWritingNull,
    };

    private const string PresetHeader = "[[customPresets]]";

    private readonly SettingsLocation _location;

    /// <summary>
    /// Set by the caller when the language is already known, and otherwise filled
    /// in on first use from the document this store read.
    /// </summary>
    private IMessages? _messages;

    public TomlSettingsStore(SettingsLocation location, IMessages? messages = null)
    {
        _location = location;
        _messages = messages;
    }

    /// <summary>
    /// Set when the file exists but could not be read. Saves are then refused:
    /// the in-memory document is the defaults, and writing it would replace a file
    /// the user may have spent an hour on with a set of defaults that says nothing
    /// about what went wrong.
    /// </summary>
    private string? _unreadable;

    /// <summary>The exact text <see cref="Load"/> read, so a save can keep it.</summary>
    private string? _source;

    public string Location => _location.File;

    public string? DataDirectory => _location.Directory;

    /// <summary>
    /// The language this store's sentences are written in.
    ///
    /// <para>A store has to speak before the language is known, because the
    /// language is one of the things it reads: a v8 file names it in
    /// <c>[ui]</c>, and a v7 file names it in <c>Language</c>. So the document it
    /// just parsed decides, <c>LAE_LANG</c> overrides, and the answer is kept --
    /// a migration happens on the first read, and by then there is nobody to ask.
    /// </para>
    ///
    /// <para>With no document at all (the file would not parse), the environment
    /// variable is the only clue, and English is the floor.</para>
    /// </summary>
    private IMessages Speak(SettingsDocument? document)
        => _messages ??= I18NextMessages.Create(LanguageChoice.Pick(
            Environment.GetEnvironmentVariable("LAE_LANG"), document?.Ui.Language));

    /// <summary>What the last <see cref="Load"/> did to the file, for the caller to report.</summary>
    public IReadOnlyList<string> MigrationNotes { get; private set; } = [];

    public SettingsDocument Load()
    {
        MigrationNotes = [];
        _unreadable = null;
        _source = null;

        if (File.Exists(_location.File))
        {
            try
            {
                string text = File.ReadAllText(_location.File);
                SettingsDocument document = Deserialize(text);
                _source = text;
                return document.Normalised();
            }
            catch (Exception exc) when (exc is not OutOfMemoryException)
            {
                // Reported, not swallowed: the note is what tells the user their
                // file is intact and why the program is running on defaults.
                IMessages messages = Speak(null);
                _unreadable = messages.Format("settings.unreadable", new { path = _location.File });
                MigrationNotes =
                [
                    messages.Format("settings.readFailed", new
                    {
                        type = exc.GetType().Name,
                        detail = exc.Message,
                    }),
                ];
                return new SettingsDocument();
            }
        }

        if (File.Exists(_location.PreviousFile) && TryReadPreviousJson() is { } previous)
        {
            return Migrate(
                previous, _location.PreviousFile, _location.PreviousBackup, "settings.migratedV8",
                removeSource: true);
        }

        if (File.Exists(_location.LegacyFile) && TryReadLegacy() is { } migrated)
        {
            return migrated;
        }

        return new SettingsDocument();
    }

    /// <summary>
    /// Parses TOML into a document, refusing any key the type does not have.
    /// Throws on a broken file; callers decide whether that is fatal.
    /// </summary>
    public SettingsDocument Deserialize(string text)
    {
        IMessages messages = Speak(null);
        TomlTable table = TomlSerializer.Deserialize<TomlTable>(text) ?? [];

        IReadOnlyList<string> problems = SettingsKeySchema.Validate(table);
        if (problems.Count > 0)
        {
            throw new InvalidDataException(messages.Format("settings.unknownKeys", new
            {
                problems = string.Join("; ", problems),
            }));
        }

        return TomlSerializer.Deserialize<SettingsDocument>(text, Options)
            ?? throw new InvalidDataException(messages["settings.empty"]);
    }

    /// <summary>
    /// Renders a whole document. This is what a brand-new file looks like, and
    /// what <c>lae config show --toml</c> prints.
    /// </summary>
    public static string Render(SettingsDocument document)
        => TomlSerializer.Serialize(document, Options);

    /// <summary>
    /// Writes atomically: a temporary sibling is fully written and flushed before
    /// it replaces the real file, so a crash mid-save leaves the previous settings
    /// rather than a truncated file that reads as "everything reset".
    /// </summary>
    public string? TrySave(SettingsDocument document)
    {
        if (_unreadable is { } refusal) return refusal;

        try
        {
            Directory.CreateDirectory(_location.Directory);

            // No source means there is nothing to preserve: either the file is new
            // or this is a migration writing the format for the first time.
            string text = _source is null ? Render(document) : Rewrite(_source, document);
            WriteAtomically(text);
            _source = text;
            return null;
        }
        catch (InvalidDataException exc)
        {
            // The file's own structure is the problem, not the disk: said
            // separately so a caller does not report it as "could not write",
            // which sends the user looking at permissions.
            return exc.Message;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                        or NotSupportedException or ArgumentException)
        {
            return exc.Message;
        }
    }

    /// <summary>
    /// Replaces the presets block in <paramref name="source"/> and changes nothing
    /// else -- not the comments, not the blank lines, not the order of the tables.
    ///
    /// <para>The block runs from its <c>[[customPresets]]</c> header to the next
    /// header. Removing and re-appending it means a save cannot reformat a file
    /// the user arranged by hand, which is the whole promise of keeping comments
    /// in the first place.</para>
    /// </summary>
    private string Rewrite(string source, SettingsDocument document)
    {
        string[] lines = source.Replace("\r\n", "\n").Split('\n');

        int start = Array.FindIndex(lines, IsPresetHeader);
        string section = PresetSection(document.CustomPresets);

        if (start < 0)
        {
            // A single `customPresets = [...]` value cannot be edited in place the
            // way a block can, and appending a block beside it would produce two
            // definitions of one key. Refusing is better than a file that no longer
            // parses the next time it is opened.
            if (Array.Exists(lines, line => IsKeyAssignment(line, "customPresets")))
            {
                throw new InvalidDataException(Speak(document).Format("settings.presetsNotBlocks", new
                {
                    header = PresetHeader,
                    key = nameof(SettingsDocument.CustomPresets),
                }));
            }

            return section.Length == 0 ? source : Trim(source) + Environment.NewLine + section;
        }

        int end = PresetBlockEnd(lines, start);

        var kept = new List<string>(lines.Length);
        kept.AddRange(lines[..start]);
        kept.AddRange(lines[end..]);

        string head = string.Join('\n', kept).TrimEnd();
        return section.Length == 0 ? head + "\n" : head + "\n\n" + section;
    }

    /// <summary>
    /// The <c>[[customPresets]]</c> blocks for a list of presets, taken from a
    /// document that contains nothing else of interest.
    ///
    /// <para>Rendering through the serializer rather than printing fields by hand
    /// keeps one definition of how a preset turns into TOML; a second writer would
    /// drift the moment a field is added.</para>
    /// </summary>
    private string PresetSection(IReadOnlyList<CustomPreset> presets)
    {
        if (presets.Count == 0) return "";

        string full = Render(new SettingsDocument { CustomPresets = presets }).Replace("\r\n", "\n");
        string[] lines = full.Split('\n');

        int start = Array.FindIndex(lines, IsPresetHeader);
        if (start < 0) throw new InvalidDataException(Speak(null)["settings.presetsRender"]);

        int end = PresetBlockEnd(lines, start);
        return string.Join('\n', lines[start..end]).TrimEnd() + "\n";
    }

    /// <summary>
    /// True for <c>[[customPresets]]</c> and anything indented in front of it.
    /// TOML allows leading whitespace before a header, so an exact prefix test on
    /// the raw line is not enough.
    /// </summary>
    private static bool IsPresetHeader(string line)
        => line.TrimStart().StartsWith(PresetHeader, StringComparison.Ordinal);

    /// <summary>True for any table or array-of-tables header.</summary>
    private static bool IsTableHeader(string line) => line.TrimStart().StartsWith('[');

    /// <summary>
    /// One past the last line belonging to the run of preset blocks that starts at
    /// <paramref name="start"/>.
    ///
    /// <para><b>Not "the next line starting with a bracket".</b> The presets are an
    /// array of tables, so every preset *is* a header: stopping at the first one
    /// made a save keep all but the first block and append a fresh copy of the
    /// first, which lost every preset added after the first and turned a removal
    /// into a rotation. Measured, not reasoned: file <c>[a]</c> plus document
    /// <c>[a,b]</c> wrote back <c>[a]</c> while the command had already printed
    /// "stored b".</para>
    ///
    /// <para>Comments and blank lines between two preset blocks belong to the run,
    /// so they are replaced along with it -- they were written about the presets.</para>
    /// </summary>
    private static int PresetBlockEnd(string[] lines, int start)
    {
        int end = start + 1;
        while (end < lines.Length && !(IsTableHeader(lines[end]) && !IsPresetHeader(lines[end]))) end++;
        return end;
    }

    private static bool IsKeyAssignment(string line, string key)
    {
        if (!line.StartsWith(key, StringComparison.Ordinal)) return false;

        string rest = line[key.Length..].TrimStart();
        return rest.StartsWith('=');
    }

    private static string Trim(string text) => text.Replace("\r\n", "\n").TrimEnd();

    private void WriteAtomically(string text)
    {
        string temporary = _location.File + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _location.File, overwrite: true);
        }
        finally
        {
            // A crash between the write and the move leaves a file next to the
            // settings that looks like a backup and is not one. The move takes it
            // away on success, so this only ever fires on a failure path.
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                // Best effort: the settings themselves are the thing that matters,
                // and failing here would replace the real error with this one.
            }
        }
    }

    /// <summary>
    /// Moves a document written by an older build into the TOML file, keeping the
    /// old one under a name that says what it is.
    ///
    /// <para><paramref name="removeSource"/> is false for the v7 file: v7 may still
    /// be the only build the user has, so its settings must stay exactly where v7
    /// expects them. It is true for v8's own previous file, which nothing else
    /// reads -- leaving it behind would mean migrating on every launch.</para>
    /// </summary>
    private SettingsDocument Migrate(
        SettingsDocument document, string from, string backup, string whatKey, bool removeSource)
    {
        IMessages messages = Speak(document);
        var notes = new List<string>();

        try
        {
            Directory.CreateDirectory(_location.Directory);
            if (!File.Exists(backup)) File.Copy(from, backup);
            notes.Add(messages.Format(whatKey, new
            {
                from = Path.GetFileName(from),
                backup = Path.GetFileName(backup),
            }));
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            notes.Add(messages.Format("settings.copyFailed", new { path = backup, detail = exc.Message }));
        }

        if (TrySave(document) is { } failure)
        {
            notes.Add(messages.Format("settings.migratedWriteFailed", new { detail = failure }));
        }
        else
        {
            notes.Add(messages.Format("settings.wrote", new { path = _location.File }));

            if (!removeSource) return Finish();

            try
            {
                File.Delete(from);
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                notes.Add(messages.Format("settings.removeFailed", new
                {
                    name = Path.GetFileName(from),
                    detail = exc.Message,
                }));
            }
        }

        return Finish();

        SettingsDocument Finish()
        {
            MigrationNotes = notes;
            return document;
        }
    }

    private SettingsDocument? TryReadPreviousJson()
    {
        try
        {
            string json = File.ReadAllText(_location.PreviousFile);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsDocument);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or JsonException)
        {
            MigrationNotes = [Speak(null).Format("settings.previousUnreadable", new { detail = exc.Message })];
            return null;
        }
    }

    private SettingsDocument? TryReadLegacy()
    {
        try
        {
            string json = File.ReadAllText(_location.LegacyFile);
            if (!LegacyV7Converter.LooksLikeV7(json)) return null;
            if (LegacyV7Converter.Parse(json) is not { } old) return null;

            // The v7 file names its own language, which is the language its notes
            // should be read in -- the v8 document does not exist yet.
            MigrationOutcome outcome = LegacyV7Converter.Convert(old, Speak(null));

            // Keep a copy before writing anything, so the conversion can be checked
            // even if the new file is later edited by hand. The v7 file itself is
            // left in place: v7 may still be the only build the user has.
            SettingsDocument document = Migrate(
                outcome.Document, _location.LegacyFile, _location.LegacyBackup, "settings.migratedV7",
                removeSource: false);

            IMessages messages = Speak(outcome.Document);
            MigrationNotes =
            [
                .. outcome.Notes,
                .. MigrationNotes,
                messages.Format("settings.legacyKept", new { path = _location.LegacyFile }),
            ];
            return document;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or JsonException)
        {
            MigrationNotes = [Speak(null).Format("settings.legacyUnreadable", new { detail = exc.Message })];
            return null;
        }
    }
}

