using Lae.Core.Settings;

namespace Lae.Tests.Core;

/// <summary>
/// The union of the two hop kinds, decided from the fields present.
///
/// <para>TOML has no tag, so the shape has to be recovered rather than read.
/// These tests pin the recovery rules, and in particular the one that matters:
/// a step with both halves is not resolved by precedence.</para>
/// </summary>
public sealed class ChainStepTests
{
    [Test]
    public async Task AFragmentAloneIsAnFfmpegHop()
    {
        var step = new ChainStep { Args = "-c:v libx264 -crf 28", Container = "mp4" };

        await Assert.That(step.Kind).IsEqualTo(ChainStepKind.Ffmpeg);
    }

    [Test]
    public async Task APluginAloneIsAPluginHop()
    {
        var step = new ChainStep { Plugin = "lae-damage", Stage = "damage" };

        await Assert.That(step.Kind).IsEqualTo(ChainStepKind.Plugin);
    }

    /// <summary>
    /// Both halves written is a mistake, and the whole point of naming it is that
    /// nothing silently wins. Preferring the fragment would leave the plugin line
    /// in the file looking like it does something.
    /// </summary>
    [Test]
    public async Task BothHalvesAreAmbiguousNotPrecedence()
    {
        var step = new ChainStep { Args = "-c:v copy", Plugin = "lae-damage" };

        await Assert.That(step.Kind).IsEqualTo(ChainStepKind.Ambiguous);
    }

    [Test]
    public async Task BlankOrAbsentHalvesAreEmpty()
    {
        await Assert.That(new ChainStep().Kind).IsEqualTo(ChainStepKind.Empty);
        await Assert.That(new ChainStep { Args = "   ", Plugin = "\t" }.Kind).IsEqualTo(ChainStepKind.Empty);
    }

    /// <summary>
    /// An ambiguous step survives normalisation. Dropping it here would turn
    /// "you wrote both halves" into a shorter chain that runs without complaint,
    /// and the planner -- the only place that can say what is wrong -- would
    /// never see it.
    /// </summary>
    [Test]
    public async Task NormalisingKeepsAnAmbiguousStepToBeReported()
    {
        var chain = new ChainSettings
        {
            Enabled = true,
            Steps =
            [
                new ChainStep { Args = "-c:v copy", Container = "mp4" },
                new ChainStep { Args = "-c:v copy", Plugin = "lae-damage" },
                new ChainStep { Args = "  ", Container = "mp4" },
            ],
        };

        IReadOnlyList<ChainStep> active = chain.Active();

        await Assert.That(active.Count).IsEqualTo(2)
            .Because("the blank one goes; the ambiguous one stays so the plan can name it");
        await Assert.That(active[1].Kind).IsEqualTo(ChainStepKind.Ambiguous);
    }
}
