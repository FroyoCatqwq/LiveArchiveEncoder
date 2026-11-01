using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lae.Core.Plugins;

/// <summary>
/// The wire format of the handshake, and the words the invocation is built from.
///
/// <para>These live in <c>Lae.Core</c> because they are the public contract: a
/// plugin author implements against them, and the host parses with them. Putting
/// them in the host assembly instead would mean a second copy in every test
/// fixture and every future caller.</para>
///
/// <para>Version numbers are not decoration. <c>schema</c> describes the shape of
/// this JSON; <c>protocol</c> describes how <c>run</c> is invoked. They can move
/// independently -- a plugin that adds a field changes neither, one that renames
/// <c>--input</c> changes the second. A host that receives a number it does not
/// know refuses the plugin rather than guessing: see <c>HandshakeReader</c>.</para>
/// </summary>
public static class PluginProtocol
{
    /// <summary>The handshake JSON shape this build understands.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The invocation shape this build understands.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Asked of a plugin to make it describe itself.</summary>
    public const string HandshakeFlag = "--lae-handshake";

    /// <summary>The subcommand that runs one stage.</summary>
    public const string RunCommand = "run";

    /// <summary>Which stage of the plugin to run (its own subcommand name).</summary>
    public const string StageOption = "--stage";

    /// <summary>Absolute path the plugin reads.</summary>
    public const string InputOption = "--input";

    /// <summary>Absolute path the plugin writes; always a <c>.part</c>.</summary>
    public const string OutputOption = "--output";

    /// <summary>One <c>key=value</c> parameter, repeated per parameter.</summary>
    public const string ParamOption = "--param";
}

/// <summary>
/// What a plugin says about itself, one line of JSON on stdout.
///
/// <para>Every member is optional in the JSON except where the reader enforces
/// otherwise, and the defaults live in the computed properties rather than in
/// initialisers: an initialiser would be indistinguishable from a plugin that
/// literally wrote that value, and the difference matters in the report --
/// "declared deterministic" and "did not say" are not the same claim.</para>
/// </summary>
public sealed record PluginHandshake
{
    /// <summary>Shape of this JSON. Required.</summary>
    public int Schema { get; init; }

    /// <summary>How <c>run</c> is invoked. Required.</summary>
    public int Protocol { get; init; }

    /// <summary>Must match the <c>[[plugins]]</c> entry that named it.</summary>
    public string Id { get; init; } = "";

    /// <summary>Reported only; never compared.</summary>
    public string? Version { get; init; }

    /// <summary>What this plugin can process. Absent means video.</summary>
    public IReadOnlyList<string>? Media { get; init; }

    /// <summary>
    /// Whether the same input and parameters give the same output.
    ///
    /// <para>A plugin that says <c>false</c> is still allowed to run; the report
    /// says the step is not reproducible. That is the useful half of the answer --
    /// refusing it would remove a working plugin over a property the user may not
    /// care about.</para>
    /// </summary>
    public bool? Deterministic { get; init; }

    /// <summary>One entry per subcommand the plugin offers.</summary>
    public IReadOnlyList<PluginStageDeclaration> Stages { get; init; } = [];

    /// <summary>What it processes, with the documented default filled in.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveMedia =>
        Media is { Count: > 0 } media ? media : ["video"];

    /// <summary>Assumed true when the plugin did not say.</summary>
    [JsonIgnore]
    public bool IsDeterministic => Deterministic ?? true;
}

/// <summary>
/// One subcommand a plugin offers.
///
/// <para><see cref="Id"/> and <see cref="Placement"/> are two different things and
/// the field names keep them apart on purpose: the id is what
/// <c>run --stage</c> takes, the placement is where the host may insert it. One
/// subcommand may be legal at all three placements -- <c>lae-damage</c> is -- and
/// a plugin with several subcommands may place them differently.</para>
/// </summary>
public sealed record PluginStageDeclaration
{
    /// <summary>The plugin's own subcommand name.</summary>
    public string Id { get; init; } = "";

    /// <summary>Where the host may insert it; unknown values are reported and ignored.</summary>
    public IReadOnlyList<string> Placement { get; init; } = [];

    /// <summary>What may be passed to this stage, and what is legal for each.</summary>
    public IReadOnlyList<PluginParamDeclaration> Params { get; init; } = [];
}

/// <summary>One parameter a stage accepts.</summary>
public sealed record PluginParamDeclaration
{
    public string Name { get; init; } = "";

    /// <summary><c>float</c> / <c>int</c> / <c>string</c> / <c>bool</c>, as written in the JSON.</summary>
    public string Type { get; init; } = "";

    /// <summary>Inclusive lower bound, for numeric types.</summary>
    public double? Min { get; init; }

    /// <summary>Inclusive upper bound, for numeric types.</summary>
    public double? Max { get; init; }

    /// <summary>
    /// The value to use when the settings do not name this parameter.
    ///
    /// <para>Kept as the JSON the plugin wrote rather than a typed value, because
    /// the contract lets it be any scalar: <c>"default": 0.25</c> is a number,
    /// <c>"default": "fast"</c> is a string, and a plugin that writes the number
    /// must not be rejected for it. <see cref="DefaultValue"/> renders whichever it
    /// is into the text <c>--param k=v</c> needs.</para>
    /// </summary>
    [JsonPropertyName("default")]
    public JsonElement? Default { get; init; }

    /// <summary>The default as bytes on a command line, or null when there is none.</summary>
    [JsonIgnore]
    public string? DefaultValue => Default switch
    {
        { ValueKind: JsonValueKind.String } element => element.GetString(),

        // Raw text, not a re-rendered number: "0.25" written by the plugin is the
        // "0.25" the plugin will parse back, with no culture in between.
        { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } element
            => element.GetRawText(),

        _ => null,
    };

    /// <summary>
    /// The declared type, or <c>null</c> when this build does not know it.
    ///
    /// <para>Forgiving about case and surrounding blanks, because the value comes
    /// from a hand-written plugin; strict about the word, because a type the host
    /// cannot parse is a value the host cannot validate, and passing an
    /// unvalidated value to a black box is how a bad encode becomes a mystery.</para>
    /// </summary>
    [JsonIgnore]
    public PluginParamType? Kind => Type.Trim().ToLowerInvariant() switch
    {
        "float" => PluginParamType.Float,
        "int" => PluginParamType.Int,
        "string" => PluginParamType.String,
        "bool" => PluginParamType.Bool,
        _ => null,
    };
}

/// <summary>The four value shapes a plugin may declare.</summary>
[SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "These are the protocol's own words -- the handshake writes \"float\", "
                  + "\"int\", \"string\", \"bool\" -- and a member called Real would have to be "
                  + "translated back to float on sight by everyone reading the mapping.")]
public enum PluginParamType
{
    /// <summary>Parsed as a double, culture-invariant.</summary>
    Float,

    /// <summary>Parsed as a long; a fractional value is an error, not a truncation.</summary>
    Int,

    /// <summary>Passed through unchanged.</summary>
    String,

    /// <summary><c>true</c> / <c>false</c>, case-insensitive.</summary>
    Bool,
}
