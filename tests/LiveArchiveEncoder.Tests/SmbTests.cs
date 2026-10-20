using LiveArchiveEncoder.Remote;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// UNC detection and the FFmpeg resilience options for network sources.
///
/// These run without a share: everything asserted here is pure string and
/// argument-vector logic, which is exactly what went wrong in v7.3.
/// </summary>
public sealed class SmbTests
{
    [Test]
    [Arguments(@"\\nas\share\clip.mp4")]
    [Arguments("//nas/share/clip.mp4")]
    [Arguments(@"\\nas\share")]
    public async Task IsUncPath_AcceptsBothSlashFlavours(string path) =>
        await Assert.That(Smb.IsUncPath(path)).IsTrue();

    [Test]
    [Arguments(@"C:\video\clip.mp4")]
    [Arguments("clip.mp4")]
    [Arguments("")]
    [Arguments(@"\\")]
    [Arguments(null)]
    public async Task IsUncPath_RejectsLocalAndEmpty(string? path) =>
        await Assert.That(Smb.IsUncPath(path)).IsFalse();

    [Test]
    public async Task Normalize_TurnsForwardSlashesIntoBackslashes() =>
        await Assert.That(Smb.Normalize("//nas/share/a.mp4")).IsEqualTo(@"\\nas\share\a.mp4");

    [Test]
    public async Task ShareRoot_ReturnsServerAndShareOnly()
    {
        await Assert.That(Smb.ShareRoot(@"\\nas\share\deep\a.mp4")).IsEqualTo(@"\\nas\share");
        await Assert.That(Smb.ShareRoot(@"C:\video\a.mp4")).IsNull();
    }

    [Test]
    public async Task Split_ExposesServerShareRemainder()
    {
        var parts = Smb.Split(@"\\nas\share\deep\a.mp4");
        await Assert.That(parts).IsNotNull();
        await Assert.That(parts!.Value.Server).IsEqualTo("nas");
        await Assert.That(parts.Value.Share).IsEqualTo("share");
        await Assert.That(parts.Value.Remainder).IsEqualTo(@"deep\a.mp4");
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
    public async Task RemoteReadArgs_IsTimeoutOnly()
    {
        List<string> args = Smb.RemoteReadArgs(@"\\nas\share\a.mp4");

        await Assert.That(args).DoesNotContain("-stimeout");
        await Assert.That(args).DoesNotContain("-reconnect");
        await Assert.That(args).DoesNotContain("-reconnect_streamed");
        await Assert.That(args).DoesNotContain("-reconnect_delay_max");
        await Assert.That(args).IsEquivalentTo(new[] { "-rw_timeout", "30000000" });
    }

    /// <summary>
    /// Read, write and probe must agree: any option that one side can carry and
    /// another cannot is exactly how the reconnect bug got in.
    /// </summary>
    [Test]
    [Arguments(@"\\nas\share\a.mp4")]
    [Arguments("smb://nas/share/a.mp4")]
    public async Task RemoteArgs_AreIdenticalForReadWriteAndProbe(string path)
    {
        List<string> read = Smb.RemoteReadArgs(path);
        List<string> write = Smb.RemoteWriteArgs(path);
        List<string> probe = Smb.RemoteProbeArgs(path);

        await Assert.That(read).IsEquivalentTo(write);
        await Assert.That(read).IsEquivalentTo(probe);
    }

    [Test]
    public async Task RemoteReadArgs_LocalFileGetsNothing() =>
        await Assert.That(Smb.RemoteReadArgs(@"C:\video\a.mp4")).IsEmpty();

    /// <summary>
    /// The <c>-reconnect</c> family is flagged <c>D</c> (decoding only) in
    /// <c>ffmpeg -h full</c>, so it is rejected on the output side.
    /// <c>-rw_timeout</c> is flagged <c>ED</c> and is the only option left.
    /// </summary>
    [Test]
    public async Task RemoteWriteArgs_IsTimeoutOnly()
    {
        List<string> args = Smb.RemoteWriteArgs(@"\\nas\share\out.rm");

        await Assert.That(args).IsEquivalentTo(new[] { "-rw_timeout", "30000000" });
        await Assert.That(Smb.RemoteWriteArgs(@"C:\video\out.rm")).IsEmpty();
    }

    [Test]
    public async Task RemoteProbeArgs_UsesTheOnlyOptionFfprobeKnows() =>
        await Assert.That(Smb.RemoteProbeArgs(@"\\nas\share\a.mp4")).IsEquivalentTo(new[] { "-rw_timeout", "30000000" });

    [Test]
    public async Task Describe_ReportsSomethingForUncAndNothingForLocal()
    {
        await Assert.That(Smb.Describe(@"\\nas\share\a.mp4")).IsNotNull();
        await Assert.That(Smb.DescribeOutput(@"\\nas\share\out.rm")).IsNotNull();
        await Assert.That(Smb.Describe(@"C:\video\a.mp4")).IsNull();
    }

    /// <summary>
    /// The probe is UNC-only (a local path is never worth a round-trip), so the
    /// local case is only asserted to be a harmless "yes". What matters is that
    /// it never throws and never leaves a marker behind.
    /// </summary>
    [Test]
    public async Task CanWriteDirectory_ProbesWithoutLeavingAMarkerBehind()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lae-smb-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        await Assert.That(Smb.CanWriteDirectory(dir)).IsTrue();
        await Assert.That(Directory.EnumerateFileSystemEntries(dir)).IsEmpty().Because("the marker file must be deleted again");

        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task CanWriteDirectory_LocalPathIsAlwaysYesWithoutTouchingDisk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lae-never-" + Guid.NewGuid().ToString("N"));

        await Assert.That(Smb.CanWriteDirectory(dir)).IsTrue();
        await Assert.That(Directory.Exists(dir)).IsFalse().Because("a local destination must not be created by a probe");
    }
}
