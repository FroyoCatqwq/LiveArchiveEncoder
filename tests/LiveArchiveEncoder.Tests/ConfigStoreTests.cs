using LiveArchiveEncoder.Configuration;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// JSON settings round-trip, written to a temp file so the real
/// <c>%LOCALAPPDATA%</c> configuration is never touched.
/// </summary>
public sealed class ConfigStoreTests
{
    private string _dir = "";
    private string _file = "";

    [Before(Test)]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lae-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "config.json");
    }

    [After(Test)]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task MissingFileGivesDefaults()
    {
        UserConfig loaded = ConfigStore.Load(Path.Combine(_dir, "nope.json"));

        await Assert.That(loaded.FfmpegSource).IsEqualTo(FfmpegCatalog.DefaultId);
        await Assert.That(loaded.Parallel).IsEqualTo(1);
    }

    /// <summary>A corrupt file must never keep the program from starting.</summary>
    [Test]
    public async Task CorruptJsonFallsBackToDefaults()
    {
        File.WriteAllText(_file, "{ this is not json");

        UserConfig loaded = ConfigStore.Load(_file);

        await Assert.That(loaded.FfmpegSource).IsEqualTo(FfmpegCatalog.DefaultId);
        await Assert.That(loaded.Language).IsEqualTo("en");
    }

    [Test]
    public async Task ToolchainAndPlaybackSettingsSurviveARoundTrip()
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
            Parallel = 4,
        };

        await Assert.That(ConfigStore.Save(ConfigStore.Capture(c), _file)).IsTrue();

        var restored = new Config();
        ConfigStore.Apply(ConfigStore.Load(_file), restored, []);

        await Assert.That(restored.Hwaccel).IsEqualTo("cuda");
        await Assert.That(restored.FfmpegSource).IsEqualTo("avbuild");
        await Assert.That(restored.FfmpegProxy).IsEqualTo("http://127.0.0.1:10808");
        await Assert.That(restored.Play).IsEqualTo(PlayMode.External);
        await Assert.That(restored.PlayerPath).IsEqualTo(@"C:\mpc\mpc-hc64.exe");
        await Assert.That(restored.Language).IsEqualTo("zh");
        await Assert.That(restored.Parallel).IsEqualTo(4);
    }

    /// <summary>
    /// A stored method name is only a request -- whether it works is decided at
    /// encode time by decoding a frame. What must be rejected at load is garbage,
    /// with "off" as the safe answer.
    /// </summary>
    [Test]
    [Arguments("cuda", "cuda")]
    [Arguments("VAAPI", "vaapi")]
    [Arguments("auto", "auto")]
    [Arguments("", "off")]
    [Arguments("some method with spaces", "off")]
    [Arguments(null, "off")]
    public async Task Hwaccel_IsSanitisedOnLoad(string? stored, string expected)
    {
        ConfigStore.Save(new UserConfig { Hwaccel = stored! }, _file);

        var restored = new Config();
        ConfigStore.Apply(ConfigStore.Load(_file), restored, []);

        await Assert.That(restored.Hwaccel).IsEqualTo(expected);
    }

    /// <summary>
    /// <c>path</c> is the escape hatch back to the system FFmpeg. It is not a
    /// distribution id, so a naive "normalise unknown ids" would drop it.
    /// </summary>
    [Test]
    public async Task PathSourceIsPreservedNotNormalisedAway()
    {
        var stored = new UserConfig { FfmpegSource = "path" };
        ConfigStore.Save(stored, _file);

        var restored = new Config();
        ConfigStore.Apply(ConfigStore.Load(_file), restored, []);

        await Assert.That(restored.FfmpegSource).IsEqualTo("path");
    }

    [Test]
    public async Task UnknownSourceFallsBackToTheDefault()
    {
        ConfigStore.Save(new UserConfig { FfmpegSource = "typo" }, _file);

        var restored = new Config();
        ConfigStore.Apply(ConfigStore.Load(_file), restored, []);

        await Assert.That(restored.FfmpegSource).IsEqualTo(FfmpegCatalog.DefaultId);
    }

    /// <summary>A nonsense shard count from an older file must never be restored.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(-3)]
    public async Task ShardCountIsClamped(int storedValue)
    {
        ConfigStore.Save(new UserConfig { Parallel = storedValue }, _file);

        var restored = new Config();
        ConfigStore.Apply(ConfigStore.Load(_file), restored, []);

        await Assert.That(restored.Parallel).IsEqualTo(1);
    }

    [Test]
    public async Task MissingInputFileIsIgnoredOnRestore()
    {
        ConfigStore.Save(new UserConfig { InputPath = @"C:\definitely\not\here.mp4" }, _file);

        var restored = new Config();
        ConfigStore.Apply(ConfigStore.Load(_file), restored, []);

        await Assert.That(restored.InputPath).IsNull();
    }

    [Test]
    public async Task SavedAtIsStampedOnWrite()
    {
        ConfigStore.Save(new UserConfig(), _file);

        await Assert.That(ConfigStore.Load(_file).SavedAt).IsNotNull();
    }
}
