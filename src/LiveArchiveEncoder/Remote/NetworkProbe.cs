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

    /// <summary>Wall-clock budget for the whole HTTP measurement (all samples).</summary>
    private const int HttpBudgetSeconds = 15;

    /// <summary>Rate samples for an HTTP source, on top of one warm-up read.</summary>
    private const int RateSamples = 3;

    /// <summary>
    /// Shortest sample that can say anything about jitter.
    ///
    /// <para>A 512 KiB read that completes in two milliseconds is measured
    /// better than the machine's own timer: the spread between two such samples
    /// is noise, not link quality, and it routinely exceeded the 30% limit. On a
    /// loopback that made a 300 MB/s link look too unstable to shard, so samples
    /// below this floor contribute to the median throughput but not to the
    /// jitter term.</para>
    /// </summary>
    private const double MinTimedSampleSeconds = 0.01;

    /// <summary>One throughput reading: the rate, and how long it took to get.</summary>
    private readonly record struct Sample(double MibPerSecond, double Seconds);

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
                                       or NotSupportedException or ArgumentException
                                       or HttpRequestException or OperationCanceledException)
        {
            // A share that cannot be read at all is a fact worth reporting, but
            // not a crash: the single-pass run will produce the real error.
            //
            // HttpRequestException and the 20 s TaskCanceledException are the
            // two ways an HTTP source fails here, and neither derives from
            // IOException -- on a box behind a proxy that answers 502 this path
            // never ran, so the gap only showed on a plain network.
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
        var rates = new List<Sample>(wanted);
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

            // Same split as the HTTP path: the first read is the latency, the
            // rest is the rate, so a share's seek time cannot masquerade as
            // jitter between samples.
            sw.Restart();
            int total = first;
            while (total < sampleBytes)
            {
                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, sampleBytes - total));
                if (read <= 0) break;
                total += read;
            }
            double seconds = sw.Elapsed.TotalSeconds;
            if (seconds <= 0 || total <= first) continue;
            rates.Add(new Sample((total - first) / seconds / (1024 * 1024), seconds));
            latencies.Add(firstMs);
        }

        if (rates.Count == 0) return null;
        return Judge("read", rates, latencies, requiredKbps);
    }

    private static NetworkMeasurement? FromHttp(string source, double requiredKbps)
    {
        // Two things here are deliberate and were both learned the hard way.
        //
        // <b>The budget is a token, not HttpClient.Timeout.</b> With
        // ResponseHeadersRead the client's own timeout stops applying the moment
        // the headers are in, so a server that advertises a Content-Length and
        // then stops sending -- or a link that stalls mid-body -- blocked the
        // very first probe forever, before a single line of output. In a bug
        // report that reads as "the program hangs on my NAS", with nothing to go
        // on. Cancelling the token aborts the connection and the measurement
        // degrades to "could not measure", which the caller already handles.
        //
        // <b>The sample size follows the file.</b> Asking for 2 MiB of an
        // 800 KiB file gets one short read and then a 416 on the second sample,
        // i.e. no verdict at all on exactly the small files a probe can measure
        // fastest.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(HttpBudgetSeconds));
        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var rates = new List<Sample>(RateSamples);
        var latencies = new List<double>(RateSamples + 1);
        byte[] buffer = new byte[64 * 1024];
        long? length = null;
        int sampleBytes = SampleBytes;

        for (int i = 0; i <= RateSamples; i++)
        {
            // Sample 0 starts at byte 0 and is the warm-up: its timing is the
            // latency a user feels, but its rate is not a rate sample. A cold
            // connection spends the first reads in slow start, and letting that
            // into the spread made a loopback look like 96% jitter -- a fast link
            // voted too unstable to shard. The rest of the samples are spread
            // across the file, because its head is what every server caches.
            long start;
            if (i == 0) start = 0;
            else if (length is { } fileSize)
                start = Math.Max(1, (long)(fileSize * (0.1 + 0.8 * (i - 1) / (RateSamples - 1.0))));
            else break;                         // a server that hides its length gets one honest sample

            long end = start + sampleBytes - 1;
            if (length is { } knownSize && end > knownSize - 1) end = knownSize - 1;
            if (end < start) break;

            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
            var clock = Stopwatch.StartNew();
            using HttpResponseMessage response =
                client.Send(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if (!response.IsSuccessStatusCode) break;

            length ??= TotalLength(response);
            if (length is { } known && i == 0)
                sampleBytes = (int)Math.Clamp(known / 4, MinSampleBytes, SampleBytes);

            using Stream body = response.Content.ReadAsStream(budget.Token);
            int first = ReadInterruptibly(body, buffer, buffer.Length, budget.Token);
            double firstMs = clock.Elapsed.TotalMilliseconds;
            if (first <= 0) break;

            // Throughput is measured from the first byte on, not from the
            // request: the connect and the response headers are the latency
            // number, not part of the rate.
            clock.Restart();
            int total = first;
            while (total < sampleBytes)
            {
                int read = ReadInterruptibly(
                    body, buffer, Math.Min(buffer.Length, sampleBytes - total), budget.Token);
                if (read <= 0) break;
                total += read;
            }
            latencies.Add(firstMs);
            double seconds = clock.Elapsed.TotalSeconds;
            if (i > 0 && seconds > 0 && total > first)
                rates.Add(new Sample((total - first) / seconds / (1024 * 1024), seconds));
        }

        if (rates.Count == 0) return null;
        return Judge("read", rates, latencies, requiredKbps);
    }

    /// <summary>
    /// One blocking read that <paramref name="token"/> can interrupt.
    ///
    /// <see cref="Stream.Read(Span{byte})"/> ignores cancellation, so a stalled
    /// body would never come back; the async overload registers the token with
    /// the connection and is waited on synchronously here (there is no
    /// synchronization context in a console tool).
    /// </summary>
    private static int ReadInterruptibly(Stream stream, byte[] buffer, int count, CancellationToken token) =>
        stream.ReadAsync(buffer.AsMemory(0, count), token).AsTask().GetAwaiter().GetResult();

    /// <summary>Total size of the resource, as the response describes it.</summary>
    private static long? TotalLength(HttpResponseMessage response)
    {
        // ContentLength is this range's length on a 206, and the whole resource
        // on a 200 -- hence ContentRange first.
        if (response.Content.Headers.ContentRange?.Length is { } whole) return whole;
        return response.Content.Headers.ContentLength;
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
            // iperf3 reports one aggregate number over three seconds, so there
            // is no spread to measure; treat it as a stable sample rather than
            // inventing jitter.
            return Judge("iperf3", [new Sample(mib, 3.0)], [0.0], requiredKbps);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NetworkMeasurement Judge(
        string kind, List<Sample> samples, List<double> latencies, double requiredKbps)
    {
        latencies.Sort();
        List<double> rates = [.. samples.Select(s => s.MibPerSecond).Order()];
        double median = rates[rates.Count / 2];

        // Jitter only from samples long enough to time -- see MinTimedSampleSeconds.
        // One such sample means the spread is unknown, which is reported as 0
        // rather than as a number the timer invented.
        List<double> timed = [.. samples.Where(s => s.Seconds >= MinTimedSampleSeconds)
                                        .Select(s => s.MibPerSecond).Order()];
        double jitter = 0;
        if (timed.Count > 1 && median > 0)
            jitter = (timed[^1] - timed[0]) / median * 100.0;

        double firstByte = latencies[latencies.Count / 2];

        double neededMib = requiredKbps / 8000.0;       // kbps -> MiB/s
        bool adequate = median >= neededMib * HeadroomFactor
                        && jitter <= JitterLimitPercent
                        && firstByte <= LatencyLimitMs;

        return new NetworkMeasurement(kind, median, firstByte, jitter, rates.Count, requiredKbps, adequate);
    }
}
