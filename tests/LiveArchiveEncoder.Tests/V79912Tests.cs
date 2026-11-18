using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.Encoding;
using LiveArchiveEncoder.Localization;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.9.12: global bitrate ceiling, all-encoder dynamic fps, and the
/// option/model plumbing those two need.
/// </summary>
public sealed class V79912Tests
{
    [Test]
    public async Task GlobalCapClampsEveryQualityScale()
    {
        List<string> crf = ArgsBuilder.VideoQualityArgs("libx264", 23, 600, maxVideoKbps: 300);
        await Assert.That(crf).Contains("-maxrate");
        await Assert.That(crf[crf.IndexOf("-maxrate") + 1]).IsEqualTo("300k");
        await Assert.That(crf[crf.IndexOf("-bufsize") + 1]).IsEqualTo("600k");

        List<string> bitrate = ArgsBuilder.VideoQualityArgs(
            "libopenh264", 8, 600, maxVideoKbps: 300);
        await Assert.That(bitrate[bitrate.IndexOf("-b:v") + 1]).IsEqualTo("300k");

        List<string> qv = ArgsBuilder.VideoQualityArgs("mjpeg", 8, 600, maxVideoKbps: 300);
        await Assert.That(qv).Contains("-q:v");
        await Assert.That(qv[qv.IndexOf("-maxrate") + 1]).IsEqualTo("300k");
    }

    [Test]
    public async Task ZeroCapLeavesExistingCommandsAlone()
    {
        List<string> plain = ArgsBuilder.VideoQualityArgs("libx264", 23, 600);
        List<string> capped = ArgsBuilder.VideoQualityArgs("libx264", 23, 600, maxVideoKbps: 0);
        await Assert.That(capped).IsEquivalentTo(plain);
    }

    [Test]
    public async Task NewOptionsParseAndAreCatalogued()
    {
        (CliOptions? opts, string? error) = CliParser.Parse(
            ["in.mp4", "--max-video-kbps", "450", "--dynamic-fps"]);
        await Assert.That(error).IsNull();
        await Assert.That(opts).IsNotNull();
        await Assert.That(opts!.MaxVideoKbps).IsEqualTo(450);
        await Assert.That(opts.DynamicFps).IsTrue();

        (CliOptions? off, string? offError) = CliParser.Parse(["in.mp4", "--no-dynamic-fps"]);
        await Assert.That(offError).IsNull();
        await Assert.That(off!.DynamicFps).IsFalse();

        (CliOptions? _, string? bad) = CliParser.Parse(["in.mp4", "--max-video-kbps", "-1"]);
        await Assert.That(bad).IsNotNull();
    }

    [Test]
    public async Task NewParametersExistInBothLanguages()
    {
        foreach (string key in new[]
                 {
                     "param.maxvideokbps", "param.prompt.maxvideokbps",
                     "param.dynamicfps", "param.dynamicfps.on", "param.dynamicfps.off",
                     "encoder.cannotencode",
                 })
        {
            await Assert.That(L.KeysFor(L.English).Contains(key)).IsTrue();
            await Assert.That(L.KeysFor(L.Chinese).Contains(key)).IsTrue();
        }
    }

    [Test]
    public async Task ConfigRoundTripsTheNewFields()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lae-v79912-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "config.json");
            var cfg = new UserConfig
            {
                MaxVideoKbps = 512,
                DynamicFps = true,
            };
            await Assert.That(ConfigStore.Save(cfg, file)).IsTrue();
            UserConfig loaded = ConfigStore.Load(file);
            await Assert.That(loaded.MaxVideoKbps).IsEqualTo(512);
            await Assert.That(loaded.DynamicFps).IsTrue();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task EncoderCapsDefaultToUsable()
    {
        // Cached profiles written before 7.9.9.12 lack the new field; they must
        // remain usable instead of making every encoder look broken.
        var caps = new EncoderCaps { Name = "mjpeg" };
        await Assert.That(caps.CanEncode).IsTrue();
    }
}
