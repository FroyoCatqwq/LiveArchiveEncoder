using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.11: installing FFmpeg has to reach cmd / pwsh / bash. Only the pure
/// text transforms are tested here -- no test may touch the developer's real
/// user PATH or shell startup files.
/// </summary>
public sealed class ToolchainPathTests
{
    private const string Dir = "/opt/lae/ffmpeg/btbn/bin";
    private const string Other = "/opt/lae/ffmpeg/nonfree/bin";

    private static char Sep => ToolchainPath.Separator;

    [Test]
    public async Task AddToPathAppendsOnceAndKeepsEveryOtherEntry()
    {
        string existing = $"/usr/bin{Sep}/bin";
        string added = ToolchainPath.AddToPath(existing, Dir, Sep);

        await Assert.That(added).IsEqualTo($"{existing}{Sep}{Dir}");
        await Assert.That(ToolchainPath.AddToPath(added, Dir, Sep)).IsEqualTo(added);
        await Assert.That(ToolchainPath.AddToPath("/usr/bin" + Sep, Dir, Sep))
            .IsEqualTo($"/usr/bin{Sep}{Dir}");
        await Assert.That(ToolchainPath.AddToPath("", Dir, Sep)).IsEqualTo(Dir);
    }

    [Test]
    public async Task RemoveFromPathDropsOnlyOurEntry()
    {
        string path = $"/usr/bin{Sep}{Dir}{Sep}/usr/local/bin";
        await Assert.That(ToolchainPath.RemoveFromPath(path, Dir, Sep))
            .IsEqualTo($"/usr/bin{Sep}/usr/local/bin");
        await Assert.That(ToolchainPath.RemoveFromPath($"/usr/bin{Sep}/bin", Dir, Sep))
            .IsEqualTo($"/usr/bin{Sep}/bin");
    }

    [Test]
    public async Task ProfileBlockReplacesItselfOnSwitchAndPreservesUserLines()
    {
        const string original = "export EDITOR=vim\n";
        string first = ToolchainPath.InsertProfileBlock(original, Dir);

        await Assert.That(first.Contains(ToolchainPath.BeginMarker)).IsTrue();
        await Assert.That(first.Contains($"export PATH=\"{Dir}:$PATH\"")).IsTrue();
        await Assert.That(first.Contains("export EDITOR=vim")).IsTrue();

        // Installing or switching a distribution must never stack blocks.
        string second = ToolchainPath.InsertProfileBlock(first, Other);
        await Assert.That(second.Split('\n').Count(x => x == ToolchainPath.BeginMarker)).IsEqualTo(1);
        await Assert.That(second.Contains($"export PATH=\"{Other}:$PATH\"")).IsTrue();
        await Assert.That(second.Contains(Dir)).IsFalse();

        string cleaned = ToolchainPath.RemoveProfileBlock(second);
        await Assert.That(cleaned.Contains("export EDITOR=vim")).IsTrue();
        await Assert.That(cleaned.Contains(ToolchainPath.BeginMarker)).IsFalse();
        await Assert.That(cleaned.Contains("export PATH")).IsFalse();
    }

    [Test]
    [Arguments("cmd", "set \"PATH=")]
    [Arguments("powershell", "$env:PATH")]
    [Arguments("pwsh", "$env:PATH")]
    [Arguments("bash", "export PATH=")]
    [Arguments("zsh", "export PATH=")]
    [Arguments("fish", "set -gx PATH")]
    public async Task ActivateLineCoversEveryShell(string shell, string prefix)
    {
        string line = ToolchainPath.ActivateLine(shell, Dir);
        await Assert.That(line.StartsWith(prefix, StringComparison.Ordinal)).IsTrue();
        await Assert.That(line.Contains(Dir)).IsTrue();
        await Assert.That(ToolchainPath.IsShell(shell)).IsTrue();
    }

    [Test]
    public async Task UnknownShellFallsBackToPosixAndNeverLooksLikeAFlag()
    {
        await Assert.That(ToolchainPath.ActivateLine("auto", Dir))
            .IsEqualTo($"export PATH=\"{Dir}:$PATH\"");
        await Assert.That(ToolchainPath.IsShell("--quiet")).IsFalse();
        await Assert.That(ToolchainPath.IsShell(null)).IsFalse();
    }
}
