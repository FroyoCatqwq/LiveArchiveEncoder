using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiveArchiveEncoder.Core;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>
/// v7.9.8.13.1: resolve a distribution's download URL. Most sources publish a
/// fixed <c>releases/latest/download/&lt;name&gt;</c> URL; a per-commit project
/// (AnimMouse) tags every build with a hash, so its asset has to be looked up in
/// the release feed. When the daily feed cannot be read, the catalogue's stable
/// asset is used -- the "daily first, stable only if there is no daily" rule.
/// </summary>
public static class ReleaseFeed
{
    public static (string Url, string? Error) Resolve(
        FfmpegDist dist, string? ghPrefix, string? proxy, Action<string> say)
    {
        if (dist.Url is { Length: > 0 } fixedUrl) return (fixedUrl, null);
        if (dist.FeedUrl is not { Length: > 0 } feed)
            return (dist.StableUrl ?? "", null);

        (JsonDocument? doc, string? error) = GetJson(GitHubProxy.Apply(feed, ghPrefix), proxy);
        if (doc is null)
        {
            say($"Daily feed unavailable ({error}); falling back to the stable asset.");
            return (dist.StableUrl ?? "", error);
        }

        // v7.9.8.13.2: an upstream feed can be malformed (assets not an array,
        // a missing name, a pathological pattern). That must degrade to the
        // stable fallback, never crash the installer.
        try
        {
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("assets", out JsonElement assets)
                    || assets.ValueKind != JsonValueKind.Array)
                    return (dist.StableUrl ?? "", "release feed has no asset list");

                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object) continue;
                    if (!asset.TryGetProperty("name", out JsonElement nameElement)) continue;
                    string name = nameElement.GetString() ?? "";
                    if (dist.AssetPattern is { Length: > 0 }
                        && !Regex.IsMatch(name, dist.AssetPattern,
                            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))) continue;
                    if (asset.TryGetProperty("browser_download_url", out JsonElement link)
                        && link.GetString() is { Length: > 0 } href)
                    {
                        say($"Daily asset: {name}");
                        return (href, null);
                    }
                }
            }
        }
        catch (Exception exc) when (exc is InvalidOperationException or KeyNotFoundException
                                        or RegexMatchTimeoutException or ArgumentException)
        {
            say($"Daily feed malformed ({exc.Message}); falling back to the stable asset.");
            return (dist.StableUrl ?? "", exc.Message);
        }
        return (dist.StableUrl ?? "", "no asset in the daily feed matched");
    }

    private static (JsonDocument? Doc, string? Error) GetJson(string url, string? proxy)
    {
        try
        {
            using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20) };
            if (proxy.HasText)
            {
                if (!Uri.TryCreate(proxy, UriKind.Absolute, out Uri? uri))
                    return (null, $"invalid proxy address '{proxy}'");
                handler.Proxy = new WebProxy(uri);
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveArchiveEncoder");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return (JsonDocument.Parse(body), null);
        }
        catch (Exception exc) when (exc is HttpRequestException or IOException
                                        or TaskCanceledException or JsonException
                                        or InvalidOperationException)
        {
            return (null, exc.Message);
        }
    }
}
