using System.Text.Json.Serialization;

namespace Lae.Core.Settings;

/// <summary>
/// The JSON contract for <see cref="SettingsDocument"/>, generated at compile time.
///
/// <para>Source generation rather than reflection: with reflection-based
/// serialisation, a trimmed or AOT-published build throws
/// <c>JsonSerializerIsReflectionDisabled</c> at the first save, which is a
/// startup crash in the release artefact and a working program in the debug
/// build -- v7 hit exactly that and fixed it this way. Even though v8 ships a
/// self-contained JIT build, keeping the contract generated costs nothing and
/// removes a whole class of "works on my machine".</para>
///
/// <para>Property names are camelCase because the file is meant to be read and
/// edited by a person; the class names stay idiomatic C#.</para>
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SettingsDocument))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
