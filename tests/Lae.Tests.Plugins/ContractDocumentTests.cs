using System.Runtime.CompilerServices;
using System.Text.Json;
using Lae.Core.Localization;
using Lae.Core.Plugins;
using Lae.Media.Localization;

namespace Lae.Tests.Plugins;

/// <summary>
/// Guards <c>docs/plugin-contract.md</c>, which M3 will be written against.
///
/// <para>A contract document has one failure mode that never announces itself:
/// it stays on disk, looks authoritative, and quietly stops matching the code.
/// This repository has already paid for that once -- <c>PluginRegistration</c>
/// and the blueprint offered different stage names before either was
/// implemented, and the unwanted one (<c>pre-encode</c>) was the one a reader
/// would have implemented from the settings type.</para>
///
/// <para>These tests read the document as data. They cannot check that the
/// prose is true; they can check that the example parses, that it carries the
/// fields the host will require, and that every placement the code knows about
/// is in it.</para>
/// </summary>
public sealed class ContractDocumentTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string ContractText()
    {
        string path = Path.Combine(RepoRoot(), "docs", "plugin-contract.md");
        return File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException(
                "docs/plugin-contract.md is the document the plugin host is written against; "
                + "if it moved, move this test with it", path);
    }

    /// <summary>
    /// The handshake example is the one piece of the contract a plugin author
    /// copies. If it stops parsing, every other sentence in the document is
    /// still readable and still wrong.
    /// </summary>
    [Test]
    public async Task TheHandshakeExampleIsValidJsonWithTheFieldsTheHostWillRequire()
    {
        string example = FirstJsonBlock(ContractText());

        using JsonDocument document = JsonDocument.Parse(example);
        JsonElement root = document.RootElement;

        await Assert.That(root.TryGetProperty("schema", out JsonElement schema)).IsTrue()
            .Because("the host refuses a handshake it cannot version");
        await Assert.That(schema.ValueKind).IsEqualTo(JsonValueKind.Number);

        await Assert.That(root.TryGetProperty("protocol", out JsonElement protocol)).IsTrue()
            .Because("schema describes the JSON; protocol describes how run is invoked");
        await Assert.That(protocol.ValueKind).IsEqualTo(JsonValueKind.Number);

        await Assert.That(root.TryGetProperty("id", out JsonElement id)).IsTrue()
            .Because("a handshake whose id does not match the settings entry is a misconfiguration");
        await Assert.That(id.ValueKind).IsEqualTo(JsonValueKind.String);

        await Assert.That(root.TryGetProperty("stages", out JsonElement stages)).IsTrue()
            .Because("a plugin that declares no stage can never be placed in a chain");
        await Assert.That(stages.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(stages.GetArrayLength()).IsGreaterThan(0);

        JsonElement first = stages[0];
        await Assert.That(first.TryGetProperty("id", out _)).IsTrue()
            .Because("stages[].id is the plugin's own subcommand name, which run --stage takes");
        await Assert.That(first.TryGetProperty("placement", out JsonElement placement)).IsTrue()
            .Because("placement is where the host may insert it, and it is a different thing");
        await Assert.That(placement.ValueKind).IsEqualTo(JsonValueKind.Array);
    }

    /// <summary>
    /// Every placement the code knows appears in the document, and the example
    /// uses it. A placement added to <see cref="PluginStages"/> without a line
    /// in the contract is exactly how the two drifted apart before.
    /// </summary>
    [Test]
    public async Task EveryKnownPlacementIsNamedInTheContract()
    {
        string text = ContractText();

        List<string> missing = [.. PluginStages.All.Where(p => !text.Contains(p, StringComparison.Ordinal))];

        await Assert.That(missing).IsEmpty()
            .Because("PluginStages.All is the wire format; the contract must list every value in it");
    }

    /// <summary>
    /// All three placements appear in the handshake example, not just in the
    /// prose. The example is what an implementer copies into their plugin.
    /// </summary>
    [Test]
    public async Task TheHandshakeExampleShowsEveryPlacement()
    {
        using JsonDocument document = JsonDocument.Parse(FirstJsonBlock(ContractText()));
        JsonElement stages = document.RootElement.GetProperty("stages");

        List<string> shown = [];
        foreach (JsonElement stage in stages.EnumerateArray())
        {
            foreach (JsonElement placement in stage.GetProperty("placement").EnumerateArray())
            {
                shown.Add(placement.GetString() ?? "");
            }
        }

        List<string> missing = [.. PluginStages.All.Where(p => !shown.Contains(p, StringComparer.Ordinal))];

        await Assert.That(missing).IsEmpty()
            .Because("an example that shows one placement teaches that there is one placement");
    }

    /// <summary>
    /// The example is not merely valid JSON -- it is what the host's own types
    /// read.
    ///
    /// <para>The three tests above check the document from the outside: it parses,
    /// it names the right words. This one checks that the <em>shape</em> matches
    /// the code, which is the failure a plugin author would hit first and a
    /// document review never catches. A handshake can be perfectly good JSON and
    /// still describe a structure the host rejects -- <c>"default": 0.25</c> as a
    /// number was exactly that until the parameter type learned to read it.</para>
    /// </summary>
    [Test]
    public async Task TheHandshakeExampleReadsAsTheHostsOwnHandshakeType()
    {
        HandshakeVerdict verdict = HandshakeReader.Read(
            FirstJsonBlock(ContractText()), "lae-damage", I18NextMessages.Create("en"));

        await Assert.That(verdict.Ok).IsTrue()
            .Because("the example is what an author copies; if the host refuses it, "
                   + "the document is teaching a shape that does not work: "
                   + string.Join(" | ", verdict.Problems));

        PluginHandshake handshake = verdict.Handshake!;
        await Assert.That(handshake.Stages.Count).IsEqualTo(1);

        PluginStageDeclaration stage = handshake.Stages[0];
        await Assert.That(stage.Id).IsEqualTo("damage");
        await Assert.That(stage.Placement.Count).IsEqualTo(PluginStages.All.Count)
            .Because("the example exists to show that one subcommand may sit at every placement");

        PluginParamDeclaration intensity = stage.Params.Single(p => p.Name == "intensity");
        await Assert.That(intensity.Kind).IsEqualTo(PluginParamType.Float);
        await Assert.That(intensity.Min).IsEqualTo(0.0);
        await Assert.That(intensity.Max).IsEqualTo(1.0);

        // The contract writes the default as a bare number; it has to survive as
        // the text a command line can carry.
        await Assert.That(intensity.DefaultValue).IsEqualTo("0.25");

        PluginParamDeclaration seed = stage.Params.Single(p => p.Name == "seed");
        await Assert.That(seed.Kind).IsEqualTo(PluginParamType.Int);
        await Assert.That(seed.DefaultValue).IsEqualTo("0");
    }

    /// <summary>
    /// The first fenced json block of the document, which is the handshake.
    ///
    /// <para>Deliberately positional rather than keyed on a heading: if someone
    /// reorders the document, the block that stops being first is a change worth
    /// noticing, and this test is where it gets noticed.</para>
    /// </summary>
    private static string FirstJsonBlock(string text)
    {
        const string fence = "```json";
        int open = text.IndexOf(fence, StringComparison.Ordinal);
        if (open < 0) throw new InvalidOperationException("the contract has no ```json block");

        int start = text.IndexOf('\n', open);
        int close = text.IndexOf("```", start + 1, StringComparison.Ordinal);
        if (start < 0 || close < 0) throw new InvalidOperationException("the ```json block is not terminated");

        return text[(start + 1)..close];
    }
}
