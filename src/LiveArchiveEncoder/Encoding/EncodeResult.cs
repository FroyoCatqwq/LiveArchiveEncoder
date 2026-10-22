namespace LiveArchiveEncoder.Encoding;

/// <summary>Outcome of a realtime encode, mirroring the Python result dict.</summary>
public sealed class EncodeResult
{
    public double Duration { get; init; }
    public double StartTimeSeconds { get; init; }
    public double? EndTimeSeconds { get; init; }
    public string SourcePixFmt { get; init; } = "";
    public string TransportPixFmt { get; init; } = "";
    public int OutputWidth { get; init; }
    public int OutputHeight { get; init; }
    public double AvgVideoKbps { get; init; }
    public double AvgTotalKbps { get; init; }

    /// <summary>
    /// True when the product carries an audio track (v7.9.3.1).
    ///
    /// The shard pipeline needs this to split the joined bitrate into video and
    /// total, and it used to infer it from <c>AvgTotalKbps != AvgVideoKbps</c> --
    /// a float comparison standing in for a fact the encode already knew.
    /// </summary>
    public bool HasAudio { get; init; }
    public int Fresh { get; init; }
    public int Repeated { get; init; }
    public string Deinterlace { get; init; } = "";
    public string ScaleAlgo { get; init; } = "";
    public int Slots { get; init; }
    /// <summary>Wall-clock seconds spent inside the encode (C#-only diagnostic).</summary>
    public double ElapsedSeconds { get; init; }
}
