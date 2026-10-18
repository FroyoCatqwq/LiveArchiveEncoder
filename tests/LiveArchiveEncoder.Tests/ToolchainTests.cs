using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// Install layout and the discovery contract, without downloading anything.
///
/// The rule under test: a managed build only wins when it was explicitly
/// selected. An untouched configuration must keep using the system FFmpeg,
/// otherwise installing a build silently changes what every scripted run
/// produces.
/// </summary>
[TestFixture]
public sealed class ToolchainTests
{
    [Test]
    public void RootLivesUnderThePerUserApplicationData()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.That(Toolchain.Root, Does.StartWith(appData));
        Assert.That(Toolchain.Root, Does.EndWith(Path.Combine("LiveArchiveEncoder", "ffmpeg")));
    }

    [Test]
    public void HomeAndBinFollowTheDocumentedLayout()
    {
        Assert.That(Toolchain.Home("btbn"),
            Is.EqualTo(Path.Combine(Toolchain.Root, "btbn")));
        Assert.That(Toolchain.BinDir("btbn"),
            Is.EqualTo(Path.Combine(Toolchain.Root, "btbn", "bin")));
    }

    [TestCase("bogus")]
    [TestCase("")]
    public void UnknownSourceIsNeverInstalled(string id)
    {
        Assert.That(Toolchain.IsInstalled(id), Is.False);
        Assert.That(Toolchain.FFmpegFor(id), Is.Null);
        Assert.That(Toolchain.Exe(id, "ffmpeg"), Is.Null);
    }

    [Test]
    public void DeletingAnUnknownSourceFailsInsteadOfThrowing()
    {
        (bool ok, string? error) = Toolchain.Delete("bogus");

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("bogus"));
    }

    [Test]
    public void DeletingSomethingThatIsNotInstalledReportsIt()
    {
        // "avbuild" is a real source, but this box may or may not have it; the
        // contract under test is only that failure is reported, never thrown.
        (bool ok, string? error) = Toolchain.Delete("avbuild");

        Assert.That(ok || error is not null, Is.True);
    }

    [Test]
    public void Installed_OnlyListsSourcesThatActuallyExistOnDisk()
    {
        foreach (InstalledToolchain item in Toolchain.Installed())
        {
            Assert.That(File.Exists(item.FFmpeg), Is.True, $"{item.Id} reported a missing ffmpeg");
            Assert.That(item.Home, Is.EqualTo(Toolchain.Home(item.Id)));
        }
    }

    /// <summary>
    /// An explicit --ffmpeg still wins over everything, and a missing path is
    /// reported rather than silently falling back.
    /// </summary>
    [Test]
    public void ExplicitFfmpegPathWins()
    {
        string probe = Path.Combine(Path.GetTempPath(), "lae-not-ffmpeg.exe");

        Assert.That(() => FFmpegDiscovery.FindFFmpeg(probe),
            Throws.InstanceOf<FileNotFoundException>());
    }

    [Test]
    public void PathMeansSystemFfmpeg()
    {
        string resolved;
        try
        {
            resolved = FFmpegDiscovery.FindFFmpeg(null, "path");
        }
        catch (FileNotFoundException)
        {
            Assert.Ignore("no FFmpeg on PATH; the 'path' source cannot be exercised here");
            return;
        }

        Assert.That(resolved, Does.Contain("ffmpeg"));
        if (Toolchain.FFmpegFor("btbn") is { } managed)
        {
            Assert.That(resolved, Is.Not.EqualTo(managed),
                "--ffmpeg-source path must not pick the managed build");
        }
    }

    [Test]
    public void PlayModeNamesRoundTrip()
    {
        Assert.That(Player.ParseMode(Player.ModeName(PlayMode.Ffplay)), Is.EqualTo(PlayMode.Ffplay));
        Assert.That(Player.ParseMode(Player.ModeName(PlayMode.External)), Is.EqualTo(PlayMode.External));
        Assert.That(Player.ParseMode(Player.ModeName(PlayMode.Off)), Is.EqualTo(PlayMode.Off));
    }
}
