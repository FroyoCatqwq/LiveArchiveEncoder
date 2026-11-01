using Lae.Core.Localization;
using Lae.Core.Plugins;
using Lae.Media.Localization;

namespace Lae.Tests.Plugins;

/// <summary>
/// Every way a handshake can be wrong, and the two ways it can be right.
///
/// <para>A plugin is third-party text arriving through a pipe, so the rejection
/// paths are the normal paths. Each one is a literal here rather than a fake
/// executable on disk: the reader is pure, and a test that needed to spawn
/// something to exercise a malformed number would not get run on the machine where
/// it matters.</para>
///
/// <para>The assertions read the finished sentence, not the message key. A reader
/// that returned "plugin.error.handshakeId" to the user would satisfy a key-based
/// assertion and help nobody.</para>
/// </summary>
public sealed class HandshakeReaderTests
{
    private static readonly IMessages En = I18NextMessages.Create("en");

    /// <summary>
    /// A handshake with every optional field present, on one line -- which is what
    /// the contract requires and what every language's default JSON printer emits.
    ///
    /// <para>One line matters beyond tidiness: an indented handshake cannot be told
    /// apart from a handshake followed by log lines, so the protocol fixes it at one
    /// line and ignores the rest of stdout. The reader implements exactly that, and
    /// a test that fed it pretty-printed JSON would be testing a shape no plugin is
    /// allowed to produce.</para>
    /// </summary>
    private static string Handshake(
        string stages = """[{"id":"damage","placement":["post-encode"]}]""",
        int schema = PluginProtocol.SchemaVersion,
        int protocol = PluginProtocol.ProtocolVersion,
        string id = "lae-damage",
        string media = """["video"]""",
        string deterministic = "true")
        => $$"""{"schema":{{schema}},"protocol":{{protocol}},"id":"{{id}}","version":"0.1.0","media":{{media}},"deterministic":{{deterministic}},"stages":{{stages}} }""";

    private static string Problems(HandshakeVerdict verdict) => string.Join(" | ", verdict.Problems);

    private static string Warnings(HandshakeVerdict verdict) => string.Join(" | ", verdict.Warnings);

    [Test]
    public async Task AWellFormedHandshakeIsAccepted()
    {
        HandshakeVerdict verdict = HandshakeReader.Read(Handshake(), "lae-damage", En);

        await Assert.That(verdict.Ok).IsTrue().Because(Problems(verdict));
        await Assert.That(verdict.Problems.Count).IsEqualTo(0);
        await Assert.That(verdict.Warnings.Count).IsEqualTo(0);
        await Assert.That(verdict.Handshake!.Id).IsEqualTo("lae-damage");
        await Assert.That(verdict.Handshake.Stages[0].Id).IsEqualTo("damage");
    }

    /// <summary>
    /// A newer schema is refused by number, whichever way the JSON is shaped.
    ///
    /// <para>Both cases matter. When the shape is compatible the parse succeeds and
    /// the version check catches it; when a field changed shape the parse fails
    /// first, and quoting a JSON error would send the author hunting for a syntax
    /// mistake in JSON they wrote correctly against a later protocol.</para>
    /// </summary>
    [Test]
    [Arguments("""[{"id":"damage","placement":["post-encode"]}]""")]
    [Arguments("""{"damage":{"placement":["post-encode"]}}""")]
    public async Task ANewerSchemaIsRefusedAndBlamesTheVersion(string stages)
    {
        HandshakeVerdict verdict = HandshakeReader.Read(Handshake(stages: stages, schema: 2), "lae-damage", En);

        await Assert.That(verdict.Ok).IsFalse();
        await Assert.That(Problems(verdict)).Contains("schema 2")
            .Because("the author has to know the host is older, not that their JSON is broken");
    }

    [Test]
    public async Task AnUnknownProtocolIsRefused()
    {
        HandshakeVerdict verdict = HandshakeReader.Read(Handshake(protocol: 2), "lae-damage", En);

        await Assert.That(verdict.Ok).IsFalse();
        await Assert.That(Problems(verdict)).Contains("protocol 2");
    }

    /// <summary>
    /// A plugin that calls itself something else is the wrong plugin, however
    /// well-formed it is. This is the one check that catches "I installed the
    /// blur plugin but the chain says damage".
    /// </summary>
    [Test]
    public async Task AHandshakeThatNamesADifferentPluginIsRefused()
    {
        HandshakeVerdict verdict = HandshakeReader.Read(Handshake(id: "lae-blur"), "lae-damage", En);

        await Assert.That(verdict.Ok).IsFalse();
        await Assert.That(Problems(verdict)).Contains("lae-blur");
        await Assert.That(Problems(verdict)).Contains("lae-damage");
    }

    /// <summary>
    /// <c>--plugin &lt;path&gt;</c> has no configuration entry to agree with, so the
    /// comparison is skipped rather than failing every debug run.
    /// </summary>
    [Test]
    public async Task AnEmptyExpectedIdSkipsTheComparison()
    {
        HandshakeVerdict verdict = HandshakeReader.Read(Handshake(id: "anything"), "", En);

        await Assert.That(verdict.Ok).IsTrue().Because(Problems(verdict));
    }

    [Test]
    public async Task AHandshakeThatDeclaresNoStageIsRefused()
    {
        HandshakeVerdict verdict = HandshakeReader.Read(Handshake(stages: "[]"), "lae-damage", En);

        await Assert.That(verdict.Ok).IsFalse();
        await Assert.That(Problems(verdict)).Contains("no stage");
    }

    /// <summary>
    /// A placement this build does not have is reported and ignored -- but only
    /// when another one is usable. A stage with none is a stage that can never run,
    /// and letting it through produces a chain entry that silently does nothing.
    /// </summary>
    [Test]
    public async Task AnUnknownPlacementAloneIsNotEnough()
    {
        HandshakeVerdict verdict = HandshakeReader.Read(
            Handshake(stages: """[{"id":"damage","placement":["pre-encode"]}]"""), "lae-damage", En);

        await Assert.That(verdict.Ok).IsFalse();
        await Assert.That(Problems(verdict)).Contains("pre-encode")
            .Because("the stage was placed somewhere this program cannot put it");
    }

    [Test]
    public async Task AnUnknownPlacementBesideAKnownOneIsAWarning()
    {
        HandshakeVerdict verdict = HandshakeReader.Read(
            Handshake(stages: """[{"id":"damage","placement":["post-encode","mid-roll"]}]"""), "lae-damage", En);

        await Assert.That(verdict.Ok).IsTrue().Because(Problems(verdict));
        await Assert.That(Warnings(verdict)).Contains("mid-roll");
    }

    /// <summary>
    /// Case and blanks in a declared placement are forgiven, because the value
    /// comes from a hand-written plugin; the settings side is canonicalised the
    /// same way, so the two meet.
    /// </summary>
    [Test]
    public async Task APlacementWrittenInAnotherCaseStillCounts()
    {
        HandshakeVerdict verdict = HandshakeReader.Read(
            Handshake(stages: """[{"id":"damage","placement":[" Post-Encode "]}]"""), "lae-damage", En);

        await Assert.That(verdict.Ok).IsTrue().Because(Problems(verdict));
        await Assert.That(verdict.Warnings.Count).IsEqualTo(0);
    }

    /// <summary>
    /// A parameter type the host cannot parse is a value the host cannot validate,
    /// and passing an unvalidated value into a black box is how a bad encode
    /// becomes a mystery.
    /// </summary>
    [Test]
    public async Task AParameterTypeThisBuildCannotValidateIsRefused()
    {
        HandshakeVerdict verdict = HandshakeReader.Read(
            Handshake(stages: """[{"id":"damage","placement":["post-encode"],"params":[{"name":"curve","type":"polynomial"}]}]"""),
            "lae-damage", En);

        await Assert.That(verdict.Ok).IsFalse();
        await Assert.That(Problems(verdict)).Contains("polynomial");
    }

    [Test]
    public async Task ANonDeterministicPluginRunsWithAWarning()
    {
        HandshakeVerdict verdict = HandshakeReader.Read(Handshake(deterministic: "false"), "lae-damage", En);

        await Assert.That(verdict.Ok).IsTrue().Because(Problems(verdict));
        await Assert.That(Warnings(verdict)).Contains("not reproducible");
    }

    [Test]
    public async Task APluginThatOnlyHandlesAudioIsRefused()
    {
        HandshakeVerdict verdict = HandshakeReader.Read(Handshake(media: """["audio"]"""), "lae-damage", En);

        await Assert.That(verdict.Ok).IsFalse();
        await Assert.That(Problems(verdict)).Contains("audio");
    }

    /// <summary>
    /// The contract says the first line is the whole answer. A plugin that prints a
    /// banner first is forgiven; one that prints anything after its JSON is not
    /// punished for it.
    /// </summary>
    [Test]
    public async Task EverythingAfterTheFirstNonEmptyLineIsIgnored()
    {
        string text = "\n\n" + Handshake() + "\n{\"this\":\"is not part of the handshake\"}\nnoise\n";

        HandshakeVerdict verdict = HandshakeReader.Read(text, "lae-damage", En);

        await Assert.That(verdict.Ok).IsTrue().Because(Problems(verdict));
    }

    [Test]
    public async Task AnEmptyOutputIsRefusedWithItsOwnSentence()
    {
        HandshakeVerdict verdict = HandshakeReader.Read("   \n\n", "lae-damage", En);

        await Assert.That(verdict.Ok).IsFalse();
        await Assert.That(Problems(verdict)).Contains("nothing");
    }

    [Test]
    public async Task PlainTextIsRefusedAndTheSentenceQuotesWhatWasWrong()
    {
        HandshakeVerdict verdict = HandshakeReader.Read("lae-damage 0.1.0 ready", "lae-damage", En);

        await Assert.That(verdict.Ok).IsFalse();
        await Assert.That(Problems(verdict)).Contains("not valid JSON");
    }

    /// <summary>
    /// An explicit null in an array survives deserialisation into a list whose
    /// element type is non-nullable, so it has to be checked rather than trusted.
    /// </summary>
    [Test]
    public async Task ANullStageIsReportedRatherThanThrown()
    {
        HandshakeVerdict verdict = HandshakeReader.Read(Handshake(stages: "[null]"), "lae-damage", En);

        await Assert.That(verdict.Ok).IsFalse();
        await Assert.That(Problems(verdict)).Contains("no id");
    }
}
