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
        await Assert.That(suffix.Contains("self-contained")).IsFalse();

        // v7.9.8.13.2: the old assertions compared two strings with EndsWith
        // and were true by construction. Drive the real selector instead.
        string ext = suffix.EndsWith(".zip", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        string scSuffix = suffix.Replace(ext, "-self-contained" + ext);
        var release = new Release
        {
            Tag = "v1.0.0",
            Version = VersionTag.Parse("v1.0.0")!,
            Assets =
            [
                new ReleaseAsset
                {
                    Name = "LiveArchiveEncoder-v1.0.0" + suffix,
                    Url = "https://example.test/framework-dependent", Size = 1,
                },
                new ReleaseAsset
                {
                    Name = "LiveArchiveEncoder-v1.0.0" + scSuffix,
                    Url = "https://example.test/self-contained", Size = 1,
                },
            ],
        };
        await Assert.That(SelfUpdate.Pick(release, suffix)?.Url)
            .IsEqualTo("https://example.test/framework-dependent");
        await Assert.That(SelfUpdate.Pick(release, scSuffix)?.Url)
            .IsEqualTo("https://example.test/self-contained");
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
