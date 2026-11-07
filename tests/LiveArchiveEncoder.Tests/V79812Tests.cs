using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Update;

namespace LiveArchiveEncoder.Tests;

/// <summary>v7.9.8.12: update flavour, RealVideo geometry, occupied outputs.</summary>
public sealed class V79812Tests
{
    [Test]
    public async Task SelfContainedIsDetectedFromTheRuntimeFilesBesideTheApp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lae_scd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await Assert.That(SelfUpdate.IsSelfContained(dir)).IsFalse();
            await File.WriteAllTextAsync(Path.Combine(dir, "coreclr.dll"), "");
            await Assert.That(SelfUpdate.IsSelfContained(dir)).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TheSuffixNamesTheFlavourWithoutColliding()
    {
        string suffix = SelfUpdate.AssetSuffix();
        await Assert.That(suffix.StartsWith('-')).IsTrue();
        await Assert.That(suffix.EndsWith(".zip") || suffix.EndsWith(".tar.gz")).IsTrue();
        // A self-contained archive must never match the framework-dependent
        // suffix, and vice versa -- that was the whole bug.
        string sc = suffix.Replace(".zip", "-self-contained.zip")
            .Replace(".tar.gz", "-self-contained.tar.gz");
        await Assert.That(sc.EndsWith(suffix, StringComparison.Ordinal)).IsFalse();
        await Assert.That(suffix.EndsWith(sc, StringComparison.Ordinal)).IsFalse();
        await Assert.That(suffix.Contains("self-contained")).IsFalse();
    }

    [Test]
    public async Task RvEncodersSnapDownToTheSixteenPixelGrid()
    {
        // libavcodec/rv10enc.c:69 rejects (width | height) & 15.
        (int w, int h, bool exact) = EncoderMath.NormalizeDimensions("rv10", 320, 180);
        await Assert.That((w, h, exact)).IsEqualTo((320, 176, false));
        (int w20, int h20, _) = EncoderMath.NormalizeDimensions("rv20", 320, 180);
        await Assert.That((w20, h20)).IsEqualTo((320, 176));
        (int wk, int hk, bool okExact) = EncoderMath.NormalizeDimensions("rv10", 320, 192);
        await Assert.That((wk, hk, okExact)).IsEqualTo((320, 192, true));
        // An ordinary encoder keeps the user's geometry untouched.
        (int wm, int hm, bool mExact) = EncoderMath.NormalizeDimensions("mpeg4", 320, 180);
        await Assert.That((wm, hm, mExact)).IsEqualTo((320, 180, true));
    }

    [Test]
    public async Task AnOccupiedOutputGetsTheNextFreeName()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lae_alt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var final = new FileInfo(Path.Combine(dir, "clip.rm"));
            await Assert.That(OutputFile.Alternate(final).Name).IsEqualTo("clip (2).rm");
            await File.WriteAllTextAsync(Path.Combine(dir, "clip (2).rm"), "");
            await Assert.That(OutputFile.Alternate(final).Name).IsEqualTo("clip (3).rm");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
