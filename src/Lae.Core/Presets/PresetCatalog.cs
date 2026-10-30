using System.Globalization;
using Lae.Core.Settings;

namespace Lae.Core.Presets;

/// <summary>The outcome of an edit: the new document, or why there is none.</summary>
public sealed record PresetChange(SettingsDocument? Document, string? Error)
{
    public bool Ok => Document is not null && Error is null;

    public static PresetChange Failed(string error) => new(null, error);

    public static PresetChange Done(SettingsDocument document) => new(document, null);
}

/// <summary>
/// The preset library: reading, validating, adding, removing and applying.
///
/// <para>v8 ships <em>no</em> built-in presets. v7 carried 32 rows of them --
/// five generic eras plus 27 site-and-year combinations -- and they were the
/// part of the program that aged worst: numbers that were right for one portal
/// in 2006 were wrong for the next one, and nothing in the program could say
/// which of them a user trusted. A preset here is something the user wrote down:
/// a name, and the handful of settings that name stands for.</para>
///
/// <para>Everything in this type is pure. Adding a preset returns a new document
/// instead of writing a file, so the rules can be tested without a disk and the
/// command that writes stays a thin shell.</para>
/// </summary>
public static class PresetCatalog
{
    /// <summary>The library, in the order a list should print: by id.</summary>
    public static IReadOnlyList<CustomPreset> All(SettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return [.. document.CustomPresets
            .Where(p => p is not null && p.Id is { Length: > 0 })
            .OrderBy(p => p.Id, StringComparer.Ordinal)];
    }

    /// <summary>One preset by id, case-insensitively -- ids are typed by hand.</summary>
    public static CustomPreset? Find(SettingsDocument document, string? id)
    {
        if (document is null || string.IsNullOrWhiteSpace(id)) return null;

        string wanted = id.Trim();
        foreach (CustomPreset preset in All(document))
        {
            if (string.Equals(preset.Id, wanted, StringComparison.OrdinalIgnoreCase)) return preset;
        }

        return null;
    }

    /// <summary>
    /// Longest id that will be accepted. Ids are typed by hand and appear in a
    /// JSON key and on a command line; the limit exists so that
    /// <see cref="NormaliseId"/> can size its stack buffer from the input without
    /// a very long argument turning into a stack overflow.
    /// </summary>
    public const int MaxIdLength = 64;

    /// <summary>
    /// The id as it will be stored: trimmed, lower-case, spaces turned into
    /// hyphens. Ids end up on a command line and in a JSON key, so anything that
    /// would need quoting is refused rather than escaped.
    /// </summary>
    public static string NormaliseId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        string trimmed = raw.Trim();
        if (trimmed.Length > MaxIdLength) trimmed = trimmed[..MaxIdLength];

        Span<char> buffer = stackalloc char[trimmed.Length];
        int length = 0;
        foreach (char c in trimmed)
        {
            char mapped = c switch
            {
                ' ' or '\t' => '-',
                _ => char.ToLowerInvariant(c),
            };

            bool legal = char.IsAsciiLetterOrDigit(mapped) || mapped is '-' or '_' or '.';
            if (legal) buffer[length++] = mapped;
        }

        return new string(buffer[..length]).Trim('-', '.');
    }

    /// <summary>
    /// Adds a preset, or replaces the one with the same id.
    ///
    /// <para>Replacing is the intended way to edit: there is no second verb for
    /// it, because "add" and "update" would need the same validation and would
    /// drift the moment one of them grew a rule.</para>
    /// </summary>
    public static PresetChange Put(SettingsDocument document, CustomPreset preset)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(preset);

        string id = NormaliseId(preset.Id);
        if (id.Length == 0)
            return PresetChange.Failed("a preset needs an id made of letters, digits, '-', '_' or '.'");

        // Truncating silently would store a preset the user cannot then reach by
        // the name they typed, so a too-long id is refused with the number.
        if (preset.Id.Trim().Length > MaxIdLength)
            return PresetChange.Failed($"an id may be at most {MaxIdLength} characters, got {preset.Id.Trim().Length}");

        if (preset.Fps is { } fps && (!double.IsFinite(fps) || fps <= 0))
            return PresetChange.Failed($"'{id}': the frame rate must be positive");

        if (preset.Width is { } w && w <= 0)
            return PresetChange.Failed($"'{id}': the width must be positive");
        if (preset.Height is { } h && h <= 0)
            return PresetChange.Failed($"'{id}': the height must be positive");
        if (preset.LimitKbps is { } limit && (!double.IsFinite(limit) || limit <= 0))
            return PresetChange.Failed($"'{id}': the bitrate ceiling must be positive");

        // Zero is not "measure instantly": the ladder's window refuses to be
        // constructed with it, and that refusal would arrive in the middle of an
        // encode. A hand-written preset is exactly where such a number comes from.
        if (preset.WindowSeconds is { } window && (!double.IsFinite(window) || window <= 0))
            return PresetChange.Failed($"'{id}': the bitrate window must be positive");

        if (preset.BufferFrames is { } buffer && buffer < 1)
            return PresetChange.Failed($"'{id}': the frame buffer must hold at least one frame");

        // A ladder whose every rung is above the base rate would adapt in one
        // direction only, and a preset that cannot do what it says is worth
        // refusing while the user is still looking at the command.
        if (preset.FpsLevels is { Count: > 0 } levels && preset.Fps is { } baseFps
            && EncoderMath.SelectLevels(baseFps, levels).Count == 0)
        {
            return PresetChange.Failed($"'{id}': no ladder rung is at or below the {baseFps:G4} fps base rate");
        }

        CustomPreset stored = preset with { Id = id };

        List<CustomPreset> kept =
        [
            .. All(document).Where(p => !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)),
            stored,
        ];

        return PresetChange.Done(document with { CustomPresets = kept });
    }

    /// <summary>Removes one preset. A missing id is an error, not a silent no-op.</summary>
    public static PresetChange Remove(SettingsDocument document, string? id)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (Find(document, id) is not { } preset) return PresetChange.Failed($"there is no preset named '{id}'");

        return PresetChange.Done(document with
        {
            CustomPresets = [.. All(document).Where(p => !string.Equals(p.Id, preset.Id, StringComparison.Ordinal))],
        });
    }

    /// <summary>
    /// The stored defaults with one preset laid over them.
    ///
    /// <para>Only the fields the preset actually carries move. A preset that says
    /// nothing about audio must not reset the audio settings to something else --
    /// that is the difference between a preset and a second copy of the defaults,
    /// and it is why every field on <see cref="CustomPreset"/> is nullable.</para>
    ///
    /// <para>The quality number is the one field that needs translating: it is
    /// stored in the preset encoder's own scale, and the run may be using a
    /// different encoder. Handing <c>-q:v 9</c> to libx264 as a CRF was
    /// v7.9.8.2's bug, measured at 2.5x the intended bitrate.</para>
    /// </summary>
    public static EncodeDefaults Apply(CustomPreset preset, EncodeDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(defaults);

        string? encoder = preset.Encoder ?? defaults.Encoder;

        int? quality = null;
        if (preset.LiveQv is { } qv)
        {
            quality = EncoderMath.ConvertQuality(EncoderMath.QualityScale.Qv, EncoderMath.ScaleOf(encoder), qv);
        }
        else if (preset.LiveCrf is { } crf)
        {
            quality = EncoderMath.ConvertQuality(EncoderMath.QualityScale.Crf, EncoderMath.ScaleOf(encoder), crf);
        }

        EncodeDefaults applied = defaults with
        {
            Encoder = encoder,
            Container = preset.Container ?? defaults.Container,
            Width = preset.Width ?? defaults.Width,
            Height = preset.Height ?? defaults.Height,
            Fps = preset.Fps ?? defaults.Fps,
            LimitKbps = preset.LimitKbps ?? defaults.LimitKbps,
            WindowSeconds = preset.WindowSeconds ?? defaults.WindowSeconds,
            BufferFrames = preset.BufferFrames ?? defaults.BufferFrames,
            ScaleAlgo = preset.ScaleAlgo ?? defaults.ScaleAlgo,
            AudioEncoder = preset.AudioEncoder ?? defaults.AudioEncoder,
            AudioKbps = preset.AudioKbps ?? defaults.AudioKbps,
            AudioChannels = preset.AudioChannels ?? defaults.AudioChannels,
            AudioRate = preset.AudioRate ?? defaults.AudioRate,
            Quality = quality ?? defaults.Quality,
        };

        // The ladder only moves when the preset names one; a preset without it
        // must keep whatever the user had, or every preset would silently reset
        // the adaptive behaviour to the built-in four rungs.
        //
        // Thresholds go with the rungs, as one list. Thresholds are keyed by frame
        // rate, so keeping the old ones while taking the new rungs would hang the
        // user's 150 kbps drop point on whichever rung happened to survive the
        // edit -- the exact failure the command-line path refuses to make, and
        // this is the same operation arriving through a different door.
        if (preset.FpsLevels is { Count: > 0 } levels)
        {
            applied = applied with { FpsLevels = [.. levels], FpsThresholds = [] };
        }

        // Deinterlacing is a filter with three parts; naming the filter alone
        // would leave a previous mode or parity attached to it.
        if (preset.Deinterlace is { Length: > 0 } filter)
        {
            applied = applied with { Deinterlace = new DeinterlaceSettings { Filter = filter } };
        }

        return applied;
    }

    /// <summary>One line for a list: what the preset changes, in the user's terms.</summary>
    public static string Describe(CustomPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        var parts = new List<string>(6);
        if (preset.Encoder is { Length: > 0 } encoder) parts.Add(encoder);
        if (preset.Width is { } w && preset.Height is { } h) parts.Add($"{w}x{h}");
        if (preset.Fps is { } fps) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{fps:G4} fps"));
        if (preset.LimitKbps is { } limit) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{limit:G4} kbps"));
        if (preset.Container is { Length: > 0 } container) parts.Add(container);

        return parts.Count == 0 ? "no overrides" : string.Join(", ", parts);
    }
}
