using Lae.Core;

namespace Lae.Tests.Core;

/// <summary>
/// The smallest possible check that Core is wired up and the test runner works.
/// Real coverage lands with the code it covers; this exists so a green suite
/// means something from the first commit.
/// </summary>
public sealed class VersionTests
{
    [Test]
    public async Task VersionHasNoLeadingV()
    {
        // Tags carry the "v" (<c>v8.0.0</c>); the constant must not, because the
        // updater compares parsed tags and a stray prefix silently sorts wrong.
        await Assert.That(LaeVersion.Current.StartsWith('v')).IsFalse();
    }

    [Test]
    public async Task MilestoneBuildsArePrereleases()
    {
        await Assert.That(LaeVersion.IsPrerelease).IsTrue();
    }
}
