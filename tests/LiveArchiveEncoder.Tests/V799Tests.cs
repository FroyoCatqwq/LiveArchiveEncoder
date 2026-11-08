using System.Text;
using LiveArchiveEncoder.Core;
using LiveArchiveEncoder.Diagnostics;
using LiveArchiveEncoder.FFmpeg;
using LiveArchiveEncoder.Localization;
using LiveArchiveEncoder.UI;

namespace LiveArchiveEncoder.Tests;

/// <summary>v7.9.9: logging, pipelines, "do not use" and modernisation regressions.</summary>
public sealed class V799Tests
{
    [Test]
    public async Task NoEraIsParsedAndMeansClearTheStoredPreset()
    {
        (CliOptions? opts, string? error) = CliParser.Parse(["clip.mkv", "--no-era"]);
        await Assert.That(error).IsNull();
        await Assert.That(opts!.NoEra).IsTrue();

        (CliOptions? plain, _) = CliParser.Parse(["clip.mkv"]);
        await Assert.That(plain!.NoEra).IsFalse();
    }

    [Test]
    public async Task TheDoNotUseMenuKeysExistInBothLanguages()
    {
        string[] keys = ["era.menu.none", "era.none.set", "rc.menu.clear", "rc.cleared"];
        foreach (string key in keys)
        {
            await Assert.That(L.KeysFor(L.English).Contains(key)).IsTrue();
            await Assert.That(L.KeysFor(L.Chinese).Contains(key)).IsTrue();
        }
    }

    [Test]
    public async Task PipelinesLineReaderKeepsTheOldTrimmingAndTailRules()
    {
        // Cross-segment CRLF, leading/trailing blanks, and a final line with no
        // newline at all: the hand-rolled reader handled all three.
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("a=1\r\n  b=2  \nlast without newline");
        using var stream = new MemoryStream(bytes);
        var lines = new List<string>();
        await PipeReaders.DiagnosticsReaderAsync(stream, lines.Add, CancellationToken.None);
        await Assert.That(string.Join("|", lines)).IsEqualTo("a=1|b=2|last without newline");
    }

    [Test]
    public async Task DiagnosticsReaderKeepsAllThreeLines()
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("one\ntwo\nthree\n");
        using var stream = new MemoryStream(bytes);
        var lines = new List<string>();
        // DiagnosticsReaderAsync's callback returns void; it cannot stop early.
        // The test used to claim to exercise "false stops the reader" while
        // only counting lines, so the name is now what it actually asserts.
        await PipeReaders.DiagnosticsReaderAsync(stream, lines.Add, CancellationToken.None);
        await Assert.That(lines.Count).IsEqualTo(3);
        await Assert.That(string.Join("|", lines)).IsEqualTo("one|two|three");
    }

    [Test]
    public async Task CachedSearchValuesFindTheSameCuts()
    {
        await Assert.That("1.2.3-beta+7".AsSpan().IndexOfAny(CharSets.VersionSuffix)).IsEqualTo(5);
        await Assert.That("http://x/y?z#f".AsSpan().IndexOfAny(CharSets.UrlQuery)).IsEqualTo(10);
        await Assert.That("ab\r\ncd".AsSpan().IndexOfAny(CharSets.NewLine)).IsEqualTo(2);
    }

    [Test]
    public async Task TheRunLogHasAStableCorrelationId()
    {
        string first = AppLog.RunId;
        await Assert.That(first.Length).IsGreaterThan(4);
        // v7.9.9.3: the old assertion compared the property with itself and
        // passed for ANY implementation, including one that generated a new id
        // on every read. Capture once, then verify stability.
        await Assert.That(AppLog.RunId).IsEqualTo(first);
        string operation = AppLog.CurrentOperation;
        await Assert.That(operation.Length).IsGreaterThan(0);
        await Assert.That(AppLog.CurrentOperation).IsEqualTo(operation);
    }
}
