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
public sealed class ToolchainTests
{
    [Test]
    public async Task RootLivesUnderThePerUserApplicationData()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        await Assert.That(Toolchain.Root).StartsWith(appData);
        await Assert.That(Toolchain.Root).EndsWith(Path.Combine("LiveArchiveEncoder", "ffmpeg"));
    }

    [Test]
    public async Task HomeAndBinFollowTheDocumentedLayout()
    {
        await Assert.That(Toolchain.Home("btbn")).IsEqualTo(Path.Combine(Toolchain.Root, "btbn"));
        await Assert.That(Toolchain.BinDir("btbn")).IsEqualTo(Path.Combine(Toolchain.Root, "btbn", "bin"));
    }

    [Test]
    [Arguments("bogus")]
    [Arguments("")]
    public async Task UnknownSourceIsNeverInstalled(string id)
    {
        await Assert.That(Toolchain.IsInstalled(id)).IsFalse();
        await Assert.That(Toolchain.FFmpegFor(id)).IsNull();
        await Assert.That(Toolchain.Exe(id, "ffmpeg")).IsNull();
    }

    [Test]
    public async Task DeletingAnUnknownSourceFailsInsteadOfThrowing()
    {
        (bool ok, string? error) = Toolchain.Delete("bogus");

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("bogus");
    }

    [Test]
    public async Task DeletingSomethingThatIsNotInstalledReportsIt()
    {
        // "avbuild" is a real source, but this box may or may not have it; the
        // contract under test is only that failure is reported, never thrown.
        (bool ok, string? error) = Toolchain.Delete("avbuild");

        await Assert.That(ok || error is not null).IsTrue();
    }

    [Test]
    public async Task Installed_OnlyListsSourcesThatActuallyExistOnDisk()
    {
        foreach (InstalledToolchain item in Toolchain.Installed())
        {
            await Assert.That(File.Exists(item.FFmpeg)).IsTrue().Because($"{item.Id} reported a missing ffmpeg");
            await Assert.That(item.Home).IsEqualTo(Toolchain.Home(item.Id));
        }
    }

    /// <summary>
    /// An explicit --ffmpeg still wins over everything, and a missing path is
    /// reported rather than silently falling back.
    /// </summary>
    [Test]
    public async Task ExplicitFfmpegPathWins()
    {
        string probe = Path.Combine(Path.GetTempPath(), "lae-not-ffmpeg.exe");

        await Assert.That(() => FFmpegDiscovery.FindFFmpeg(probe)).Throws<FileNotFoundException>();
    }

    [Test]
    public async Task PathMeansSystemFfmpeg()
    {
        string resolved;
        try
        {
            resolved = FFmpegDiscovery.FindFFmpeg(null, "path");
        }
        catch (FileNotFoundException)
        {
            Skip.Test("no FFmpeg on PATH; the 'path' source cannot be exercised here");
            return;
        }

        await Assert.That(resolved).Contains("ffmpeg");
        if (Toolchain.FFmpegFor("btbn") is { } managed)
        {
            // The contract is that "path" does not consult the managed install
            // -- it resolves through PATH like any other program. When the user
            // put that very build on PATH, both routes legitimately agree, so
            // there is nothing to distinguish here.
            if (IsOnPath(managed))
                Skip.Test("the managed build is on PATH; 'path' and 'btbn' resolve to the same file");

            await Assert.That(resolved).IsNotEqualTo(managed).Because("--ffmpeg-source path must not pick the managed build");
        }
    }

    private static bool IsOnPath(string executable)
    {
        string? dir = Path.GetDirectoryName(executable);
        if (dir is null) return false;

        string? envPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(envPath)) return false;

        foreach (string entry in envPath.Split(Path.PathSeparator))
        {
            if (entry.Length == 0) continue;
            try
            {
                if (string.Equals(Path.GetFullPath(entry), Path.GetFullPath(dir),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase
                                                    : StringComparison.Ordinal))
                    return true;
            }
            catch { /* invalid PATH entry */ }
        }
        return false;
    }

    /// <summary>
    /// The catalogue holds one entry per id per platform, so anything that
    /// enumerates sources for the user has to go through
    /// <see cref="FfmpegCatalog.Available"/> -- otherwise the Windows and Linux
    /// "btbn" both show up and the menu offers a build that cannot be installed.
    /// </summary>
    [Test]
    public async Task SourceListsNeverMixPlatforms()
    {
        foreach (FfmpegDist dist in FfmpegCatalog.Available)
            await Assert.That(dist.Platform).IsEqualTo(FfmpegCatalog.Current);

        string script = Completion.Generate("bash", []);
        await Assert.That(script).Contains("btbn");
        await Assert.That(script.Contains("gyan")).IsEqualTo(FfmpegCatalog.Current == DistPlatform.Windows).Because("gyan.dev publishes no Linux build");
    }

    [Test]
    public async Task PlayModeNamesRoundTrip()
    {
        await Assert.That(Player.ParseMode(Player.ModeName(PlayMode.Ffplay))).IsEqualTo(PlayMode.Ffplay);
        await Assert.That(Player.ParseMode(Player.ModeName(PlayMode.External))).IsEqualTo(PlayMode.External);
        await Assert.That(Player.ParseMode(Player.ModeName(PlayMode.Off))).IsEqualTo(PlayMode.Off);
    }
}
