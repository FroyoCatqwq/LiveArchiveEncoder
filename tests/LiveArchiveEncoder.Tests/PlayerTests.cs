using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>Post-encode playback selection. Nothing here launches a process.</summary>
public sealed class PlayerTests
{
    [Test]
    [Arguments("off", PlayMode.Off)]
    [Arguments("ffplay", PlayMode.Ffplay)]
    [Arguments("external", PlayMode.External)]
    [Arguments("EXT", PlayMode.External)]
    [Arguments("  ffplay  ", PlayMode.Ffplay)]
    public async Task ParseMode_UnderstandsTheThreeModes(string raw, PlayMode expected) =>
        await Assert.That(Player.ParseMode(raw)).IsEqualTo(expected);

    [Test]
    [Arguments("bogus")]
    [Arguments("")]
    [Arguments(null)]
    public async Task ParseMode_AnythingUnknownIsOff(string? raw) =>
        await Assert.That(Player.ParseMode(raw)).IsEqualTo(PlayMode.Off);

    [Test]
    public async Task ModeName_IsWhatTheParserAcceptsBack()
    {
        await Assert.That(Player.ModeName(PlayMode.Off)).IsEqualTo("off");
        await Assert.That(Player.ModeName(PlayMode.Ffplay)).IsEqualTo("ffplay");
        await Assert.That(Player.ModeName(PlayMode.External)).IsEqualTo("external");
    }

    [Test]
    public async Task Describe_OffIsJustOff() =>
        await Assert.That(Player.Describe(PlayMode.Off, null, null)).IsEqualTo("off");

    [Test]
    public async Task Describe_FfplayNamesTheBinaryWhenItIsMissing()
    {
        await Assert.That(Player.Describe(PlayMode.Ffplay, null, null)).IsEqualTo("ffplay (not found)");
        await Assert.That(Player.Describe(PlayMode.Ffplay, null, @"C:\ff\ffplay.exe")).IsEqualTo(@"C:\ff\ffplay.exe");
    }

    [Test]
    public async Task Describe_ExternalFallsBackToTheShellAssociation()
    {
        await Assert.That(Player.Describe(PlayMode.External, null, null)).IsEqualTo("external (system default player)");
        await Assert.That(Player.Describe(PlayMode.External, @"C:\mpc\mpc-hc64.exe", null)).IsEqualTo(@"C:\mpc\mpc-hc64.exe");
    }

    [Test]
    public async Task Launch_OffDoesNothingEvenWhenTheFileIsGone()
    {
        (bool ok, string? error) = Player.Launch(
            PlayMode.Off, @"C:\definitely\not\here.rm", "ffmpeg", null);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsNull().Because("off is a no-op, not an error");
    }

    [Test]
    public async Task Launch_MissingOutputIsReportedNotThrown()
    {
        (bool ok, string? error) = Player.Launch(
            PlayMode.Ffplay, @"C:\definitely\not\here.rm", "ffmpeg", null);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("output file is gone");
    }

    [Test]
    public async Task Launch_ExternalPlayerThatDoesNotExistIsReported()
    {
        string file = Path.Combine(Path.GetTempPath(), "lae-play-" + Guid.NewGuid().ToString("N") + ".rm");
        File.WriteAllText(file, "x");

        try
        {
            (bool ok, string? error) = Player.Launch(
                PlayMode.External, file, "ffmpeg", @"C:\definitely\not\player.exe");

            await Assert.That(ok).IsFalse();
            await Assert.That(error).Contains("external player not found");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
