using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.6.1: an era carries its <b>ladder</b>, not just a base frame rate.
///
/// <para>A preset that only set <c>Fps</c> left the tool's default
/// 13/8/5/3 rungs in place, so <c>--era mp4_10</c> encoded at 25 fps and then
/// dropped straight onto 13 -- half the frame rate in one step, which no period
/// encoder did. The ladder is part of what makes a preset look like its year,
/// so it lives in the preset and is pinned here.</para>
/// </summary>
public sealed class EraFpsLadderTests
{
    /// <summary>The five eras and the ladder each one ran, highest first.</summary>
    private static readonly Dictionary<string, double[]> Expected = new()
    {
        ["dialup98"] = [10, 8, 5, 3],
        ["broadband03"] = [15, 12, 10, 8, 5],
        ["flv07"] = [15, 12, 10, 8, 5],
        ["mp4_10"] = [25, 20, 15, 10],
        ["hd14"] = [30, 25, 20, 15],
    };

    [Test]
    public async Task EveryEraCarriesTheLadderOfItsPeriod()
    {
        foreach ((string id, double[] levels) in Expected)
        {
            EraPreset preset = EraPresets.Find(id)!;
            await Assert.That(preset.FpsLevels).IsNotNull().Because($"{id} must carry a ladder");
            await Assert.That(preset.FpsLevels!.Length).IsEqualTo(levels.Length).Because(id);
            for (int i = 0; i < levels.Length; i++)
                await Assert.That(preset.FpsLevels[i]).IsEqualTo(levels[i]).Because($"{id} rung {i}");
        }
    }

    [Test]
    public async Task ALadderStartsAtTheBaseRateAndNeverGoesBackUp()
    {
        foreach (EraPreset era in EraPresets.WithLiveLayer)
        {
            double[] levels = era.FpsLevels ?? [];
            await Assert.That(levels.Length).IsGreaterThanOrEqualTo(2).Because(era.Id);
            await Assert.That(levels[0]).IsEqualTo(era.Fps!.Value).Because($"{era.Id} starts at its base rate");
            for (int i = 1; i < levels.Length; i++)
            {
                await Assert.That(levels[i]).IsLessThan(levels[i - 1])
                    .Because($"{era.Id} must descend: {string.Join(",", levels)}");
                await Assert.That(levels[i]).IsGreaterThan(0.0).Because(era.Id);
            }
        }
    }

    /// <summary>
    /// v7.9.6.2: a site preset owns the live layer, so it carries a ladder too.
    ///
    /// <para>The capture side of a 2007 portal is as period-specific as its
    /// delivery side: selecting <c>--era tudou06</c> has to record 352x264 at
    /// 12 fps, not leave the user's 640x480@25 master alone while the second
    /// pass claims otherwise.</para>
    /// </summary>
    [Test]
    public async Task EverySitePresetCarriesItsLadder()
    {
        foreach (EraPreset site in EraPresets.Sites)
        {
            await Assert.That(site.HasLive).IsTrue().Because(site.Id);
            await Assert.That(site.FpsLevels).IsNotNull().Because(site.Id);
            await Assert.That(site.FpsLevels![0]).IsEqualTo(site.Fps!.Value).Because(site.Id);
            for (int i = 1; i < site.FpsLevels.Length; i++)
                await Assert.That(site.FpsLevels[i]).IsLessThan(site.FpsLevels[i - 1]).Because(site.Id);
        }
    }

    [Test]
    public async Task ApplyingAnEraReplacesTheDefaultLadder()
    {
        var config = new Config { Fps = 13, FpsLevels = [13, 8, 5, 3] };

        EraPresets.Find("mp4_10")!.ApplyLive(config);

        await Assert.That(config.Fps).IsEqualTo(25.0);
        await Assert.That(config.FpsLevels).IsEquivalentTo(new[] { 25.0, 20.0, 15.0, 10.0 });
    }

    /// <summary>
    /// Pinned per-rung thresholds are keyed by FPS. A rung that is not on the
    /// new ladder must not survive and be silently applied to another rate.
    /// </summary>
    [Test]
    public async Task ApplyingAnEraDropsThresholdsForRungsItNoLongerHas()
    {
        var config = new Config
        {
            FpsLevels = [13, 8, 5, 3],
            FpsLevelKbps =
            [
                new FpsRungSetting { Fps = 8, DropAboveKbps = 128, RiseBelowKbps = 110 },
                new FpsRungSetting { Fps = 5, DropAboveKbps = 150, RiseBelowKbps = 130 },
            ],
        };

        EraPresets.Find("hd14")!.ApplyLive(config);

        await Assert.That(config.FpsLevels).IsEquivalentTo(new[] { 30.0, 25.0, 20.0, 15.0 });
        await Assert.That(config.FpsLevelKbps).IsEmpty();
    }

    /// <summary>A preset written before the field existed still works: derive.</summary>
    [Test]
    public async Task AnEraWithoutALadderDerivesOneFromItsBaseRate()
    {
        var config = new Config { Fps = 13, FpsLevels = [13, 8, 5, 3] };
        var custom = new EraPreset
        {
            Id = "mine",
            BuiltIn = false,
            Width = 320, Height = 240,
            Fps = 20,
            LimitKbps = 400,
        };

        custom.ApplyLive(config);

        await Assert.That(config.Fps).IsEqualTo(20.0);
        // Base first, then the default rungs that still fit under it.
        await Assert.That(config.FpsLevels).IsEquivalentTo(new[] { 20.0, 13.0, 8.0, 5.0, 3.0 });
    }

    /// <summary>
    /// A ladder the user pinned in a settings file must survive save/load; a
    /// hand-edited non-monotonic one is cleaned instead of trusted.
    /// </summary>
    [Test]
    public async Task ACustomEraKeepsItsLadderAcrossTheSettingsFile()
    {
        EraPreset preset = EraConfigMap.FromConfig(new CustomEraConfig
        {
            Id = "my_2008",
            Kind = "era",
            Fps = 15,
            FpsLevels = [15, 12, 10, 8, 5],
            Width = 320, Height = 240,
            RcWidth = 320, RcHeight = 240, RcFps = 15,
            RcVideoKbps = 300,
            VideoCodec = "libx264",
            AudioCodec = "aac",
            Container = "mp4",
        })!;

        await Assert.That(preset.FpsLevels).IsEquivalentTo(new[] { 15.0, 12.0, 10.0, 8.0, 5.0 });
        await Assert.That(EraConfigMap.ToConfig(preset).FpsLevels)
            .IsEquivalentTo(new[] { 15.0, 12.0, 10.0, 8.0, 5.0 });
    }

    [Test]
    public async Task ANonMonotonicStoredLadderIsCleaned()
    {
        EraPreset preset = EraConfigMap.FromConfig(new CustomEraConfig
        {
            Id = "bad_ladder",
            Kind = "era",
            Fps = 15,
            FpsLevels = [8, 15, 15, -3, 0, 12],
            RcWidth = 320, RcHeight = 240, RcFps = 15,
            VideoCodec = "libx264", AudioCodec = "aac", Container = "mp4",
        })!;

        // Sorted descending, duplicates and non-positive values dropped.
        await Assert.That(preset.FpsLevels).IsEquivalentTo(new[] { 15.0, 12.0, 8.0 });
    }

    /// <summary>
    /// v7.9.6.2: a stored row's live layer is whatever the file gives, not what
    /// its kind implies -- an older site row without a frame size still loads as
    /// "second pass only", and one that names a frame size gets a live layer.
    /// </summary>
    [Test]
    public async Task ASiteRowKeepsTheLiveFieldsTheFileGives()
    {
        EraPreset bare = EraConfigMap.FromConfig(new CustomEraConfig
        {
            Id = "my_site",
            Kind = "site",
            Fps = 15,
            FpsLevels = [15, 12, 10],
            RcWidth = 320, RcHeight = 240, RcFps = 15,
            VideoCodec = "flv", AudioCodec = "libmp3lame", Container = "flv",
        })!;

        // No Width/Height in the file: no live layer, exactly as before.
        await Assert.That(bare.HasLive).IsFalse();
        await Assert.That(bare.FpsLevels).IsNotNull().Because("the ladder alone is harmless");

        EraPreset full = EraConfigMap.FromConfig(new CustomEraConfig
        {
            Id = "my_site_live",
            Kind = "site",
            Encoder = "flv",
            Width = 352, Height = 264,
            Fps = 12,
            FpsLevels = [12, 10, 8, 5],
            RcWidth = 352, RcHeight = 264, RcFps = 12, RcVideoKbps = 295,
            AudioKbps = 32, AudioRate = 22050, AudioChannels = 1,
            VideoCodec = "flv", AudioCodec = "libmp3lame", Container = "flv",
        })!;

        await Assert.That(full.HasLive).IsTrue();
        await Assert.That(full.FpsLevels).IsEquivalentTo(new[] { 12.0, 10.0, 8.0, 5.0 });
        // A frame size without a ceiling means the site's own bitrate.
        await Assert.That(full.LimitKbps).IsEqualTo(295.0);
        await Assert.That(full.AudioRate).IsEqualTo(22050);
    }
}
