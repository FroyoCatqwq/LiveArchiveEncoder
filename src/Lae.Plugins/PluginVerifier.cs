using System.Globalization;
using Lae.Core;
using Lae.Core.Localization;

namespace Lae.Plugins;

/// <summary>
/// Decides whether a plugin's product is worth keeping.
///
/// <para>Three layers, because "the part file exists and is not empty" is what v7
/// checked and it is not enough for a black box. A truncated MP4 has a positive
/// length; a plugin that processed the first ten percent and exited zero produces
/// exactly that. The third layer -- the length matches the input within a second --
/// is the only one that catches it, and it is the failure a human reviewer would
/// not notice either, because the file plays.</para>
///
/// <para>Each layer answers a different question and names itself in the message.
/// "The plugin wrote nothing" and "the plugin wrote something that is not a video"
/// send the reader to different places.</para>
/// </summary>
public sealed class PluginVerifier(
    string ffprobe,
    Func<string, string, CancellationToken, (SourceInfo? Info, string? Error)> probe,
    IMessages messages)
{
    /// <summary>
    /// How far the product's length may drift from its input.
    ///
    /// <para>One second, from the contract. A hop that re-encodes legitimately loses
    /// or gains a fraction -- keyframe alignment, an audio/video start offset -- but
    /// a whole second is far beyond that, and a hop that lost more than a second is
    /// a hop that lost content.</para>
    /// </summary>
    public const double LengthToleranceSeconds = 1.0;

    /// <summary>Null when the product is usable; otherwise the sentence to show.</summary>
    public string? Check(string pluginId, string input, string part, CancellationToken token)
    {
        // 1. It exists and holds bytes. A zero-length file is the commonest way a
        //    plugin "succeeds": it opened its output and then failed quietly.
        var written = new FileInfo(part);
        written.Refresh();

        if (!written.Exists)
            return messages.Format("plugin.error.noProduct", new { id = pluginId });

        if (written.Length == 0)
            return messages.Format("plugin.error.emptyProduct", new { id = pluginId });

        // 2. It is a container ffprobe can read, with at least one video stream.
        //    Audio-only output from a video plugin is always a mistake, and it is
        //    invisible without asking.
        (SourceInfo? product, string? unreadable) = probe(ffprobe, part, token);

        if (product is null)
        {
            return messages.Format("plugin.error.notReadable", new
            {
                id = pluginId,
                detail = string.IsNullOrWhiteSpace(unreadable) ? "-" : unreadable,
            });
        }

        if (!product.Streams.Any(s => string.Equals(s.Type, "video", StringComparison.OrdinalIgnoreCase)))
            return messages.Format("plugin.error.noVideoStream", new { id = pluginId });

        // 3. It is as long as what it was given.
        //
        //    Only when both lengths are known: a source whose duration ffprobe could
        //    not work out (MPEG-PS without an index) reports zero, and treating
        //    "unknown" as "wrong" would reject products that are fine.
        (SourceInfo? source, _) = probe(ffprobe, input, token);
        double expected = source?.DurationSeconds ?? 0;

        if (expected > 0 && product.DurationSeconds > 0)
        {
            double difference = Math.Abs(expected - product.DurationSeconds);

            if (difference > LengthToleranceSeconds)
            {
                return messages.Format("plugin.error.truncated", new
                {
                    id = pluginId,
                    got = product.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture),
                    expected = expected.ToString("F1", CultureInfo.InvariantCulture),
                });
            }
        }

        return null;
    }
}
