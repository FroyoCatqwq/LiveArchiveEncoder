using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>Post-encode playback selection. Nothing here launches a process.</summary>
[TestFixture]
public sealed class PlayerTests
{
    [TestCase("off", PlayMode.Off)]
    [TestCase("ffplay", PlayMode.Ffplay)]
    [TestCase("external", PlayMode.External)]
    [TestCase("EXT", PlayMode.External)]
    [TestCase("  ffplay  ", PlayMode.Ffplay)]
    public void ParseMode_UnderstandsTheThreeModes(string raw, PlayMode expected) =>
        Assert.That(Player.ParseMode(raw), Is.EqualTo(expected));

    [TestCase("bogus")]
    [TestCase("")]
    [TestCase(null)]
    public void ParseMode_AnythingUnknownIsOff(string? raw) =>
        Assert.That(Player.ParseMode(raw), Is.EqualTo(PlayMode.Off));

    [Test]
    public void ModeName_IsWhatTheParserAcceptsBack()
    {
        Assert.That(Player.ModeName(PlayMode.Off), Is.EqualTo("off"));
        Assert.That(Player.ModeName(PlayMode.Ffplay), Is.EqualTo("ffplay"));
        Assert.That(Player.ModeName(PlayMode.External), Is.EqualTo("external"));
    }

    [Test]
    public void Describe_OffIsJustOff() =>
        Assert.That(Player.Describe(PlayMode.Off, null, null), Is.EqualTo("off"));

    [Test]
    public void Describe_FfplayNamesTheBinaryWhenItIsMissing()
    {
        Assert.That(Player.Describe(PlayMode.Ffplay, null, null),
            Is.EqualTo("ffplay (not found)"));
        Assert.That(Player.Describe(PlayMode.Ffplay, null, @"C:\ff\ffplay.exe"),
            Is.EqualTo(@"C:\ff\ffplay.exe"));
    }

    [Test]
    public void Describe_ExternalFallsBackToTheShellAssociation()
    {
        Assert.That(Player.Describe(PlayMode.External, null, null),
            Is.EqualTo("external (system default player)"));
        Assert.That(Player.Describe(PlayMode.External, @"C:\mpc\mpc-hc64.exe", null),
            Is.EqualTo(@"C:\mpc\mpc-hc64.exe"));
    }

    [Test]
    public void Launch_OffDoesNothingEvenWhenTheFileIsGone()
    {
        (bool ok, string? error) = Player.Launch(
            PlayMode.Off, @"C:\definitely\not\here.rm", "ffmpeg", null);

        Assert.That(ok, Is.False);
        Assert.That(error, Is.Null, "off is a no-op, not an error");
    }

    [Test]
    public void Launch_MissingOutputIsReportedNotThrown()
    {
        (bool ok, string? error) = Player.Launch(
            PlayMode.Ffplay, @"C:\definitely\not\here.rm", "ffmpeg", null);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("output file is gone"));
    }

    [Test]
    public void Launch_ExternalPlayerThatDoesNotExistIsReported()
    {
        string file = Path.Combine(Path.GetTempPath(), "lae-play-" + Guid.NewGuid().ToString("N") + ".rm");
        File.WriteAllText(file, "x");

        try
        {
            (bool ok, string? error) = Player.Launch(
                PlayMode.External, file, "ffmpeg", @"C:\definitely\not\player.exe");

            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("external player not found"));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
