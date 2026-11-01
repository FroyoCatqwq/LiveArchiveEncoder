using System.Net;
using System.Net.Http;
using Lae.Core;
using Lae.Core.Localization;

namespace Lae.Media.Toolchain;

/// <summary>
/// One HTTP GET written straight into a file.
///
/// <para>Shared by the two downloads this program makes -- an FFmpeg archive and
/// the NNEDI3 weights -- because the interesting parts are not the transfer: they
/// are the connect timeout, the idle deadline that ends a connection which has
/// stopped delivering, the progress line, and the rule that a response body is
/// quoted in the error but never substituted for the failure.</para>
///
/// <para>Returns null on success and a sentence on failure. Not an exception: a
/// download that fails is an expected outcome, and both callers report it rather
/// than unwind. A cancellation is the one exception -- it belongs to the caller
/// and comes back as <see cref="OperationCanceledException"/>, which is what makes
/// Ctrl+C exit 130 instead of "download failed".</para>
/// </summary>
internal static class HttpDownload
{
    /// <summary>How long a single read may take before the connection is called dead.</summary>
    private const int IdleSeconds = 60;

    public static string? Fetch(
        string url, string destination, string? proxy, IMessages messages,
        Action<string>? log, CancellationToken token)
    {
        // SocketsHttpHandler rather than HttpClientHandler: the latter does not
        // expose ConnectTimeout, and a host that never answers would otherwise
        // wait for the operating system's own timeout, which is minutes.
        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) };

        if (proxy.HasText)
        {
            if (!Uri.TryCreate(proxy, UriKind.Absolute, out Uri? proxyUri))
                return messages.Format("toolchain.error.badProxy", new { value = proxy });

            handler.Proxy = new WebProxy(proxyUri);
            handler.UseProxy = true;
        }

        using var client = new HttpClient(handler, disposeHandler: true)
        {
            // No total timeout: an archive is 80-200 MB and a slow link is not a
            // failure. The idle deadline below is what ends a connection that has
            // stopped delivering.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveArchiveEncoder");

        try
        {
            using HttpResponseMessage response = client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                .GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                string detail = "";
                try
                {
                    string page = response.Content.ReadAsStringAsync(token).GetAwaiter().GetResult();
                    if (page.Trim() is { Length: > 0 } trimmed) detail = " -- " + Head(trimmed.Replace('\n', ' '), 160);
                }
                catch (Exception exc) when (exc is HttpRequestException or IOException or OperationCanceledException)
                {
                    // The body is a nicety; the status line is the answer.
                }

                return messages.Format("toolchain.error.httpStatus", new
                {
                    code = (int)response.StatusCode,
                    reason = response.ReasonPhrase ?? "",
                    detail,
                });
            }

            long? total = response.Content.Headers.ContentLength;
            using Stream body = response.Content.ReadAsStreamAsync(token).GetAwaiter().GetResult();
            using FileStream target = File.Create(destination);

            byte[] buffer = new byte[1024 * 1024];
            long written = 0;
            long nextReport = 0;

            while (true)
            {
                int read;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(IdleSeconds));
                    try
                    {
                        read = body.ReadAsync(buffer, idle.Token).AsTask().GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        return messages.Format("toolchain.error.stalled", new
                        {
                            seconds = IdleSeconds,
                            mb = written / (1024 * 1024),
                        });
                    }
                }

                if (read <= 0) break;

                target.Write(buffer, 0, read);
                written += read;

                if (log is not null && written >= nextReport)
                {
                    // A tenth of the file, but never less than a megabyte: the
                    // weights are 13 MB and a fixed 20 MB step printed one line --
                    // "0 / 12 MB" -- for the whole transfer, which reads like a
                    // hang. An archive of 150 MB still gets ten lines, not a
                    // hundred.
                    nextReport = written + (total is { } length && length > 0
                        ? Math.Max(1L << 20, length / 10)
                        : 20L << 20);

                    log(total is { } size
                        ? messages.Format("toolchain.log.progress", new
                        {
                            done = written / (1024 * 1024),
                            total = size / (1024 * 1024),
                            percent = (int)(100.0 * written / Math.Max(1, size)),
                        })
                        : messages.Format("toolchain.log.progressUnknown", new { done = written / (1024 * 1024) }));
                }
            }

            return null;
        }
        catch (Exception exc) when (exc is HttpRequestException or IOException or TaskCanceledException
                                        or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            // A cancel is the user's decision, and the caller's catch turns it
            // into exit 130; everything else is a sentence.
            if (token.IsCancellationRequested) throw;

            return exc.Message;
        }
    }

    /// <summary>
    /// The size the server would send, or null when it will not say.
    ///
    /// <para>This exists so "already current" can be answered without
    /// transferring a file to find out. A server that refuses HEAD, or a proxy
    /// that eats it, is not an error: null simply means "ask by downloading".
    /// </para>
    /// </summary>
    public static long? Length(string url, string? proxy, CancellationToken token)
    {
        try
        {
            using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) };

            if (proxy.HasText)
            {
                if (!Uri.TryCreate(proxy, UriKind.Absolute, out Uri? proxyUri)) return null;

                handler.Proxy = new WebProxy(proxyUri);
                handler.UseProxy = true;
            }

            using var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveArchiveEncoder");

            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using HttpResponseMessage response = client.Send(
                request, HttpCompletionOption.ResponseHeadersRead, token);

            return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength : null;
        }
        catch (Exception exc) when (exc is HttpRequestException or IOException or TaskCanceledException
                                        or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            if (token.IsCancellationRequested) throw;
            return null;
        }
    }

    /// <summary>The first <paramref name="max"/> characters of a page body, for an error line.</summary>
    private static string Head(string text, int max)
        => text.Length <= max ? text : text[..max] + "...";
}
