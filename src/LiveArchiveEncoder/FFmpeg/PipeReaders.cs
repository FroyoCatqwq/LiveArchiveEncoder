using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using LiveArchiveEncoder.Diagnostics;
using LiveArchiveEncoder.RateControl;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>
/// Asynchronous line readers for FFmpeg's stderr pipes.
///
/// Both the decoder and the encoder write diagnostics continuously; if nobody
/// drains those pipes the child process blocks on a full pipe buffer and the
/// whole encode stalls. These readers never touch the frame path.
///
/// <para>v7.9.9: the hand-rolled byte[]/BlockCopy line splitter is now a
/// <see cref="PipeReader"/> over the stream plus a
/// <see cref="SequenceReader{T}"/>. Same API, same trimming rules, same
/// cancellation and error callbacks; the difference is that buffering across
/// reads is handled by System.IO.Pipelines instead of two growable arrays.</para>
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
                string diagnostic = Decode(line);
                AppLog.Sampled("ffmpeg.stderr {Line}", diagnostic);
                monitor.AddDiagnostic(diagnostic);
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
        return ForEachLineAsync(stderr, line =>
        {
            string text = Decode(line);
            AppLog.Sampled("ffmpeg.stderr {Line}", text);
            onLine(text);
            return true;
        }, token, ex => onLine($"decoder stderr reader: {ex.Message}"));
    }

    private static async Task ForEachLineAsync(
        Stream stream,
        Func<ReadOnlySpan<byte>, bool> onLine,
        CancellationToken token,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(stream);   // .NET 6+ guard
        ArgumentNullException.ThrowIfNull(onLine);
        PipeReader reader = PipeReader.Create(stream, new StreamPipeReaderOptions(
            bufferSize: BlockSize, minimumReadSize: BlockSize, leaveOpen: true));
        byte[] line = new byte[512];

        try
        {
            while (!token.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(token).ConfigureAwait(false);
                if (result.IsCanceled) break;

                ReadOnlySequence<byte> buffer = result.Buffer;
                var sequence = new SequenceReader<byte>(buffer);

                while (sequence.TryReadTo(out ReadOnlySequence<byte> raw, (byte)'\n'))
                {
                    if (!Emit(raw, ref line, onLine))
                    {
                        reader.AdvanceTo(buffer.End);
                        return;
                    }
                }

                if (result.IsCompleted)
                {
                    // A final line without a trailing newline is still a line.
                    Emit(buffer.Slice(sequence.Position), ref line, onLine);
                    reader.AdvanceTo(buffer.End);
                    break;
                }

                reader.AdvanceTo(sequence.Position, buffer.End);
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
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Copy one (possibly multi-segment) sequence into the reusable buffer,
    /// strip CR and leading/trailing whitespace, then hand a contiguous span
    /// to the caller. Returning false stops the reader.
    /// </summary>
    private static bool Emit(ReadOnlySequence<byte> raw, ref byte[] line,
        Func<ReadOnlySpan<byte>, bool> onLine)
    {
        int length = (int)raw.Length;
        if (length == 0) return true;
        if (length > line.Length)
            Array.Resize(ref line, Math.Max(line.Length * 2, length));
        raw.CopyTo(line.AsSpan(0, length));

        if (line[length - 1] == (byte)'\r') length--;
        int start = 0;
        while (start < length && line[start] is (byte)' ' or (byte)'\t') start++;
        while (length > start && line[length - 1] is (byte)' ' or (byte)'\t') length--;

        return length <= start || onLine(line.AsSpan(start, length - start));
    }

    private static string Decode(ReadOnlySpan<byte> line) => System.Text.Encoding.UTF8.GetString(line);

    private static long? ParseInt64(ReadOnlySpan<byte> text) =>
        long.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out long value)
            ? value
            : null;
}
