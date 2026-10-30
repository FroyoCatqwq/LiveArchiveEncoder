using Lae.Core;
using Lae.Core.Presets;
using Lae.Core.Settings;

namespace Lae.Tests.Core;

/// <summary>
/// The preset library.
///
/// <para>v8 has no built-in presets, so everything a preset can be is decided
/// here: how an id is spelled, what a name may contain, and -- the part that
/// matters most -- which settings a preset is allowed to move. A preset that
/// quietly reset a field it never mentioned would be a second, worse copy of the
/// defaults.</para>
/// </summary>
public sealed class PresetCatalogTests
{
    private static SettingsDocument Stored(params CustomPreset[] presets) => new()
    {
        Defaults = new EncodeDefaults
        {
            Encoder = "mpeg4",
            Width = 320,
            Height = 240,
            Fps = 13,
            LimitKbps = 200,
            Quality = 8,
            AudioEncoder = "libmp3lame",
            AudioKbps = 32,
        },
        CustomPresets = presets,
    };

    [Test]
    public async Task AnIdIsATrimmedLowercaseSlug()
    {
        await Assert.That(PresetCatalog.NormaliseId("  My Portal 2006 ")).IsEqualTo("my-portal-2006");
        await Assert.That(PresetCatalog.NormaliseId("2004/index")).IsEqualTo("2004index");
        await Assert.That(PresetCatalog.NormaliseId("---")).IsEqualTo("");
        await Assert.That(PresetCatalog.NormaliseId(null)).IsEqualTo("");
    }

    [Test]
    public async Task AddingReplacesTheRowWithTheSameId()
    {
        SettingsDocument document = Stored(new CustomPreset { Id = "portal", Label = "first" });

        PresetChange change = PresetCatalog.Put(document, new CustomPreset { Id = "PORTAL", Label = "second" });

        await Assert.That(change.Ok).IsTrue();
        await Assert.That(change.Document!.CustomPresets.Count).IsEqualTo(1);
        await Assert.That(change.Document.CustomPresets[0].Label).IsEqualTo("second");
        await Assert.That(change.Document.CustomPresets[0].Id).IsEqualTo("portal");
    }

    [Test]
    public async Task AnIdThatCannotBeTypedIsRefused()
    {
        PresetChange change = PresetCatalog.Put(Stored(), new CustomPreset { Id = "   " });

        await Assert.That(change.Ok).IsFalse();
        await Assert.That(change.Error!.Contains("id", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ImplausibleNumbersAreRefusedRatherThanStored()
    {
        await Assert.That(PresetCatalog.Put(Stored(), new CustomPreset { Id = "x", Fps = 0 }).Ok).IsFalse();
        await Assert.That(PresetCatalog.Put(Stored(), new CustomPreset { Id = "x", Width = -4 }).Ok).IsFalse();
        await Assert.That(PresetCatalog.Put(Stored(), new CustomPreset { Id = "x", LimitKbps = 0 }).Ok).IsFalse();
    }

    [Test]
    public async Task RemovingSomethingThatIsNotThereIsAnError()
    {
        PresetChange change = PresetCatalog.Remove(Stored(), "nope");

        await Assert.That(change.Ok).IsFalse();
        await Assert.That(change.Error!.Contains("nope", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// The whole point of a preset: it moves what it names and nothing else.
    /// </summary>
    [Test]
    public async Task ApplyingMovesOnlyTheFieldsThePresetCarries()
    {
        EncodeDefaults applied = PresetCatalog.Apply(
            new CustomPreset { Id = "small", Width = 160, Height = 120, LimitKbps = 90 },
            Stored().Defaults);

        await Assert.That(applied.Width).IsEqualTo(160);
        await Assert.That(applied.Height).IsEqualTo(120);
        await Assert.That(applied.LimitKbps).IsEqualTo(90);

        // Untouched: the encoder, the rate, the quality, the audio.
        await Assert.That(applied.Encoder).IsEqualTo("mpeg4");
        await Assert.That(applied.Fps).IsEqualTo(13.0);
        await Assert.That(applied.Quality).IsEqualTo(8);
        await Assert.That(applied.AudioKbps).IsEqualTo(32);
    }

    /// <summary>
    /// A preset written for the mpeg family has to survive being applied to an
    /// x264 run. Handing <c>-q:v 9</c> to libx264 as a CRF was v7.9.8.2's bug.
    /// </summary>
    [Test]
    public async Task QualityIsTranslatedIntoTheEncoderThatWillRun()
    {
        EncodeDefaults applied = PresetCatalog.Apply(
            new CustomPreset { Id = "h264", Encoder = "libx264", LiveQv = 9 },
            Stored().Defaults);

        await Assert.That(applied.Encoder).IsEqualTo("libx264");

        // crf = 7.5 + 1.17 * 9 = 18.
        await Assert.That(applied.Quality).IsEqualTo(18);
    }

    [Test]
    public async Task APresetForTheSameScaleLeavesTheNumberAlone()
    {
        EncodeDefaults applied = PresetCatalog.Apply(
            new CustomPreset { Id = "mpeg", Encoder = "mpeg4", LiveCrf = 20 },
            Stored().Defaults);

        // 20 on the CRF scale is about q:v 11 (7.5 + 1.17 * 11 = 20.4).
        await Assert.That(applied.Quality).IsEqualTo(11);
    }

    [Test]
    public async Task ALadderInThePresetReplacesTheStoredOne()
    {
        EncodeDefaults applied = PresetCatalog.Apply(
            new CustomPreset { Id = "choppy", FpsLevels = [8, 5] },
            Stored().Defaults);

        await Assert.That(applied.FpsLevels.Count).IsEqualTo(2);
        await Assert.That(applied.FpsLevels[0]).IsEqualTo(8.0);
    }

    [Test]
    public async Task DescribeSaysWhatThePresetChanges()
    {
        string line = PresetCatalog.Describe(new CustomPreset
        {
            Id = "p", Encoder = "mpeg4", Width = 320, Height = 240, Fps = 12, LimitKbps = 150, Container = "avi",
        });

        await Assert.That(line).IsEqualTo("mpeg4, 320x240, 12 fps, 150 kbps, avi");
        await Assert.That(PresetCatalog.Describe(new CustomPreset { Id = "p" })).IsEqualTo("no overrides");
    }

    [Test]
    public async Task TheLibraryIsAlwaysSortedById()
    {
        SettingsDocument document = Stored(
            new CustomPreset { Id = "zulu" },
            new CustomPreset { Id = "alpha" },
            new CustomPreset { Id = "mike" });

        await Assert.That(string.Join(',', PresetCatalog.All(document).Select(p => p.Id)))
            .IsEqualTo("alpha,mike,zulu");
    }
}
