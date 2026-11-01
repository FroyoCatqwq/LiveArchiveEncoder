using Lae.Core.Settings;
using Lae.Core.Toolchain;

namespace Lae.Tests.Core;

/// <summary>
/// The download catalogue and the paths it implies, as pure rules.
///
/// <para>Nothing here touches the network or the disk: what is worth pinning is
/// that the table is internally consistent (one entry per id per platform, a URL
/// that matches the archive kind, an archive kind that matches the extension) and
/// that the folder arithmetic puts the install where the settings live. The
/// download itself is exercised by `tools/ci/toolchain-linux.sh` on a real
/// machine, because a test that fakes the network proves nothing about tar.</para>
/// </summary>
public sealed class ToolchainCatalogTests
{
    /// <summary>
    /// The NNEDI3 weights, resolved the same way the FFmpeg install is: an
    /// explicit path wins, the managed folder is the fallback, and a path that is
    /// not there comes back null rather than being passed to the filter.
    ///
    /// <para>The last part is the one worth pinning. A stale path that reached
    /// <c>nnedi=weights=/gone/file</c> would arrive as an FFmpeg sentence about a
    /// file it could not open; null turns into the sentence that says
    /// <c>lae toolchain nnedi</c>, which is the one a user can act on.</para>
    /// </summary>
    [Test]
    public async Task AnInstalledWeightsFileIsFoundInTheManagedFolder()
    {
        string managed = Nnedi.Weights("/cfg");

        await Assert.That(ToolchainSource.NnediWeights(new ToolchainSettings(), "/cfg", p => p == managed))
            .IsEqualTo(managed);
    }

    [Test]
    public async Task AConfiguredFolderIsSearchedForTheFileName()
    {
        string expected = Path.Combine("/home/me/znedi3", Nnedi.WeightsName);
        var settings = new ToolchainSettings { NnediWeights = "/home/me/znedi3" };

        await Assert.That(ToolchainSource.NnediWeights(settings, "/cfg", p => p == expected))
            .IsEqualTo(expected);
    }

    [Test]
    public async Task AConfiguredFileIsTakenAsItIs()
    {
        var settings = new ToolchainSettings { NnediWeights = "/home/me/nnedi3_weights.bin" };

        await Assert.That(ToolchainSource.NnediWeights(settings, "/cfg", p => p == settings.NnediWeights))
            .IsEqualTo("/home/me/nnedi3_weights.bin");
    }

    [Test]
    public async Task AConfiguredPathThatIsGoneIsNotReportedAsConfigured()
    {
        var settings = new ToolchainSettings { NnediWeights = "/gone/nnedi3_weights.bin" };

        await Assert.That(ToolchainSource.NnediWeights(settings, "/cfg", _ => false)).IsNull();
    }

    [Test]
    public async Task NothingConfiguredAndNothingInstalledIsNull()
    {
        await Assert.That(ToolchainSource.NnediWeights(new ToolchainSettings(), "/cfg", _ => false)).IsNull();
    }

    /// <summary>
    /// An id appears at most once per platform. Two "btbn" rows on one platform
    /// would make <see cref="FfmpegCatalog.Find"/> return whichever came first,
    /// and the install would land in a folder the other row disagreed about.
    /// </summary>
    [Test]
    public async Task EachIdAppearsOncePerPlatform()
    {
        var seen = new HashSet<(string Id, DistPlatform Platform)>();

        foreach (FfmpegDist dist in FfmpegCatalog.All)
        {
            await Assert.That(seen.Add((dist.Id, dist.Platform))).IsTrue()
                .Because($"{dist.Id} appears twice for {dist.Platform}");
        }
    }

    /// <summary>
    /// The artefact name and the declared archive kind agree. A .tar.xz announced
    /// as <see cref="ArchiveKind.Zip"/> would fail the magic-byte check at the very
    /// end of an 80 MB download, which is the worst possible place to find out.
    /// </summary>
    [Test]
    public async Task TheUrlMatchesTheArchiveKind()
    {
        foreach (FfmpegDist dist in FfmpegCatalog.All)
        {
            await Assert.That(dist.Url.EndsWith(dist.ArchiveExtension, StringComparison.Ordinal)).IsTrue()
                .Because($"{dist.Id} on {dist.Platform} says {dist.Archive} but points at {dist.Url}");

            await Assert.That(FfmpegCatalog.KindOfArchive(dist.Url)).IsEqualTo(dist.Archive)
                .Because($"the file name of {dist.Id} should be recognised as {dist.Archive}");
        }
    }

    /// <summary>
    /// Both platforms offer the same set of sources. A settings file written on one
    /// machine names a distribution; if the other platform had never heard of it,
    /// the setting would look broken for reasons that have nothing to do with it.
    /// </summary>
    [Test]
    public async Task BothPlatformsOfferTheSameSources()
    {
        string[] windows = [.. FfmpegCatalog.All
            .Where(d => d.Platform == DistPlatform.Windows).Select(d => d.Id).Order(StringComparer.Ordinal)];

        string[] linux = [.. FfmpegCatalog.All
            .Where(d => d.Platform == DistPlatform.Linux).Select(d => d.Id).Order(StringComparer.Ordinal)];

        await Assert.That(string.Join(",", linux)).IsEqualTo(string.Join(",", windows));
    }

    /// <summary>"path" is a choice, not a distribution: it must never turn into a download.</summary>
    [Test]
    public async Task ThePathChoiceIsNotADistribution()
    {
        await Assert.That(FfmpegCatalog.Find(FfmpegCatalog.PathId)).IsNull();
        await Assert.That(FfmpegCatalog.FindKnown(FfmpegCatalog.PathId)).IsNull();
    }

    /// <summary>
    /// An install lives under the settings folder, in its own directory, with its
    /// executables in a <c>bin</c> beside them -- and <c>ffmpeg.exe</c> only on
    /// Windows.
    /// </summary>
    [Test]
    public async Task AnInstallLivesBesideTheSettings()
    {
        string root = Path.Combine("home", "user", "lae");

        await Assert.That(ToolchainLayout.Bin(root, "btbn"))
            .IsEqualTo(Path.Combine(root, "ffmpeg", "btbn", "bin"));

        string tool = ToolchainLayout.Tool(root, "btbn", "ffmpeg");
        await Assert.That(Path.GetFileName(tool))
            .IsEqualTo(OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
    }

    /// <summary>
    /// A distribution id only names a folder when that install is really there.
    /// Pointing a search at a folder that does not exist hides the very problem
    /// <c>lae setup</c> exists to report.
    /// </summary>
    [Test]
    public async Task AConfiguredSourceThatIsNotInstalledFallsBackToPath()
    {
        var settings = new ToolchainSettings { FfmpegSource = FfmpegCatalog.DefaultId };

        await Assert.That(ToolchainSource.Folder(settings, "/cfg", _ => false)).IsNull();
        await Assert.That(ToolchainSource.Folder(settings, "/cfg", _ => true))
            .IsEqualTo(ToolchainLayout.Bin("/cfg", FfmpegCatalog.DefaultId));

        // "path" is the default and means "whatever the machine has".
        await Assert.That(ToolchainSource.Folder(new ToolchainSettings(), "/cfg", _ => true)).IsNull();
    }

    /// <summary>An explicit executable wins over the configured source, and names its folder.</summary>
    [Test]
    public async Task AnExplicitExecutableWins()
    {
        // Both spellings are accepted on every platform: a settings file travels.
        foreach (string tool in new[] { "ffmpeg", "ffmpeg.exe" })
        {
            string executable = Path.Combine("/opt/ff/bin", tool);
            var settings = new ToolchainSettings
            {
                FfmpegSource = FfmpegCatalog.DefaultId,
                FfmpegPath = executable,
            };

            await Assert.That(ToolchainSource.Folder(settings, "/cfg", _ => true))
                .IsEqualTo(Path.GetDirectoryName(executable));
        }

        // A folder is taken as it is.
        var asFolder = new ToolchainSettings { FfmpegPath = "/opt/ff/bin" };
        await Assert.That(ToolchainSource.Folder(asFolder, "/cfg", _ => true)).IsEqualTo("/opt/ff/bin");
    }

    /// <summary>
    /// The GitHub front only rewrites GitHub, and only when one was asked for. A
    /// mirror of one's own must be left exactly as it is.
    /// </summary>
    [Test]
    public async Task TheFrontOnlyWrapsGitHubAddresses()
    {
        FfmpegDist dist = FfmpegCatalog.All.First(d => d.Id == FfmpegCatalog.DefaultId);

        await Assert.That(GitHubFront.Apply(dist.Url, GitHubFront.DefaultPrefix))
            .IsEqualTo(GitHubFront.DefaultPrefix + dist.Url);

        await Assert.That(GitHubFront.Apply(dist.Url, null)).IsEqualTo(dist.Url);
        await Assert.That(GitHubFront.Apply("https://mirror.example/ffmpeg.zip", GitHubFront.DefaultPrefix))
            .IsEqualTo("https://mirror.example/ffmpeg.zip");
    }

    /// <summary>A prefix typed as "gh-proxy.com" is a prefix, not a typo to be rejected.</summary>
    [Test]
    public async Task APrefixIsNormalisedToAnAbsoluteUrlWithOneSlash()
    {
        (string? bare, string? bareError) = GitHubFront.Normalise("gh-proxy.com");
        await Assert.That(bareError).IsNull();
        await Assert.That(bare).IsEqualTo("https://gh-proxy.com/");

        (string? slashed, _) = GitHubFront.Normalise("https://gh-proxy.com/");
        await Assert.That(slashed).IsEqualTo("https://gh-proxy.com/");

        // "off" is a decision, not a URL: it normalises to "no front" rather than
        // to an error, because that is what a settings file stores.
        (string? off, string? offError) = GitHubFront.Normalise(GitHubFront.Off);
        await Assert.That(off).IsNull();
        await Assert.That(offError).IsNull();

        (string? bad, string? badError) = GitHubFront.Normalise("not a url");
        await Assert.That(bad).IsNull();
        await Assert.That(badError).IsNotNull();
    }
}
