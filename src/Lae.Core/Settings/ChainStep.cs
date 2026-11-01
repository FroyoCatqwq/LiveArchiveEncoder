using System.Text.Json.Serialization;

namespace Lae.Core.Settings;

/// <summary>What a chain step turned out to be.</summary>
public enum ChainStepKind
{
    /// <summary>Nothing usable was written; the step is dropped.</summary>
    Empty,

    /// <summary>An FFmpeg hop: <see cref="ChainStep.Args"/> is the fragment.</summary>
    Ffmpeg,

    /// <summary>A plugin hop: <see cref="ChainStep.Plugin"/> names the executable.</summary>
    Plugin,

    /// <summary>Both were written. Never guessed at -- reported.</summary>
    Ambiguous,
}

/// <summary>
/// One link of the post-encode chain, as the settings file describes it.
///
/// <para>Two kinds of hop share this record: an FFmpeg transcode, written as
/// <c>args</c> plus an optional <c>container</c>; and a plugin invocation,
/// written as <c>plugin</c> plus an optional <c>stage</c> and <c>params</c>.
/// The chain itself (the list, the container pairing, the order) is identical
/// either way, which is why there is one list and not two.</para>
///
/// <para>One record with nullable fields rather than an abstract base with two
/// subtypes, because TOML has no tag: <c>[[recompress.passes]]</c> is a flat
/// table, and a polymorphic reader would need a discriminator the file does not
/// have. The shape is recovered from which fields are present, in
/// <see cref="Kind"/>.</para>
///
/// <para><see cref="Kind"/> answers <see cref="ChainStepKind.Ambiguous"/> when
/// both halves are written. That is a configuration mistake, not something to
/// resolve by precedence: silently preferring one half means the other one sits
/// in the file looking like it does something.</para>
/// </summary>
public sealed record ChainStep
{
    /// <summary>
    /// The FFmpeg fragment, everything between <c>-i</c> and the output path.
    /// Split into tokens by the planner, never by the shell.
    /// </summary>
    public string? Args { get; init; }

    /// <summary>
    /// Container extension without the dot: what this hop writes. Omitted, it
    /// follows the codec the fragment names, then the hop before.
    /// </summary>
    public string? Container { get; init; }

    /// <summary>The plugin's id, which is also the name discovery looks for.</summary>
    public string? Plugin { get; init; }

    /// <summary>
    /// The plugin's own stage id, passed as <c>run --stage</c>. Not a placement:
    /// see the note on <see cref="Lae.Core.Plugins.PluginStages"/>.
    /// </summary>
    public string? Stage { get; init; }

    /// <summary>
    /// Parameters for the plugin, passed as <c>--param k=v</c>. Values are
    /// strings here because that is what a settings file holds; the plugin's
    /// handshake says what type each one is, and the host validates against it.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Params { get; init; }

    /// <summary>Which of the two kinds this is, from the fields that are present.</summary>
    [JsonIgnore]
    public ChainStepKind Kind
    {
        get
        {
            bool ffmpeg = !string.IsNullOrWhiteSpace(Args);
            bool plugin = !string.IsNullOrWhiteSpace(Plugin);

            return (ffmpeg, plugin) switch
            {
                (true, true) => ChainStepKind.Ambiguous,
                (true, false) => ChainStepKind.Ffmpeg,
                (false, true) => ChainStepKind.Plugin,
                _ => ChainStepKind.Empty,
            };
        }
    }
}
