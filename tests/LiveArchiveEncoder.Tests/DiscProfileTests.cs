using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Localization;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.9.8: the disc targets. The numbers come from the published formats
/// (DVD-Video / White Book VCD / Green Book CD-i / SVCD), so they are pinned
/// here rather than left to drift with a refactor.
/// </summary>
public sealed class DiscProfileTests
{
    [Test]
    public async Task DvdMatchesTheDvdVideoSpecification()
    {
        DiscProfile dvd = DiscProfiles.Dvd;
        await Assert.That(dvd.Encoder).IsEqualTo("mpeg2video");
        await Assert.That(dvd.Container).IsEqualTo("mpeg");
        // PAL 625-line and NTSC 525-line D-1 sizes.
        await Assert.That((dvd.PalWidth, dvd.PalHeight)).IsEqualTo((720, 576));
        await Assert.That((dvd.NtscWidth, dvd.NtscHeight)).IsEqualTo((720, 480));
        await Assert.That(dvd.PalFps).IsEqualTo(25);
        await Assert.That(dvd.NtscFps).IsEqualTo(30);
        // 9.8 Mbit/s is the video-alone ceiling; 8 Mbit/s keeps VBV inside it.
        await Assert.That(dvd.MaxRateKbps).IsLessThanOrEqualTo(9800);
        // AC-3 is 48 kHz, up to 448 kbit/s.
        await Assert.That(dvd.AudioEncoder).IsEqualTo("ac3");
        await Assert.That(dvd.AudioRate).IsEqualTo(48000);
        await Assert.That(dvd.AudioKbps).IsLessThanOrEqualTo(448);
    }

    [Test]
    public async Task VcdMatchesTheVideoCdSpecification()
    {
        DiscProfile vcd = DiscProfiles.Vcd;
        await Assert.That(vcd.Encoder).IsEqualTo("mpeg1video");
        await Assert.That((vcd.PalWidth, vcd.PalHeight)).IsEqualTo((352, 288));
        await Assert.That((vcd.NtscWidth, vcd.NtscHeight)).IsEqualTo((352, 240));
        // 1150 kbit/s video, 224 kbit/s MP2 at 44.1 kHz.
        await Assert.That(vcd.VideoKbps).IsEqualTo(1150);
        await Assert.That(vcd.MaxRateKbps).IsEqualTo(1150);
        await Assert.That(vcd.AudioEncoder).IsEqualTo("mp2");
        await Assert.That(vcd.AudioKbps).IsEqualTo(224);
        await Assert.That(vcd.AudioRate).IsEqualTo(44100);
        await Assert.That(vcd.AudioChannels).IsEqualTo(2);
    }

    [Test]
    public async Task CdiAndSvcdExistAsSeparateTargets()
    {
        // Green Book CD-i uses the same MPEG-1 envelope as VCD; it is a distinct
        // id so a CD-i target can be named, and it must not silently drift into
        // an MPEG-2 profile.
        DiscProfile cd = DiscProfiles.Cd;
        await Assert.That(cd.Id).IsEqualTo("cd");
        await Assert.That(cd.Encoder).IsEqualTo("mpeg1video");
        await Assert.That(cd.VideoKbps).IsEqualTo(DiscProfiles.Vcd.VideoKbps);

        DiscProfile svcd = DiscProfiles.Svcd;
        await Assert.That(svcd.Encoder).IsEqualTo("mpeg2video");
        await Assert.That(svcd.VideoKbps).IsGreaterThan(DiscProfiles.Vcd.VideoKbps);
        await Assert.That(svcd.VideoKbps).IsLessThan(DiscProfiles.Dvd.VideoKbps);
    }

    [Test]
    public async Task AliasesResolveToTheSameProfile()
    {
        await Assert.That(DiscProfiles.Find("cd-i")?.Id).IsEqualTo("cd");
        await Assert.That(DiscProfiles.Find("CDI")?.Id).IsEqualTo("cd");
        await Assert.That(DiscProfiles.Find("DVD")?.Id).IsEqualTo("dvd");
        await Assert.That(DiscProfiles.Find(" nope ")?.Id).IsNull();
        await Assert.That(DiscProfiles.Find(null)).IsNull();
    }

    [Test]
    public async Task EncoderArgsCarryTheVbvPairAndGop()
    {
        // FFmpeg's MPEG encoders refuse a half VBV pair; both halves always ship.
        foreach (DiscProfile p in DiscProfiles.All)
        {
            await Assert.That(p.EncoderArgs.Contains("-maxrate", StringComparison.Ordinal)).IsTrue();
            await Assert.That(p.EncoderArgs.Contains("-bufsize", StringComparison.Ordinal)).IsTrue();
            await Assert.That(p.EncoderArgs.Contains("-g ", StringComparison.Ordinal)).IsTrue();
            await Assert.That(p.EncoderArgs.Contains("-c:v", StringComparison.Ordinal)).IsFalse();
            await Assert.That(FragmentEditor.ValidateVbv(p.EncoderArgs)).IsNull();
        }
    }

    [Test]
    public async Task SizesFollowTheTelevisionStandard()
    {
        await Assert.That(DiscProfiles.Dvd.WidthFor(25)).IsEqualTo(720);
        await Assert.That(DiscProfiles.Dvd.HeightFor(25)).IsEqualTo(576);
        await Assert.That(DiscProfiles.Dvd.WidthFor(30)).IsEqualTo(720);
        await Assert.That(DiscProfiles.Dvd.HeightFor(30)).IsEqualTo(480);
        await Assert.That(DiscProfiles.Vcd.WidthFor(25)).IsEqualTo(352);
        await Assert.That(DiscProfiles.Vcd.HeightFor(30)).IsEqualTo(240);
    }

    [Test]
    public async Task OnlyIsoAndImgAreTreatedAsDiscImages()
    {
        await Assert.That(DiscImages.LooksLikeImage("movie.iso")).IsTrue();
        await Assert.That(DiscImages.LooksLikeImage("MOVIE.ISO")).IsTrue();
        await Assert.That(DiscImages.LooksLikeImage(@"M:\shares\disc.img")).IsTrue();
        await Assert.That(DiscImages.LooksLikeImage("movie.mkv")).IsFalse();
        await Assert.That(DiscImages.LooksLikeImage(null)).IsFalse();
        // A plain file is not a disc image: no error, no wrapper.
        (DiscImage? none, string? noneError) = DiscImages.Resolve("tests/nonexistent.mkv");
        await Assert.That(none).IsNull();
        await Assert.That(noneError).IsNull();
    }

    [Test]
    public async Task AnUnreadableImageIsRefusedWithAReason()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "lae-not-a-disc-" + Guid.NewGuid().ToString("N") + ".iso");
        try
        {
            File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03]);
            (DiscImage? image, string? error) = DiscImages.Resolve(path);
            await Assert.That(image).IsNull();
            await Assert.That(error).IsNotNull();
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Test]
    public async Task NewDiscKeysExistInBothLanguages()
    {
        foreach (string key in new[]
                 {
                     "disc.menu", "disc.prompt", "disc.applied", "disc.unknown",
                     "disc.encoderhint", "disc.codecoverride", "disc.encodermissing",
                     "disc.kind.dvd", "disc.kind.vcd", "disc.no7zip",
                     "disc.unreadable", "disc.notsupported", "disc.extractfailed",
                 })
        {
            await Assert.That(L.KeysFor(L.English).Contains(key)).IsTrue();
            await Assert.That(L.KeysFor(L.Chinese).Contains(key)).IsTrue();
        }
    }
}
