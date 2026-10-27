using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// The second pass's codec catalog (v7.9.5.1). Before it, a fragment could name
/// any encoder and the container stayed <c>mp4</c>: <c>-c:v libvpx-vp9</c> was
/// accepted on the command line and then failed inside FFmpeg. These tests hold
/// the catalog to what FFmpeg can actually mux.
/// </summary>
public sealed class RecompressCodecTests
{
    [Test]
    public async Task TheCatalogOnlyNamesEncodersThisProjectRuns()
    {
        // Every entry is a codec the second pass is expected to drive; the names
        // are FFmpeg's own, lower case.
        foreach (SecondPassVideoCodec codec in RecompressCodecs.Video)
        {
            await Assert.That(codec.Name).IsNotEmpty();
            await Assert.That(codec.Name).IsEqualTo(codec.Name.ToLowerInvariant());
            await Assert.That(codec.Containers).IsNotEmpty().Because(codec.Name);
            // v7.9.7: an encoder with no quality knob (OpenH264: bitrate only)
            // carries an empty flag and a zero default on purpose -- the preset
            // always drives it with -b:v. Anything else must have a real knob.
            if (codec.QualityFlag.Length == 0)
                await Assert.That(codec.DefaultQuality).IsEqualTo(0).Because(codec.Name);
            else
                await Assert.That(codec.DefaultQuality).IsGreaterThan(0).Because(codec.Name);
        }

        await Assert.That(RecompressCodecs.VideoNames).Contains("libx265");
        await Assert.That(RecompressCodecs.VideoNames).Contains("libvpx-vp9");
        await Assert.That(RecompressCodecs.VideoNames).Contains("libsvtav1");
        await Assert.That(RecompressCodecs.AudioNames).Contains("libopus");
        await Assert.That(RecompressCodecs.AudioNames).Contains("aac");
    }

    [Test]
    public async Task TheWebmFamilyDoesNotLandInMp4()
    {
        // The exact combination that used to be accepted and then failed.
        await Assert.That(RecompressCodecs.ValidateContainer("libvpx-vp9", "libopus", "mp4"))
            .IsNotNull();
        await Assert.That(RecompressCodecs.ValidateContainer("libvpx-vp9", "libopus", "webm"))
            .IsNull();
        await Assert.That(RecompressCodecs.ValidateContainer("libvpx-vp9", "libopus", "mkv"))
            .IsNull();

        // ... and Opus itself is refused in MP4 even with a compatible video codec.
        await Assert.That(RecompressCodecs.ValidateContainer("libx264", "libopus", "mp4"))
            .IsNotNull();
        await Assert.That(RecompressCodecs.ValidateContainer("libx264", "aac", "mp4")).IsNull();
    }

    [Test]
    public async Task TheContainerFollowsThePair()
    {
        await Assert.That(RecompressCodecs.DefaultContainer("libvpx-vp9", "libopus")).IsEqualTo("webm");
        await Assert.That(RecompressCodecs.DefaultContainer("libx265", "aac")).IsEqualTo("mp4");
        await Assert.That(RecompressCodecs.DefaultContainer("flv", "libmp3lame")).IsEqualTo("flv");
        await Assert.That(RecompressCodecs.DefaultContainer("libx264", "flac")).IsEqualTo("mkv");
    }

    [Test]
    public async Task TheRecommendedAudioFollowsTheVideoFamily()
    {
        await Assert.That(RecompressCodecs.RecommendedAudio("libvpx-vp9")).IsEqualTo("libopus");
        await Assert.That(RecompressCodecs.RecommendedAudio("libsvtav1")).IsEqualTo("libopus");
        await Assert.That(RecompressCodecs.RecommendedAudio("libx265")).IsEqualTo("aac");
        await Assert.That(RecompressCodecs.RecommendedAudio("flv")).IsEqualTo("libmp3lame");
    }

    [Test]
    public async Task QualityUsesTheKnobTheCodecUnderstands()
    {
        string x265 = RecompressCodecs.VideoArgs(RecompressCodecs.FindVideo("libx265")!, 30);
        await Assert.That(x265).Contains("-crf 30");
        // HEVC in MP4 has to carry the hvc1 tag or QuickTime refuses it.
        await Assert.That(x265).Contains("-tag:v hvc1");

        string mpeg4 = RecompressCodecs.VideoArgs(RecompressCodecs.FindVideo("mpeg4")!, 4);
        await Assert.That(mpeg4).Contains("-q:v 4");
        await Assert.That(mpeg4).DoesNotContain("-crf");
    }

    [Test]
    public async Task BitrateRenderingDropsTheConstantQualityFlag()
    {
        // VP9 carries "-b:v 0" for CRF mode; a site ladder asking for 1500k must
        // replace it, not end up with both.
        string vp9 = RecompressCodecs.VideoArgsAtBitrate(RecompressCodecs.FindVideo("libvpx-vp9")!, 1500);
        await Assert.That(vp9).Contains("-b:v 1500k");
        await Assert.That(vp9).DoesNotContain("-b:v 0");
        await Assert.That(vp9).DoesNotContain("-crf");
    }

    [Test]
    public async Task AudioArgumentsRespectLosslessAndCopy()
    {
        await Assert.That(RecompressCodecs.AudioArgs("aac", 96, 44100, 2))
            .IsEqualTo("-c:a aac -b:a 96k -ac 2 -ar 44100");
        await Assert.That(RecompressCodecs.AudioArgs("copy", 128, null, null))
            .IsEqualTo("-c:a copy");
        // FLAC is lossless: a bitrate is meaningless to it.
        await Assert.That(RecompressCodecs.AudioArgs("flac", 128, null, null))
            .IsEqualTo("-c:a flac");
    }

    [Test]
    public async Task TheCodecIsFoundInATokenStream()
    {
        List<string> tokens = PostArgs.Tokenize(
            "-vf scale=320:240 -c:v libvpx-vp9 -crf 34 -c:a libopus -b:a 64k");

        await Assert.That(RecompressCodecs.DetectVideoCodec(tokens)).IsEqualTo("libvpx-vp9");
        await Assert.That(RecompressCodecs.DetectAudioCodec(tokens)).IsEqualTo("libopus");

        // Unknown/absent codecs answer null so the caller can skip the check
        // instead of guessing.
        await Assert.That(RecompressCodecs.DetectVideoCodec(PostArgs.Tokenize("-c:v copy"))).IsNull();
        await Assert.That(RecompressCodecs.DetectVideoCodec(PostArgs.Tokenize("-crf 20"))).IsNull();
    }
}

/// <summary>
/// User-written presets (v7.9.5.1): the JSON shape has to round-trip, and a
/// hand-edited file must not be able to describe a combination FFmpeg cannot
/// write.
/// </summary>
[NotInParallel]
public sealed class CustomEraTests
{
    [Test]
    public async Task APresetSurvivesTheSettingsFile()
    {
        EraPreset original = EraPresets.Find("tudou06")!;
        CustomEraConfig stored = EraConfigMap.ToConfig(original);
        EraPreset restored = EraConfigMap.FromConfig(stored)!;

        await Assert.That(restored.Id).IsEqualTo(original.Id);
        await Assert.That(restored.Kind).IsEqualTo(original.Kind);
        await Assert.That(restored.VideoCodec).IsEqualTo(original.VideoCodec);
        await Assert.That(restored.AudioCodec).IsEqualTo(original.AudioCodec);
        await Assert.That(restored.RcWidth).IsEqualTo(original.RcWidth);
        await Assert.That(restored.RcFps).IsEqualTo(original.RcFps);
        await Assert.That(restored.RcVideoKbps).IsEqualTo(original.RcVideoKbps);
        await Assert.That(restored.RecompressContainer).IsEqualTo(original.RecompressContainer);
        await Assert.That(restored.Fragment(native: true)).IsEqualTo(original.Fragment(native: true));
    }

    [Test]
    public async Task IdsBecomeSlugs()
    {
        await Assert.That(EraConfigMap.NormaliseId("My Preset!")).IsEqualTo("my_preset");
        await Assert.That(EraConfigMap.NormaliseId("  2008 ")).IsEqualTo("2008");
        await Assert.That(EraConfigMap.NormaliseId("")).IsEmpty();
        await Assert.That(EraConfigMap.NormaliseId(null)).IsEmpty();
    }

    [Test]
    public async Task ABadFileCannotDescribeAnImpossiblePair()
    {
        // A hand-edited container that the codec pair cannot fill falls back to
        // one that it can, rather than failing at encode time.
        EraPreset fixedUp = EraConfigMap.FromConfig(new CustomEraConfig
        {
            Id = "broken",
            VideoCodec = "libvpx-vp9",
            AudioCodec = "libopus",
            Container = "mp4",
        })!;

        await Assert.That(fixedUp.RecompressContainer).IsEqualTo("webm");

        // An unknown codec becomes libx264/aac rather than a name FFmpeg rejects.
        EraPreset unknown = EraConfigMap.FromConfig(new CustomEraConfig
        {
            Id = "unknown_codec",
            VideoCodec = "libnope",
            AudioCodec = "nope",
        })!;
        await Assert.That(unknown.VideoCodec).IsEqualTo("libx264");
        await Assert.That(unknown.AudioCodec).IsEqualTo("aac");
    }

    [Test]
    public async Task ACustomPresetOverridesABuiltInRow()
    {
        try
        {
            EraPresets.SetCustom([new EraPreset
            {
                Id = "youku07",
                BuiltIn = false,
                Kind = EraKind.Site,
                YearFrom = 2007, YearTo = 2010,
                RcWidth = 512, RcHeight = 384, RcFps = 20,
                RcVideoKbps = 900,
                VideoCodec = "libx264",
                AudioCodec = "aac",
                RecompressContainer = "mp4",
                NoteText = "my own Youku",
            }]);

            EraPreset? found = EraPresets.Find("youku07");
            await Assert.That(found).IsNotNull();
            await Assert.That(found!.BuiltIn).IsFalse();
            await Assert.That(found.RcVideoKbps).IsEqualTo(900);
            // ... and it replaced the row instead of appearing twice.
            await Assert.That(EraPresets.All.Count(p => p.Id == "youku07")).IsEqualTo(1);
        }
        finally
        {
            EraPresets.SetCustom([]);
        }
    }
}
