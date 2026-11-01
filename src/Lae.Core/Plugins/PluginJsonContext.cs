using System.Text.Json.Serialization;

namespace Lae.Core.Plugins;

/// <summary>
/// The JSON contract for <see cref="PluginHandshake"/>, generated at compile time.
///
/// <para>Source generation, not reflection, for the same reason
/// <c>SettingsJsonContext</c> exists: a trimmed or AOT build throws
/// <c>JsonSerializerIsReflectionDisabled</c> the first time it deserialises, and
/// the failure lands on the one code path that talks to third-party code. v7 paid
/// for that lesson once; the plugin host is a worse place to learn it again.</para>
///
/// <para>Unknown members are ignored (the default), which is what lets a plugin
/// built against a later <c>schema</c> still fail with the honest reason --
/// "this build speaks schema 1" -- instead of a parse error that says nothing
/// about versions.</para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PluginHandshake))]
[JsonSerializable(typeof(PluginManifest))]
public sealed partial class PluginJsonContext : JsonSerializerContext;
