using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// JSON settings round-trip, written to a temp file so the real
/// <c>%LOCALAPPDATA%</c> configuration is never touched.
/// </summary>
[TestFixture]
public sealed class ConfigStoreTests
{
    private string _dir = "";
    private string _file = "";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lae-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "config.json");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public void MissingFileGivesDefaults()
    {
        UserConfig loaded = ConfigStore.Load(Path.Combine(_dir, "nope.json"));

        Assert.That(loaded.FfmpegSource, Is.EqualTo(FfmpegCatalog.DefaultId));
        Assert.That(loaded.Parallel, Is.EqualTo(1));
    }

    /// <summary>A corrupt file must never keep the program from starting.</summary>
    [Test]
    public void CorruptJsonFallsBackToDefaults()
    {
        File.WriteAllText(_file, "{ this is not json");

        UserConfig loaded = ConfigStore.Load(_file);

        Assert.That(loaded.FfmpegSource, Is.EqualTo(FfmpegCatalog.DefaultId));
        Assert.That(loaded.Language, Is.EqualTo("en"));
    }

    [Test]
    public void ToolchainAndPlaybackSettingsSurviveARoundTrip()
    {
        // avbuild, not gyan: gyan.dev ships no Linux build, so storing it and
        // reading it back would be rejected by the catalogue on that platform
        // and silently reset to btbn -- which is correct behaviour, just not
        // what this round-trip test is about.
        var c = new Config
        {
            Hwaccel = "cuda",
            FfmpegSource = "avbuild",
            FfmpegProxy = "http://127.0.0.1:10808",
            Play = PlayMode.External,
            PlayerPath = @"C:\mpc\mpc-hc64.exe",
            Language = "zh",
            Turbo = true,
            Parallel = 4,
        };

        Assert.That(ConfigStore.Save(ConfigStore.Capture(c), _file), Is.True);

        var restored = new Config();
        ConfigStore.Apply(ConfigStore.Load(_file), restored, []);

        Assert.That(restored.Hwaccel, Is.EqualTo("cuda"));
        Assert.That(restored.FfmpegSource, Is.EqualTo("avbuild"));
        Assert.That(restored.FfmpegProxy, Is.EqualTo("http://127.0.0.1:10808"));
        Assert.That(restored.Play, Is.EqualTo(PlayMode.External));
        Assert.That(restored.PlayerPath, Is.EqualTo(@"C:\mpc\mpc-hc64.exe"));
        Assert.That(restored.Language, Is.EqualTo("zh"));
        Assert.That(restored.Turbo, Is.True);
        Assert.That(restored.Parallel, Is.EqualTo(4));
    }

    /// <summary>
    /// A stored method name is only a request -- whether it works is decided at
    /// encode time by decoding a frame. What must be rejected at load is garbage,
    /// with "off" as the safe answer.
    /// </summary>
    [TestCase("cuda", "cuda")]
    [TestCase("VAAPI", "vaapi")]
    [TestCase("auto", "auto")]
    [TestCase("", "off")]
    [TestCase("some method with spaces", "off")]
    [TestCase(null, "off")]
    public void Hwaccel_IsSanitisedOnLoad(string? stored, string expected)
    {
        ConfigStore.Save(new UserConfig { Hwaccel = stored! }, _file);

        var restored = new Config();
        ConfigStore.Apply(ConfigStore.Load(_file), restored, []);

        Assert.That(restored.Hwaccel, Is.EqualTo(expected));
    }

    /// <summary>
    /// <c>path</c> is the escape hatch back to the system FFmpeg. It is not a
    /// distribution id, so a naive "normalise unknown ids" would drop it.
    /// </summary>
    [Test]
    public void PathSourceIsPreservedNotNormalisedAway()
    {
        var stored = new UserConfig { FfmpegSource = "path" };
        ConfigStore.Save(stored, _file);

        var restored = new Config();
        ConfigStore.Apply(ConfigStore.Load(_file), restored, []);

        Assert.That(restored.FfmpegSource, Is.EqualTo("path"));
    }

    [Test]
    public void UnknownSourceFallsBackToTheDefault()
    {
        ConfigStore.Save(new UserConfig { FfmpegSource = "typo" }, _file);

        var restored = new Config();
        ConfigStore.Apply(ConfigStore.Load(_file), restored, []);

        Assert.That(restored.FfmpegSource, Is.EqualTo(FfmpegCatalog.DefaultId));
    }

    /// <summary>A nonsense shard count from an older file must never be restored.</summary>
    [TestCase(0)]
    [TestCase(-3)]
    public void ShardCountIsClamped(int storedValue)
    {
        ConfigStore.Save(new UserConfig { Parallel = storedValue }, _file);

        var restored = new Config();
        ConfigStore.Apply(ConfigStore.Load(_file), restored, []);

        Assert.That(restored.Parallel, Is.EqualTo(1));
    }

    [Test]
    public void MissingInputFileIsIgnoredOnRestore()
    {
        ConfigStore.Save(new UserConfig { InputPath = @"C:\definitely\not\here.mp4" }, _file);

        var restored = new Config();
        ConfigStore.Apply(ConfigStore.Load(_file), restored, []);

        Assert.That(restored.InputPath, Is.Null);
    }

    [Test]
    public void SavedAtIsStampedOnWrite()
    {
        ConfigStore.Save(new UserConfig(), _file);

        Assert.That(ConfigStore.Load(_file).SavedAt, Is.Not.Null);
    }
}
