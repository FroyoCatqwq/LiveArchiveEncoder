using System.Globalization;
using System.Text.Json;
using Lae.Core.Localization;
using Lae.Core.Plugins;
using Lae.Media.Localization;

namespace Lae.Tests.Plugins;

/// <summary>
/// Turning the strings in <c>settings.toml</c> into the parameters a stage
/// declared.
///
/// <para>Every case here is a configuration mistake a person can actually make,
/// and the point of catching it here is that the alternative is a plugin that runs,
/// exits zero, and produces a file nobody can explain.</para>
/// </summary>
public sealed class PluginParamsTests
{
    private static readonly IMessages En = I18NextMessages.Create("en");

    private static PluginStageDeclaration Stage(params PluginParamDeclaration[] declared)
        => new() { Id = "damage", Placement = [PluginStages.PostEncode], Params = declared };

    private static PluginParamDeclaration Float(string name, double? min = null, double? max = null, string? fallback = null)
        => new()
        {
            Name = name,
            Type = "float",
            Min = min,
            Max = max,
            Default = fallback is null ? null : JsonElement.Parse(fallback),
        };

    private static string Problems(ParamResolution resolution) => string.Join(" | ", resolution.Problems);

    [Test]
    public async Task ADefaultFillsInWhatTheSettingsOmitted()
    {
        ParamResolution resolution = PluginParams.Resolve(Stage(Float("intensity", fallback: "0.25")), null, En);

        await Assert.That(resolution.Ok).IsTrue().Because(Problems(resolution));
        await Assert.That(resolution.Values["intensity"]).IsEqualTo("0.25");
    }

    [Test]
    public async Task WhatTheSettingsSayWinsOverTheDefault()
    {
        var given = new Dictionary<string, string> { ["intensity"] = "0.5" };

        ParamResolution resolution = PluginParams.Resolve(Stage(Float("intensity", fallback: "0.25")), given, En);

        await Assert.That(resolution.Ok).IsTrue().Because(Problems(resolution));
        await Assert.That(resolution.Values["intensity"]).IsEqualTo("0.5");
    }

    /// <summary>
    /// A parameter nobody declared would be dropped by the command line, and the
    /// user would believe they had set something.
    /// </summary>
    [Test]
    public async Task AParameterNoStageDeclaredIsRefused()
    {
        var given = new Dictionary<string, string> { ["intensity"] = "0.5", ["blur"] = "3" };

        ParamResolution resolution = PluginParams.Resolve(Stage(Float("intensity")), given, En);

        await Assert.That(resolution.Ok).IsFalse();
        await Assert.That(Problems(resolution)).Contains("blur");
    }

    /// <summary>
    /// <c>2.5</c> for an <c>int</c> is a mistake, not a value to round: the author
    /// meant either 2 or 3, and guessing which is how a shot gets the wrong amount
    /// of damage.
    /// </summary>
    [Test]
    public async Task AFractionalValueForAnIntegerIsRefused()
    {
        var given = new Dictionary<string, string> { ["seed"] = "2.5" };
        var stage = Stage(new PluginParamDeclaration { Name = "seed", Type = "int" });

        ParamResolution resolution = PluginParams.Resolve(stage, given, En);

        await Assert.That(resolution.Ok).IsFalse();
        await Assert.That(Problems(resolution)).Contains("whole number");
    }

    [Test]
    public async Task AValueBelowTheMinimumIsRefused()
    {
        var given = new Dictionary<string, string> { ["intensity"] = "-0.1" };

        ParamResolution resolution = PluginParams.Resolve(Stage(Float("intensity", min: 0, max: 1)), given, En);

        await Assert.That(resolution.Ok).IsFalse();
        await Assert.That(Problems(resolution)).Contains("-0.1");
    }

    [Test]
    public async Task AValueAboveTheMaximumIsRefused()
    {
        var given = new Dictionary<string, string> { ["intensity"] = "1.5" };

        ParamResolution resolution = PluginParams.Resolve(Stage(Float("intensity", min: 0, max: 1)), given, En);

        await Assert.That(resolution.Ok).IsFalse();
        await Assert.That(Problems(resolution)).Contains("1.5");
    }

    [Test]
    public async Task TheBoundsAreInclusive()
    {
        var given = new Dictionary<string, string> { ["intensity"] = "1" };

        ParamResolution resolution = PluginParams.Resolve(Stage(Float("intensity", min: 0, max: 1)), given, En);

        await Assert.That(resolution.Ok).IsTrue().Because(Problems(resolution));
    }

    [Test]
    public async Task AValueThatIsNotANumberIsRefused()
    {
        var given = new Dictionary<string, string> { ["intensity"] = "strong" };

        ParamResolution resolution = PluginParams.Resolve(Stage(Float("intensity")), given, En);

        await Assert.That(resolution.Ok).IsFalse();
        await Assert.That(Problems(resolution)).Contains("strong");
    }

    /// <summary>
    /// Numbers are read with a decimal point, whatever the machine's culture says.
    ///
    /// <para>Two assertions, both true on every machine. The first says <c>0.25</c>
    /// is a number. The second says <c>0,25</c> is not -- which is the one that
    /// fails if the reader ever starts honouring <c>CurrentCulture</c>: on a machine
    /// set to a decimal-comma culture that value would parse to 0.25 locally and be
    /// handed to a plugin that reads <c>0.25</c>, i.e. a different number, or none.</para>
    ///
    /// <para>Written this way rather than by switching cultures inside the test
    /// because the build runs in globalization-invariant mode, where
    /// <c>new CultureInfo("de-DE")</c> throws -- a test that cannot run on the
    /// machine that gates the release proves nothing.</para>
    /// </summary>
    [Test]
    public async Task NumbersAreReadWithADecimalPointWhateverTheMachineUses()
    {
        ParamResolution point = PluginParams.Resolve(
            Stage(Float("intensity")), new Dictionary<string, string> { ["intensity"] = "0.25" }, En);

        await Assert.That(point.Ok).IsTrue().Because(Problems(point));
        await Assert.That(point.Values["intensity"]).IsEqualTo("0.25");

        ParamResolution comma = PluginParams.Resolve(
            Stage(Float("intensity")), new Dictionary<string, string> { ["intensity"] = "0,25" }, En);

        await Assert.That(comma.Ok).IsFalse()
            .Because("a reader that followed the machine's culture would accept this and "
                   + "hand the plugin a number it cannot parse");
    }

    [Test]
    public async Task ABadBooleanIsRefused()
    {
        var given = new Dictionary<string, string> { ["soften"] = "maybe" };
        var stage = Stage(new PluginParamDeclaration { Name = "soften", Type = "bool" });

        ParamResolution resolution = PluginParams.Resolve(stage, given, En);

        await Assert.That(resolution.Ok).IsFalse();
        await Assert.That(Problems(resolution)).Contains("maybe");
    }

    /// <summary>
    /// A string parameter takes whatever was written, including something that looks
    /// like a number. It is the plugin's job to interpret it.
    /// </summary>
    [Test]
    public async Task AStringParameterTakesAnything()
    {
        var given = new Dictionary<string, string> { ["curve"] = "1.5, weird, ünïcode" };
        var stage = Stage(new PluginParamDeclaration { Name = "curve", Type = "string" });

        ParamResolution resolution = PluginParams.Resolve(stage, given, En);

        await Assert.That(resolution.Ok).IsTrue().Because(Problems(resolution));
        await Assert.That(resolution.Values["curve"]).IsEqualTo("1.5, weird, ünïcode");
    }
}
