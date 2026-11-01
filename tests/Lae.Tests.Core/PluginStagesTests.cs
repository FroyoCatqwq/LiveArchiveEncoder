using Lae.Core.Plugins;
using Lae.Core.Settings;

namespace Lae.Tests.Core;

/// <summary>
/// The stage vocabulary, before any plugin code exists to depend on it.
///
/// <para>The first version of this list was written twice -- once in the
/// blueprint's handshake example, once in the settings type -- and the two
/// disagreed. The settings one offered <c>pre-encode</c>, which a live capture
/// cannot honour because its source is a stream rather than a file. A test is
/// what keeps the third list from appearing during M3.</para>
/// </summary>
public sealed class PluginStagesTests
{
    [Test]
    public async Task ThereAreExactlyThreeStagesAndNoneOfThemIsPreEncode()
    {
        await Assert.That(PluginStages.All.Count).IsEqualTo(3);
        await Assert.That(PluginStages.All).DoesNotContain("pre-encode");
        await Assert.That(PluginStages.All).Contains(PluginStages.PostEncode);
        await Assert.That(PluginStages.All).Contains(PluginStages.BetweenPasses);
        await Assert.That(PluginStages.All).Contains(PluginStages.AfterChain);
    }

    /// <summary>
    /// The names are the wire format: the handshake, the settings file and the
    /// event stream all spell them the same way, so a rename here is a protocol
    /// change and the test is where that is noticed.
    /// </summary>
    [Test]
    public async Task TheNamesAreTheOnesTheProtocolPublishes()
    {
        await Assert.That(PluginStages.PostEncode).IsEqualTo("post-encode");
        await Assert.That(PluginStages.BetweenPasses).IsEqualTo("between-passes");
        await Assert.That(PluginStages.AfterChain).IsEqualTo("after-chain");
    }

    [Test]
    [Arguments("post-encode", true)]
    [Arguments("between-passes", true)]
    [Arguments("after-chain", true)]
    [Arguments(" Post-Encode ", true)]
    [Arguments("BETWEEN-PASSES", true)]
    [Arguments("pre-encode", false)]
    [Arguments("post_encode", false)]
    [Arguments("after-chain ", true)]
    [Arguments("", false)]
    [Arguments("   ", false)]
    [Arguments(null, false)]
    public async Task OnlyTheThreeAreKnownAndSpellingIsForgiven(string? stage, bool known)
    {
        await Assert.That(PluginStages.IsKnown(stage)).IsEqualTo(known);
    }

    /// <summary>
    /// A declared list is repaired, not filtered: blanks and duplicates go,
    /// unknown names stay so the host can name them.
    /// </summary>
    [Test]
    public async Task NormalisingRepairsSpellingAndKeepsUnknownNames()
    {
        IReadOnlyList<string> stages =
            PluginStages.Normalise([" Post-Encode ", "", "  ", "between-passes", "BETWEEN-PASSES", "pre-encode"]);

        await Assert.That(string.Join(",", stages)).IsEqualTo("post-encode,between-passes,pre-encode")
            .Because("order is the declaration order, duplicates collapse, and the impossible stage survives to be reported");
    }

    [Test]
    public async Task ANullListBecomesAnEmptyOne()
    {
        await Assert.That(PluginStages.Normalise(null)).IsEmpty();
    }

    /// <summary>
    /// The end-to-end shape that matters: a hand-edited file gets canonical
    /// stages, and a stage this build cannot run is still visible afterwards --
    /// which is what lets the host refuse with a sentence instead of running
    /// nothing.
    /// </summary>
    [Test]
    public async Task TheSettingsDocumentCanonicalisesStagesAndKeepsTheImpossibleOnes()
    {
        var document = new SettingsDocument
        {
            Plugins =
            [
                new PluginRegistration
                {
                    Id = "lae-damage",
                    Command = "lae-damage",
                    Stages = [" Post-Encode", "pre-encode", "post-encode"],
                },
            ],
        };

        SettingsDocument normalised = document.Normalised();
        PluginRegistration plugin = normalised.Plugins[0];

        await Assert.That(string.Join(",", plugin.Stages)).IsEqualTo("post-encode,pre-encode");
        await Assert.That(string.Join(",", plugin.UnknownStages)).IsEqualTo("pre-encode");
    }
}
