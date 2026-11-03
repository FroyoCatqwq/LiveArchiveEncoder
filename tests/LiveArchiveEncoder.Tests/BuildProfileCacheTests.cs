using System.Text.Json;
using LiveArchiveEncoder.FFmpeg;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.8.8: the build profile is a disk cache of <c>ffmpeg -h</c> answers, and
/// <see cref="EncoderCaps"/> gained a field this release. A profile written by
/// v7.9.8.7 has no <c>HasRcMode</c>, so it would deserialize to <c>false</c> and
/// the program would conclude -- for every user who ever ran it -- that their
/// FFmpeg cannot do <c>-rc_mode</c>. The mode would ship dead and only on
/// upgraded installs, which is the worst kind of dead: invisible in testing.
///
/// <para>The fix is the schema bump, so what this file pins is the mechanism it
/// depends on: a profile whose schema is not the current one is a cache miss,
/// not a partially-filled answer.</para>
/// </summary>
public sealed class BuildProfileCacheTests
{
    [Test]
    public async Task AnOlderSchemaIsAMissRatherThanAPartialAnswer()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lae-caps-{Guid.NewGuid():N}.json");
        try
        {
            // Exactly the shape v7.9.8.7 wrote: schema 3, caps with no HasRcMode.
            await File.WriteAllTextAsync(path, """
            {
              "Schema": 3,
              "Identity": "abc",
              "Version": "ffmpeg version N-1",
              "Configuration": "",
              "Gpl": true,
              "Encoders": [],
              "VideoEncoders": ["libopenh264"],
              "AudioEncoders": [],
              "SubtitleEncoders": [],
              "Filters": [],
              "ScaleAlgorithms": [],
              "Caps": {
                "libopenh264": {
                  "Name": "libopenh264",
                  "Description": "OpenH264",
                  "Flags": "V....D",
                  "SupportedPixFmts": ["yuv420p"],
                  "SupportedRates": [15],
                  "HasCrf": false,
                  "HasQscale": false
                }
              },
              "BuiltAt": "2026-11-03T00:00:00+08:00"
            }
            """);
            await Assert.That(BuildProfileStore.TryRead(path, "abc")).IsNull();

            // The same file at the current schema loads, and the field the
            // schema bump exists for survives the round trip.
            string current = (await File.ReadAllTextAsync(path))
                .Replace("\"Schema\": 3", $"\"Schema\": {BuildProfile.SchemaVersion}")
                .Replace("\"HasQscale\": false", "\"HasQscale\": false, \"HasRcMode\": true");
            await File.WriteAllTextAsync(path, current);

            BuildProfile? loaded = BuildProfileStore.TryRead(path, "abc");
            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded!.Caps["libopenh264"].HasRcMode).IsTrue();
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Test]
    public async Task TheCachedFieldIsTheOneTheBuilderReads()
    {
        // Serialization drops nothing by accident: EncoderCaps is a plain type
        // with init properties, and a field missing from the JSON context would
        // come back false even at the current schema.
        var profile = new BuildProfile
        {
            Identity = "abc",
            Version = "ffmpeg version N-1",
            Caps = { ["libopenh264"] = new Configuration.EncoderCaps { Name = "libopenh264", HasRcMode = true } },
        };

        string json = JsonSerializer.Serialize(profile, BuildProfileJsonContext.Default.BuildProfile);
        await Assert.That(json).Contains("\"HasRcMode\": true");

        BuildProfile? round = JsonSerializer.Deserialize(json, BuildProfileJsonContext.Default.BuildProfile);
        await Assert.That(round!.Caps["libopenh264"].HasRcMode).IsTrue();
    }
}
