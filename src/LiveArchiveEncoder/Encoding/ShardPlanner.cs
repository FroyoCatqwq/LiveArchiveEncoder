using System.Globalization;

namespace LiveArchiveEncoder.Encoding;

/// <summary>One shard's slice of the selected time range.</summary>
public readonly record struct ShardRange(double Start, double End, int Slots);

/// <summary>
/// Turns a time range into per-shard boundaries.
///
/// Boundaries are placed on whole output slots, not on raw seconds: a boundary
/// that lands mid-slot would make the shard slot counts fail to add up and
/// silently shorten or lengthen the joined file.
///
/// The slot grid is coarser than the media timeline, though. <c>ceil(span*fps)</c>
/// slots cover slightly <em>more</em> time than the source actually has, and a
/// shard that asks for an end past EOF is rejected outright ("end time ...
/// exceeds the source duration"). Every boundary is therefore clamped to the end
/// of the range, and the last shard is pinned to it exactly so the tail of the
/// source is always covered.
/// </summary>
public static class ShardPlanner
{
    public static List<ShardRange> Plan(double start, double span, double fps, int shards)
    {
        if (!double.IsFinite(start) || !double.IsFinite(span) || span <= 0
            || !double.IsFinite(fps) || fps <= 0)
        {
            throw new ArgumentException("shard planning needs a positive span and frame rate");
        }

        int wanted = Math.Max(1, shards);
        double rangeEnd = start + span;

        int fpsSlots = (int)Math.Ceiling(span * fps - 1e-9);
        if (fpsSlots < 1) fpsSlots = 1;
        // More shards than slots would leave shard-sized holes in the timeline.
        if (fpsSlots < wanted) wanted = fpsSlots;

        int baseSlots = fpsSlots / wanted;
        int remainder = fpsSlots % wanted;

        var result = new List<ShardRange>(wanted);
        double cursor = start;
        for (int i = 0; i < wanted; i++)
        {
            int count = baseSlots + (i < remainder ? 1 : 0);
            bool last = i == wanted - 1;
            double end = last ? rangeEnd : Math.Min(cursor + count / fps, rangeEnd);
            if (end - cursor <= 1e-9) break;   // nothing of the source is left to cover

            result.Add(new ShardRange(cursor, end, count));
            cursor = end;
            if (last) break;
        }

        if (result.Count == 0) return [new ShardRange(start, rangeEnd, fpsSlots)];

        ShardRange tail = result[^1];
        result[^1] = tail with { End = rangeEnd };
        return result;
    }
}
