using System.Text.Json;
using Lae.Core.Localization;

namespace Lae.Core.Plugins;

/// <summary>
/// What came of reading a plugin's handshake: either a usable declaration, or the
/// reasons it cannot be used.
///
/// <para>Two lists, not one, because they call for different behaviour. A problem
/// means the plugin must not run; a warning means it runs and the report says
/// something the user should know. Folding a warning into the problem list would
/// disable a working plugin over a spelling in a future version of the protocol,
/// and folding a problem into the warnings would run a plugin the host has just
/// admitted it does not understand.</para>
/// </summary>
public sealed record HandshakeVerdict
{
    /// <summary>The parsed declaration, or <c>null</c> when it was rejected.</summary>
    public PluginHandshake? Handshake { get; init; }

    /// <summary>Reasons not to run it. Non-empty means <see cref="Handshake"/> is null.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>Things worth saying that do not stop the run.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool Ok => Problems.Count == 0 && Handshake is not null;
}

/// <summary>
/// Reads one line of handshake JSON into a <see cref="PluginHandshake"/>, and
/// decides whether this build can honour it.
///
/// <para>Pure: a string in, a verdict out. No child process, no clock, no
/// cancellation -- everything that talks to the plugin lives in
/// <c>Lae.Plugins</c>. That split is what lets every rejection path be tested
/// with a string literal instead of a fake executable, and the rejection paths are
/// the ones that matter: a happy handshake is one case, a plugin that lies about
/// itself is eleven.</para>
///
/// <para>Nothing here throws. A plugin is third-party code printing text into a
/// pipe, so malformed input is normal input, and a parse error that escapes as an
/// exception would end the run with a stack trace instead of the one sentence the
/// user needs.</para>
/// </summary>
public static class HandshakeReader
{
    /// <summary>
    /// Read <paramref name="text"/> -- the plugin's stdout -- as a handshake.
    ///
    /// <param name="expectedId">
    /// The id the settings used. Empty skips the comparison, which is what an
    /// explicit <c>--plugin &lt;path&gt;</c> needs: there is no configuration entry
    /// to agree with.
    /// </param>
    /// </summary>
    public static HandshakeVerdict Read(string? text, string expectedId, IMessages messages)
    {
        if (FirstLine(text) is not { } line)
            return Reject(messages["plugin.error.handshakeEmpty"]);

        string whole = (text ?? "").Trim();

        // The contract says the first line is the whole handshake. That rule exists
        // so a plugin can log after it, and it is what the host tries first.
        //
        // Then, once: the whole of stdout. Every language's default JSON printer
        // can indent, and the contract's own example is indented -- a plugin author
        // who copies it would otherwise be told their JSON is broken. The retry
        // cannot mask anything: the first attempt has already failed, and a
        // multi-line handshake followed by log lines still ends up refused.
        string? error;
        PluginHandshake? handshake = TryParse(line, out error);

        if (handshake is null && whole.Length > line.Length)
            handshake = TryParse(whole, out error);

        if (handshake is null)
        {
            // A later schema may have changed a field's shape, in which case the
            // honest reason is the version, not the parse. Asking one more time
            // with a looser reader costs nothing and is the difference between
            // "upgrade the host" and "your JSON is broken".
            return TooNew(line, messages) ?? TooNew(whole, messages)
                ?? Reject(messages.Format("plugin.error.handshakeNotJson", new { detail = error ?? "-" }));
        }

        // Version first: everything below reads fields whose meaning the version
        // decides. Reporting a missing stage from a schema 2 handshake would send
        // the author looking in the wrong place.
        if (handshake.Schema > PluginProtocol.SchemaVersion)
        {
            return Reject(messages.Format("plugin.error.handshakeSchema", new
            {
                given = handshake.Schema,
                supported = PluginProtocol.SchemaVersion,
            }));
        }

        if (handshake.Schema < 1)
        {
            return Reject(messages.Format("plugin.error.handshakeSchemaInvalid", new { given = handshake.Schema }));
        }

        if (handshake.Protocol != PluginProtocol.ProtocolVersion)
        {
            return Reject(messages.Format("plugin.error.handshakeProtocol", new
            {
                given = handshake.Protocol,
                supported = PluginProtocol.ProtocolVersion,
            }));
        }

        if (!string.IsNullOrWhiteSpace(expectedId)
            && !string.Equals(handshake.Id, expectedId, StringComparison.OrdinalIgnoreCase))
        {
            return Reject(messages.Format("plugin.error.handshakeId", new
            {
                actual = string.IsNullOrWhiteSpace(handshake.Id) ? "?" : handshake.Id,
                expected = expectedId,
            }));
        }

        var problems = new List<string>();
        var warnings = new List<string>();

        if (!handshake.EffectiveMedia.Any(m => string.Equals(m, "video", StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add(messages.Format("plugin.error.mediaNotVideo", new
            {
                media = string.Join(", ", handshake.EffectiveMedia),
            }));
        }

        if (handshake.Stages is not { Count: > 0 })
            problems.Add(messages["plugin.error.handshakeNoStages"]);

        for (int i = 0; i < handshake.Stages.Count; i++)
        {
            // JSON can carry an explicit null in an array, and a non-nullable
            // element type does not stop it: `"stages": [null]` deserialises to a
            // list whose only member is null. Third-party text, so it is checked
            // rather than trusted.
            if (handshake.Stages[i] is not { } stage)
            {
                problems.Add(messages.Format("plugin.error.stageNoId", new { index = i + 1 }));
                continue;
            }

            if (string.IsNullOrWhiteSpace(stage.Id))
            {
                problems.Add(messages.Format("plugin.error.stageNoId", new { index = i + 1 }));
                continue;
            }

            CheckPlacements(stage, problems, warnings, messages);
            CheckParamTypes(stage, problems, messages);
        }

        if (!handshake.IsDeterministic) warnings.Add(messages["plugin.warn.notDeterministic"]);

        return problems.Count > 0
            ? new HandshakeVerdict { Problems = problems, Warnings = warnings }
            : new HandshakeVerdict { Handshake = handshake, Warnings = warnings };
    }

    /// <summary>
    /// Split a declared placement list into the ones the host can honour and the
    /// ones it cannot.
    ///
    /// <para>Unknown values are reported, not dropped in silence: a plugin built
    /// against a later protocol may declare a placement this build has never heard
    /// of, and the user who placed it there needs to know it will not run. Known
    /// ones are canonicalised so <c>Post-Encode</c> from a hand-written plugin
    /// matches the settings value it was configured with.</para>
    /// </summary>
    private static void CheckPlacements(
        PluginStageDeclaration stage, List<string> problems, List<string> warnings, IMessages messages)
    {
        IReadOnlyList<string> declared = PluginStages.Normalise(stage.Placement);        string[] unknown = [.. declared.Where(p => !PluginStages.IsKnown(p))];
        int known = declared.Count - unknown.Length;

        if (unknown.Length > 0 && known > 0)
        {
            warnings.Add(messages.Format("plugin.warn.stagePlacement", new
            {
                stage = stage.Id,
                given = string.Join(", ", unknown),
            }));
        }

        if (known == 0)
        {
            problems.Add(messages.Format("plugin.error.stageNoPlacement", new
            {
                stage = stage.Id,
                given = declared.Count == 0 ? "-" : string.Join(", ", declared),
            }));
        }
    }

    /// <summary>
    /// Every declared parameter must have a type this build can validate.
    ///
    /// <para>A parameter whose type is unknown cannot be checked against anything,
    /// and the host would have to pass whatever the settings said straight through
    /// to a black box. Rejecting the handshake is the same choice as everywhere
    /// else here: no guessing about a value that decides what the plugin does.</para>
    /// </summary>
    private static void CheckParamTypes(PluginStageDeclaration stage, List<string> problems, IMessages messages)
    {
        // Same reason as the stage loop: an explicit null survives deserialisation
        // into a list with a non-nullable element type.
        foreach (PluginParamDeclaration? param in stage.Params ?? [])
        {
            if (param is null) continue;
            if (param.Kind is not null && !string.IsNullOrWhiteSpace(param.Name)) continue;

            problems.Add(messages.Format("plugin.error.stageParamType", new
            {
                stage = stage.Id,
                name = string.IsNullOrWhiteSpace(param.Name) ? "?" : param.Name,
                type = string.IsNullOrWhiteSpace(param.Type) ? "-" : param.Type,
            }));
        }
    }

    /// <summary>Deserialize, or null and the reason it did not work.</summary>
    private static PluginHandshake? TryParse(string text, out string? error)
    {
        try
        {
            error = null;
            return JsonSerializer.Deserialize(text, PluginJsonContext.Default.PluginHandshake);
        }
        catch (JsonException exc)
        {
            error = exc.Message;
            return null;
        }
    }

    /// <summary>
    /// The first non-empty line, which is the whole handshake.
    ///
    /// <para>The contract says the first line is the answer and anything after it
    /// is ignored. Splitting on <c>\n</c> and trimming the <c>\r</c> keeps a
    /// Windows plugin working, and skipping leading blank lines is the one
    /// forgiveness worth having: a plugin that prints a banner before its JSON is
    /// still describable, and the alternative is a rule nobody can see they broke.</para>
    /// </summary>
    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0) return line;
        }

        return null;
    }

    /// <summary>
    /// The schema number from a handshake that failed to parse, when the failure
    /// was really a version this build does not know.
    ///
    /// <para>Deliberately crude: it looks only at the number, and only so it can
    /// say "this build speaks schema 1" instead of quoting a JSON error that
    /// mentions a field the author has every right to have changed.</para>
    /// </summary>
    private static HandshakeVerdict? TooNew(string line, IMessages messages)
    {
        int? declared;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            declared = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schema", out JsonElement schema)
                && schema.ValueKind == JsonValueKind.Number
                && schema.TryGetInt32(out int value)
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;   // not JSON at all; the caller reports that
        }

        return declared is { } version && version > PluginProtocol.SchemaVersion
            ? Reject(messages.Format("plugin.error.handshakeSchema", new
            {
                given = version,
                supported = PluginProtocol.SchemaVersion,
            }))
            : null;
    }

    private static HandshakeVerdict Reject(string problem)
        => new() { Problems = [problem] };
}
