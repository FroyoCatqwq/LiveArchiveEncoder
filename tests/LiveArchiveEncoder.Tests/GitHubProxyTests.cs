using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.3: the GitHub front.
///
/// Two rules carry the whole feature. The prefix is only ever prepended, never
/// substituted, so a mirror or a vendor host keeps working untouched; and a
/// value that does not parse is an error, because the one outcome the switch
/// exists to prevent is "it quietly downloaded from GitHub anyway".
/// </summary>
public sealed class GitHubProxyTests
{
    [Test]
    public async Task AMissingSchemeBecomesHttps()
    {
        (string? prefix, string? error) = GitHubProxy.Normalise("gh-proxy.com");

        await Assert.That(error).IsNull();
        await Assert.That(prefix).IsEqualTo("https://gh-proxy.com/");
    }

    [Test]
    public async Task TheTrailingSlashIsAddedOnce()
    {
        (string? bare, _) = GitHubProxy.Normalise("https://gh-proxy.com");
        (string? slashed, _) = GitHubProxy.Normalise("https://gh-proxy.com/");

        await Assert.That(bare).IsEqualTo(slashed);
        await Assert.That(slashed).IsEqualTo("https://gh-proxy.com/");
    }

    [Test]
    public async Task AQueryStringIsRefusedRatherThanFlattened()
    {
        // "https://x/?url=" looks like a prefix and would produce
        // "https://x/?url=https://github.com/..." -- which some fronts accept and
        // silently drop the tail of.
        (string? prefix, string? error) = GitHubProxy.Normalise("https://x/?url=");

        await Assert.That(prefix).IsNull();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task OffMeansNoFrontAndNoError()
    {
        // The settings file stores this literal to remember "do not ask again";
        // parsing it as a URL would produce "https://off/".
        (string? prefix, string? error) = GitHubProxy.Normalise(GitHubProxy.Off);

        await Assert.That(error).IsNull();
        await Assert.That(prefix).IsNull();
    }

    [Test]
    public async Task OnlyGitHubHostsAreWrapped()
    {
        const string prefix = "https://gh-proxy.com/";

        await Assert.That(GitHubProxy.Apply("https://raw.githubusercontent.com/o/r/m/f.bin", prefix))
            .IsEqualTo(prefix + "https://raw.githubusercontent.com/o/r/m/f.bin");
        await Assert.That(GitHubProxy.Apply("https://api.github.com/repos/o/r/releases/latest", prefix))
            .IsEqualTo(prefix + "https://api.github.com/repos/o/r/releases/latest");
        // A release asset redirects through objects.githubusercontent.com, which
        // is why that host is on the list as well.
        await Assert.That(GitHubProxy.Apply("https://objects.githubusercontent.com/x/y", prefix))
            .IsEqualTo(prefix + "https://objects.githubusercontent.com/x/y");

        // Everything else stays as it was: BtbN, gyan.dev, SourceForge and any
        // local mirror must not be routed through a GitHub front.
        await Assert.That(GitHubProxy.Apply("https://github.com.evil.example/f", prefix))
            .IsEqualTo("https://github.com.evil.example/f");
        await Assert.That(GitHubProxy.Apply("https://www.gyan.dev/ffmpeg/builds/x.7z", prefix))
            .IsEqualTo("https://www.gyan.dev/ffmpeg/builds/x.7z");
    }

    [Test]
    public async Task NoPrefixLeavesTheUrlAlone()
    {
        await Assert.That(GitHubProxy.Apply("https://api.github.com/repos/o/r", null))
            .IsEqualTo("https://api.github.com/repos/o/r");
        await Assert.That(GitHubProxy.Apply("https://api.github.com/repos/o/r", ""))
            .IsEqualTo("https://api.github.com/repos/o/r");
    }
}
