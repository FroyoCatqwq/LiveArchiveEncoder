using LiveArchiveEncoder.Remote;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// UNC detection and the FFmpeg resilience options for network sources.
///
/// These run without a share: everything asserted here is pure string and
/// argument-vector logic, which is exactly what went wrong in v7.3.
/// </summary>
[TestFixture]
public sealed class SmbTests
{
    [TestCase(@"\\nas\share\clip.mp4")]
    [TestCase("//nas/share/clip.mp4")]
    [TestCase(@"\\nas\share")]
    public void IsUncPath_AcceptsBothSlashFlavours(string path) =>
        Assert.That(Smb.IsUncPath(path), Is.True);

    [TestCase(@"C:\video\clip.mp4")]
    [TestCase("clip.mp4")]
    [TestCase("")]
    [TestCase(@"\\")]
    [TestCase(null)]
    public void IsUncPath_RejectsLocalAndEmpty(string? path) =>
        Assert.That(Smb.IsUncPath(path), Is.False);

    [Test]
    public void Normalize_TurnsForwardSlashesIntoBackslashes() =>
        Assert.That(Smb.Normalize("//nas/share/a.mp4"), Is.EqualTo(@"\\nas\share\a.mp4"));

    [Test]
    public void ShareRoot_ReturnsServerAndShareOnly()
    {
        Assert.That(Smb.ShareRoot(@"\\nas\share\deep\a.mp4"), Is.EqualTo(@"\\nas\share"));
        Assert.That(Smb.ShareRoot(@"C:\video\a.mp4"), Is.Null);
    }

    [Test]
    public void Split_ExposesServerShareRemainder()
    {
        var parts = Smb.Split(@"\\nas\share\deep\a.mp4");
        Assert.That(parts, Is.Not.Null);
        Assert.That(parts!.Value.Server, Is.EqualTo("nas"));
        Assert.That(parts.Value.Share, Is.EqualTo("share"));
        Assert.That(parts.Value.Remainder, Is.EqualTo(@"deep\a.mp4"));
    }

    /// <summary>
    /// Two options that looked right and broke every remote source.
    ///
    /// <c>-stimeout</c> (v7.3) is an ffplay option -- <c>ffmpeg -h full</c> does
    /// not list it -- and FFmpeg aborts with "Unrecognized option 'stimeout'"
    /// before reading a single frame.
    ///
    /// The <c>-reconnect*</c> family (v7.4) belongs to the HTTP/FTP protocols.
    /// It is listed as <c>D</c> (decoding only) and older builds ignored it on a
    /// UNC path, but a current master build answers
    /// "Option reconnect not found. Error opening input file ...". Measured on a
    /// BtbN linux64 build against a real CIFS mount: the same 2 s segment
    /// decoded to 26 frames without it and to 0 frames with it.
    ///
    /// A share needs none of it: UNC reconnection is the OS's job and a CIFS
    /// mount is the kernel's.
    /// </summary>
    [Test]
    public void RemoteReadArgs_IsTimeoutOnly()
    {
        List<string> args = Smb.RemoteReadArgs(@"\\nas\share\a.mp4");

        Assert.That(args, Does.Not.Contain("-stimeout"));
        Assert.That(args, Does.Not.Contain("-reconnect"));
        Assert.That(args, Does.Not.Contain("-reconnect_streamed"));
        Assert.That(args, Does.Not.Contain("-reconnect_delay_max"));
        Assert.That(args, Is.EqualTo(new[] { "-rw_timeout", "30000000" }));
    }

    /// <summary>
    /// Read, write and probe must agree: any option that one side can carry and
    /// another cannot is exactly how the reconnect bug got in.
    /// </summary>
    [TestCase(@"\\nas\share\a.mp4")]
    [TestCase("smb://nas/share/a.mp4")]
    public void RemoteArgs_AreIdenticalForReadWriteAndProbe(string path)
    {
        List<string> read = Smb.RemoteReadArgs(path);
        List<string> write = Smb.RemoteWriteArgs(path);
        List<string> probe = Smb.RemoteProbeArgs(path);

        Assert.That(read, Is.EqualTo(write));
        Assert.That(read, Is.EqualTo(probe));
    }

    [Test]
    public void RemoteReadArgs_LocalFileGetsNothing() =>
        Assert.That(Smb.RemoteReadArgs(@"C:\video\a.mp4"), Is.Empty);

    /// <summary>
    /// The <c>-reconnect</c> family is flagged <c>D</c> (decoding only) in
    /// <c>ffmpeg -h full</c>, so it is rejected on the output side.
    /// <c>-rw_timeout</c> is flagged <c>ED</c> and is the only option left.
    /// </summary>
    [Test]
    public void RemoteWriteArgs_IsTimeoutOnly()
    {
        List<string> args = Smb.RemoteWriteArgs(@"\\nas\share\out.rm");

        Assert.That(args, Is.EqualTo(new[] { "-rw_timeout", "30000000" }));
        Assert.That(Smb.RemoteWriteArgs(@"C:\video\out.rm"), Is.Empty);
    }

    [Test]
    public void RemoteProbeArgs_UsesTheOnlyOptionFfprobeKnows() =>
        Assert.That(Smb.RemoteProbeArgs(@"\\nas\share\a.mp4"),
            Is.EqualTo(new[] { "-rw_timeout", "30000000" }));

    [Test]
    public void Describe_ReportsSomethingForUncAndNothingForLocal()
    {
        Assert.That(Smb.Describe(@"\\nas\share\a.mp4"), Is.Not.Null);
        Assert.That(Smb.DescribeOutput(@"\\nas\share\out.rm"), Is.Not.Null);
        Assert.That(Smb.Describe(@"C:\video\a.mp4"), Is.Null);
    }

    /// <summary>
    /// The probe is UNC-only (a local path is never worth a round-trip), so the
    /// local case is only asserted to be a harmless "yes". What matters is that
    /// it never throws and never leaves a marker behind.
    /// </summary>
    [Test]
    public void CanWriteDirectory_ProbesWithoutLeavingAMarkerBehind()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lae-smb-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        Assert.That(Smb.CanWriteDirectory(dir), Is.True);
        Assert.That(Directory.EnumerateFileSystemEntries(dir), Is.Empty,
            "the marker file must be deleted again");

        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public void CanWriteDirectory_LocalPathIsAlwaysYesWithoutTouchingDisk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lae-never-" + Guid.NewGuid().ToString("N"));

        Assert.That(Smb.CanWriteDirectory(dir), Is.True);
        Assert.That(Directory.Exists(dir), Is.False,
            "a local destination must not be created by a probe");
    }
}
