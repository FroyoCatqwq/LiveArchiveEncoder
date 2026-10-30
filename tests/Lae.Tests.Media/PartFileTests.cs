using Lae.Media;

namespace Lae.Tests.Media;

/// <summary>
/// The part-file rule: a run writes to a sibling temporary and is renamed only
/// once it has succeeded.
///
/// <para>These are disk tests on purpose -- the whole contract is about what is
/// on disk after a failure. Each one works in its own folder.</para>
/// </summary>
public sealed class PartFileTests
{
    private static string Scratch()
    {
        string folder = Path.Combine(Path.GetTempPath(), "lae-partfile-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Test]
    public async Task KeepsTheRealExtensionAndSaysPart()
    {
        string folder = Scratch();
        try
        {
            string part = PartFile.PathFor(Path.Combine(folder, "clip.mp4"));

            await Assert.That(Path.GetFileName(part)).IsEqualTo("clip.part.mp4");
            await Assert.That(Path.GetDirectoryName(part)).IsEqualTo(Path.GetFullPath(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task PromoteReplacesAnOlderCopy()
    {
        string folder = Scratch();
        try
        {
            string final = Path.Combine(folder, "clip.avi");
            string part = PartFile.PathFor(final);

            File.WriteAllBytes(final, [1, 2, 3]);
            File.WriteAllBytes(part, [4, 5, 6, 7]);

            PartFile.Promote(part, final);

            await Assert.That(File.ReadAllBytes(final)).IsEquivalentTo(new byte[] { 4, 5, 6, 7 });
            await Assert.That(File.Exists(part)).IsFalse();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// FFmpeg can exit 0 and write nothing -- a muxer that rejected every packet
    /// does exactly that. Promoting an empty file would overwrite a good copy with
    /// a zero-byte one.
    /// </summary>
    [Test]
    public async Task RefusesToPromoteAnEmptyOutput()
    {
        string folder = Scratch();
        try
        {
            string final = Path.Combine(folder, "clip.avi");
            string part = PartFile.PathFor(final);
            File.WriteAllBytes(part, []);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
            {
                PartFile.Promote(part, final);
                return Task.CompletedTask;
            });

            await Assert.That(File.Exists(part)).IsTrue();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task DiscardIsQuietWhenThereIsNothingToRemove()
    {
        string folder = Scratch();
        try
        {
            string part = PartFile.PathFor(Path.Combine(folder, "clip.avi"));
            await Assert.That(PartFile.Discard(part)).IsNull();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task DiscardRemovesALeftover()
    {
        string folder = Scratch();
        try
        {
            string part = PartFile.PathFor(Path.Combine(folder, "clip.avi"));
            File.WriteAllBytes(part, [9]);

            await Assert.That(PartFile.Discard(part)).IsNull();
            await Assert.That(File.Exists(part)).IsFalse();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
