using LiveArchiveEncoder.Remote;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// HTTP/HTTPS input recognition and the protocol options FFmpeg needs for it.
///
/// The interesting half of v7.9 goal 6 is not the URL parsing -- it is that
/// <c>-reconnect*</c> is legal on http:// (and measured so, 2026-10-21) while
/// being rejected outright on a UNC path, which is what
/// <see cref="SmbTests.RemoteReadArgs_IsTimeoutOnly"/> pins down. Both files
/// assert the same wall from opposite sides.
/// </summary>
public sealed class HttpSourceTests
{
    [Test]
    [Arguments("http://archive.org/download/item/clip.mp4")]
    [Arguments("https://archive.org/download/item/clip.mp4")]
    [Arguments("HTTPS://ARCHIVE.ORG/clip.mp4")]
    public async Task IsUrl_AcceptsBothSchemesInAnyCase(string url) =>
        await Assert.That(HttpSource.IsUrl(url)).IsTrue();

    [Test]
    [Arguments(@"C:\video\clip.mp4")]
    [Arguments(@"\\nas\share\clip.mp4")]
    [Arguments("smb://nas/share/clip.mp4")]
    [Arguments("ftp://host/clip.mp4")]
    [Arguments("clip.mp4")]
    [Arguments("")]
    [Arguments(null)]
    public async Task IsUrl_RejectsEverythingElse(string? path) =>
        await Assert.That(HttpSource.IsUrl(path)).IsFalse();

    /// <summary>
    /// The archive.org download URL the feature was specced against: query
    /// string dropped, last path segment kept.
    /// </summary>
    [Test]
    public async Task FileNameFromUrl_TakesTheLastSegmentAndDropsTheQuery() =>
        await Assert.That(HttpSource.FileNameFromUrl(
            "https://archive.org/download/MacArthur_Foundation_100andChange_dQw4w9WgXcQ"
            + "/Rick_Astley_-_Never_Gonna_Give_You_Up_dQw4w9WgXcQ.mp4?download=1"))
            .IsEqualTo("Rick_Astley_-_Never_Gonna_Give_You_Up_dQw4w9WgXcQ.mp4");

    [Test]
    [Arguments("https://host/clip.mp4#t=10", "clip.mp4")]
    [Arguments("https://host/", "remote")]
    [Arguments("https://host", "host")]
    [Arguments("https://host/a/b/c.avi", "c.avi")]
    public async Task FileNameFromUrl_HandlesFragmentsAndBareHosts(string url, string expected) =>
        await Assert.That(HttpSource.FileNameFromUrl(url)).IsEqualTo(expected);

    /// <summary>
    /// Percent-decoding is not optional: archive.org serves spaces as %20, and
    /// a literal %20 in an output name is useless to the user.
    /// </summary>
    [Test]
    public async Task FileNameFromUrl_PercentDecodes() =>
        await Assert.That(HttpSource.FileNameFromUrl("https://host/my%20clip.mp4"))
            .IsEqualTo("my clip.mp4");

    /// <summary>
    /// A URL must never be able to steer the default output outside the current
    /// directory: separators that survive decoding become underscores.
    /// </summary>
    [Test]
    [Arguments("https://host/a%2F..%5C..%5Cwindows.mp4", "a_.._.._windows.mp4")]
    [Arguments("https://host/%2e%2e%2fetc%2fpasswd", ".._etc_passwd")]
    public async Task FileNameFromUrl_SanitisesSeparatorsAfterDecoding(string url, string expected) =>
        await Assert.That(HttpSource.FileNameFromUrl(url)).IsEqualTo(expected);

    [Test]
    public async Task FileNameFromUrl_EmptyBodyFallsBackToRemote() =>
        await Assert.That(HttpSource.FileNameFromUrl("https://host/?download=1")).IsEqualTo("remote");

    /// <summary>
    /// The full resilience set, and only it: <c>-reconnect_on_http_error</c>
    /// stays out on purpose -- a 404 is a wrong URL, and retrying it five times
    /// turns a two-second error into a two-minute one.
    /// </summary>
    [Test]
    public async Task ReadArgs_UrlGetsTheReconnectFamilyAndAReadTimeout()
    {
        List<string> args = HttpSource.ReadArgs("https://host/clip.mp4");

        await Assert.That(args).IsEquivalentTo(new[]
        {
            "-reconnect", "1",
            "-reconnect_streamed", "1",
            "-reconnect_on_network_error", "1",
            "-rw_timeout", "30000000",
        });
    }

    [Test]
    public async Task ReadArgs_LocalPathGetsNothing() =>
        await Assert.That(HttpSource.ReadArgs(@"C:\video\clip.mp4")).IsEmpty();

    /// <summary>
    /// ffprobe carries the socket timeout but not the reconnect family -- the
    /// probe is a single short request, and the measured behaviour (2026-10-21)
    /// is that ffprobe accepts both anyway; keeping the probe minimal means a
    /// dead URL fails in the same place it always did.
    /// </summary>
    [Test]
    public async Task ProbeArgs_UrlGetsTimeoutOnly()
    {
        List<string> args = HttpSource.ProbeArgs("https://host/clip.mp4");

        await Assert.That(args).IsEquivalentTo(new[] { "-rw_timeout", "30000000" });
    }

    [Test]
    public async Task ProbeArgs_LocalPathGetsNothing() =>
        await Assert.That(HttpSource.ProbeArgs(@"C:\video\clip.mp4")).IsEmpty();

    /// <summary>
    /// The probe and the read must not drift apart on which paths count as
    /// remote, or a URL would decode fine and then open without a timeout.
    /// </summary>
    [Test]
    [Arguments("https://host/clip.mp4")]
    [Arguments("http://host/clip.mp4")]
    [Arguments(@"C:\video\clip.mp4")]
    [Arguments(@"\\nas\share\clip.mp4")]
    public async Task ReadAndProbeAgreeOnWhatIsRemote(string path) =>
        await Assert.That(HttpSource.ReadArgs(path).Count > 0)
            .IsEqualTo(HttpSource.ProbeArgs(path).Count > 0);
}
