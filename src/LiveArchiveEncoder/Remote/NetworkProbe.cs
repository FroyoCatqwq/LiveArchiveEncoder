using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.Remote;

/// <summary>
/// What a link actually delivered, and whether that is enough to read a source
/// from it through N decoders at once.
/// </summary>
/// <param name="Kind">How it was measured: <c>read</c> or <c>iperf3</c>.</param>
/// <param name="MebibytesPerSecond">Median throughput over the samples.</param>
/// <param name="FirstByteMs">Median time to the first byte of a sample.</param>
/// <param name="JitterPercent">Spread of the per-sample throughput, as a percentage of the median.</param>
/// <param name="Samples">How many measurements the numbers are based on.</param>
/// <param name="RequiredKbps">What the encode itself needs from this link.</param>
/// <param name="Adequate">The verdict: throughput, jitter and latency together.</param>
public sealed record NetworkMeasurement(
    string Kind,
    double MebibytesPerSecond,
    double FirstByteMs,
    double JitterPercent,
    int Samples,
    double RequiredKbps,
    bool Adequate)
{
    /// <summary>Delivered throughput as a multiple of what the encode needs.</summary>
    public double Headroom => RequiredKbps <= 0 ? 0 : MebibytesPerSecond * 8000.0 / RequiredKbps;

    /// <summary>One line for the log and for the "sharding stays off" message.</summary>
    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"{MebibytesPerSecond:F1} MB/s, {JitterPercent:F0}% jitter, {FirstByteMs:F0} ms "
        + $"({Kind}, {Samples} samples) for {RequiredKbps:F0} kbps needed "
        + $"=> {Headroom:F1}x headroom");
}

/// <summary>
/// Decides whether a network source may be read by several shards at once.
///
/// <para>Until v7.9.6.3 the answer was "never": a network path was forced to a
/// single pass on the theory that N readers on a flaky link stall each other.
/// That is true of a congested Wi-Fi hop and false of a gigabit LAN, and it
/// cost every LAN case the whole point of sharding. The rule is now measured
/// rather than assumed -- if the link delivers a comfortable multiple of what
/// the encode consumes, with stable latency, sharding is safe.</para>
///
/// <para>Two measurement modes, both optional in the sense that a failure
/// means "do not shard", never "crash":</para>
/// <list type="bullet">
/// <item><description><b>read</b> (default) -- a handful of ranged reads from
/// the source itself. It measures the exact path the shards will use, needs no
/// extra software, and works for UNC paths, mapped drives and HTTP(S).</description></item>
/// <item><description><b>iperf3</b> -- when the user names a host, the link is
/// measured with <c>iperf3 -c HOST -J</c> instead. That is the honest way to
/// tell "my LAN is fine" from "this share is slow", but it needs an iperf3
/// server on the other end, so it is opt-in.</description></item>
/// </list>
///
/// <para>Every measurement is cached per source for the life of the process:
/// the CLI prints the verdict and the encoder acts on it, and probing twice
/// would both waste time and allow the two answers to disagree.</para>
/// </summary>
public static class NetworkProbe
{
    /// <summary>Throughput multiplier a link must clear before shards share it.</summary>
    public const double HeadroomFactor = 2.5;

    /// <summary>Jitter (spread as a percentage of the median rate) above which a link is not sharded.</summary>
    public const double JitterLimitPercent = 30.0;

    /// <summary>Latency above which even a fast link is considered unstable.</summary>
    public const double LatencyLimitMs = 500.0;

    private const int SampleBytes = 2 * 1024 * 1024;
    private const int MaxSamples = 4;
    private const int MinSampleBytes = 512 * 1024;

    private static readonly ConcurrentDictionary<string, NetworkMeasurement?> Cache = new();

    /// <summary>
    /// Measure the link behind <paramref name="source"/> and judge it against
    /// <paramref name="requiredKbps"/>. Null means "could not measure", which
    /// the callers treat as "no sharding".
    /// </summary>
    public static NetworkMeasurement? Measure(string source, double requiredKbps, string? iperf3Host = null)
    {
        string key = string.Create(CultureInfo.InvariantCulture,
            $"{source}|{iperf3Host}|{(int)Math.Round(requiredKbps)}");
        if (Cache.TryGetValue(key, out NetworkMeasurement? cached)) return cached;

        NetworkMeasurement? measured = iperf3Host.HasText
            ? FromIperf3(iperf3Host!, requiredKbps)
            : FromReads(source, requiredKbps);

        Cache[key] = measured;
        return measured;
    }

    /// <summary>Forget every cached measurement (used by the diagnostics path).</summary>
    public static void Reset() => Cache.Clear();

    private static NetworkMeasurement? FromReads(string source, double requiredKbps)
    {
        try
        {
            return HttpSource.IsUrl(source)
                ? FromHttp(source, requiredKbps)
                : FromFile(source, requiredKbps);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            // A share that cannot be read at all is a fact worth reporting, but
            // not a crash: the single-pass run will produce the real error.
            return null;
        }
    }

    private static NetworkMeasurement? FromFile(string source, double requiredKbps)
    {
        var info = new FileInfo(source);
        if (!info.Exists) return null;

        long length = info.Length;
        if (length <= 0) return null;

        int sampleBytes = (int)Math.Min(SampleBytes, Math.Max(MinSampleBytes, length / 6));
        int wanted = (int)Math.Min(MaxSamples, Math.Max(1, length / sampleBytes));
        var rates = new List<double>(wanted);
        var latencies = new List<double>(wanted);

        using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.RandomAccess | FileOptions.SequentialScan);

        byte[] buffer = new byte[64 * 1024];
        var sw = new Stopwatch();
        for (int i = 0; i < wanted; i++)
        {
            // Spread the samples across the file: the first megabyte of a share
            // is the one the server has cached, and measuring only that is how a
            // 40 MB/s link looks like a 400 MB/s one.
            double fraction = wanted == 1 ? 0.1 : 0.1 + (0.8 * i / (wanted - 1));
            long offset = (long)(length * fraction);
            if (offset + sampleBytes > length) offset = Math.Max(0, length - sampleBytes);
            if (stream.CanSeek) stream.Position = offset;

            sw.Restart();
            int first = stream.Read(buffer, 0, buffer.Length);
            double firstMs = sw.Elapsed.TotalMilliseconds;
            if (first <= 0) break;

            int total = first;
            while (total < sampleBytes)
            {
                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, sampleBytes - total));
                if (read <= 0) break;
                total += read;
            }
            double seconds = sw.Elapsed.TotalSeconds;
            if (seconds <= 0 || total <= 0) continue;
            rates.Add(total / seconds / (1024 * 1024));
            latencies.Add(firstMs);
        }

        if (rates.Count == 0) return null;
        return Judge("read", rates, latencies, requiredKbps);
    }

    private static NetworkMeasurement? FromHttp(string source, double requiredKbps)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var rates = new List<double>();
        var latencies = new List<double>();
        var sw = new Stopwatch();

        for (int i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            long offset = (long)i * SampleBytes;
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + SampleBytes - 1);
            sw.Restart();
            using HttpResponseMessage response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return null;
            using Stream body = response.Content.ReadAsStream();
            byte[] buffer = new byte[64 * 1024];
            int first = body.Read(buffer, 0, buffer.Length);
            double firstMs = sw.Elapsed.TotalMilliseconds;
            if (first <= 0) break;
            int total = first;
            while (total < SampleBytes)
            {
                int read = body.Read(buffer, 0, Math.Min(buffer.Length, SampleBytes - total));
                if (read <= 0) break;
                total += read;
            }
            double seconds = sw.Elapsed.TotalSeconds;
            if (seconds <= 0) continue;
            rates.Add(total / seconds / (1024 * 1024));
            latencies.Add(firstMs);
        }

        if (rates.Count == 0) return null;
        return Judge("read", rates, latencies, requiredKbps);
    }

    private static NetworkMeasurement? FromIperf3(string host, double requiredKbps)
    {
        // -J is JSON, -t 3 keeps the probe short; a running server is required.
        FFmpeg.CaptureResult run;
        try
        {
            run = FFmpeg.ProcessRunner.Run(["iperf3", "-c", host, "-t", "3", "-J"], timeoutSeconds: 25);
        }
        catch (Exception exc) when (exc is System.ComponentModel.Win32Exception
                                       or InvalidOperationException)
        {
            return null;        // no iperf3 on PATH
        }
        if (run.ExitCode != 0 || !run.Stdout.HasText) return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(run.Stdout);
            if (!doc.RootElement.TryGetProperty("end", out JsonElement end)) return null;
            double bits = 0;
            if (end.TryGetProperty("sum_received", out JsonElement received)
                && received.TryGetProperty("bits_per_second", out JsonElement bps))
                bits = bps.GetDouble();
            else if (end.TryGetProperty("sum", out JsonElement sum)
                     && sum.TryGetProperty("bits_per_second", out JsonElement sumBps))
                bits = sumBps.GetDouble();
            if (bits <= 0) return null;

            double mib = bits / 8.0 / (1024 * 1024);
            // iperf3 reports one aggregate number, so there is no spread to
            // measure; treat it as a stable sample rather than inventing jitter.
            return Judge("iperf3", [mib], [0.0], requiredKbps);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NetworkMeasurement Judge(
        string kind, List<double> rates, List<double> latencies, double requiredKbps)
    {
        rates.Sort();
        latencies.Sort();
        double median = rates[rates.Count / 2];
        double spread = rates.Count > 1 ? rates[^1] - rates[0] : 0;
        double jitter = median > 0 ? spread / median * 100.0 : 0;
        double firstByte = latencies[latencies.Count / 2];

        double neededMib = requiredKbps / 8000.0;       // kbps -> MiB/s
        bool adequate = median >= neededMib * HeadroomFactor
                        && jitter <= JitterLimitPercent
                        && firstByte <= LatencyLimitMs;

        return new NetworkMeasurement(kind, median, firstByte, jitter, rates.Count, requiredKbps, adequate);
    }
}
