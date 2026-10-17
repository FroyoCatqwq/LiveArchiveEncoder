using System.Text;
using LiveArchiveEncoder.RateControl;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>
/// Asynchronous line readers for FFmpeg's stderr pipes.
///
/// Both the decoder and the encoder write diagnostics continuously; if nobody
/// drains those pipes the child process blocks on a full pipe buffer and the
/// whole encode stalls. These readers never touch the frame path.
/// </summary>
public static class PipeReaders
{
    private const int BlockSize = 16 * 1024;

    public static async Task ProgressReaderAsync(
        Stream stderr, OutputMonitor monitor, CancellationToken token)
    {
        long? size = null;
        long? outTimeUs = null;

        bool Handle(ReadOnlySpan<byte> line)
        {
            int eq = line.IndexOf((byte)'=');
            if (eq < 0)
            {
                monitor.AddDiagnostic(Decode(line));
                return true;
            }

            ReadOnlySpan<byte> key = line[..eq];
            ReadOnlySpan<byte> value = line[(eq + 1)..];

            if (key.SequenceEqual("total_size"u8))
            {
                size = ParseInt64(value);
            }
            else if (key.SequenceEqual("out_time_us"u8))
            {
                outTimeUs = ParseInt64(value);
            }
            else if (key.SequenceEqual("progress"u8))
            {
                if (size is not null && outTimeUs is not null)
                {
                    monitor.AddProgress(size.Value, outTimeUs.Value);
                }
                if (value.SequenceEqual("end"u8)) monitor.ProgressEnded = true;
                size = null;
                outTimeUs = null;
            }
            return true;
        }

        await ForEachLineAsync(stderr, Handle, token, ex =>
            monitor.AddDiagnostic($"progress reader: {ex.Message}"));
    }

    public static Task DiagnosticsReaderAsync(
        Stream stderr, Action<string> onLine, CancellationToken token)
    {
        return ForEachLineAsync(stderr, line => { onLine(Decode(line)); return true; }, token,
            ex => onLine($"decoder stderr reader: {ex.Message}"));
    }

    private static async Task ForEachLineAsync(
        Stream stream,
        Func<ReadOnlySpan<byte>, bool> onLine,
        CancellationToken token,
        Action<Exception>? onError = null)
    {
        byte[] block = new byte[BlockSize];
        byte[] line = new byte[512];
        int lineLength = 0;

        static void TrimRange(byte[] buffer, ref int start, ref int length)
        {
            while (length > 0 && buffer[start] is (byte)' ' or (byte)'\t') { start++; length--; }
            while (length > 0 && buffer[start + length - 1] is (byte)' ' or (byte)'\t') length--;
        }

        try
        {
            int read;
            while (!token.IsCancellationRequested
                   && (read = await stream.ReadAsync(block, token).ConfigureAwait(false)) > 0)
            {
                int segmentStart = 0;
                for (int i = 0; i < read; i++)
                {
                    if (block[i] != (byte)'\n') continue;

                    int segmentLength = i - segmentStart;
                    if (segmentLength > 0 && block[segmentStart + segmentLength - 1] == (byte)'\r')
                        segmentLength--;

                    if (lineLength + segmentLength > line.Length)
                    {
                        Array.Resize(ref line, Math.Max(line.Length * 2, lineLength + segmentLength));
                    }
                    Buffer.BlockCopy(block, segmentStart, line, lineLength, segmentLength);
                    lineLength += segmentLength;

                    int start = 0;
                    TrimRange(line, ref start, ref lineLength);
                    if (lineLength > 0 && !onLine(line.AsSpan(start, lineLength))) return;

                    lineLength = 0;
                    segmentStart = i + 1;
                }

                if (segmentStart < read)
                {
                    int tail = read - segmentStart;
                    if (lineLength + tail > line.Length)
                    {
                        Array.Resize(ref line, Math.Max(line.Length * 2, lineLength + tail));
                    }
                    Buffer.BlockCopy(block, segmentStart, line, lineLength, tail);
                    lineLength += tail;
                }
            }

            if (lineLength > 0)
            {
                int start = 0;
                TrimRange(line, ref start, ref lineLength);
                if (lineLength > 0) onLine(line.AsSpan(start, lineLength));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
    }

    private static string Decode(ReadOnlySpan<byte> line) =>
        System.Text.Encoding.UTF8.GetString(line);

    private static long? ParseInt64(ReadOnlySpan<byte> text) =>
        long.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out long value)
            ? value
            : null;
}
