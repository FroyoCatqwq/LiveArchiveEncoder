using System.Diagnostics;

namespace LiveArchiveEncoder.FFmpeg;

/// <summary>
/// Hardware-assisted decoding.
///
/// Policy, decided in v7.5: <b>off by default</b>. Turning it on requires an
/// actual driver and an actual device, and the only way to know that is to try
/// to decode a frame -- <c>ffmpeg -hwaccels</c> merely lists what the binary was
/// compiled against, which is why a CUDA-enabled build on a machine with no
/// NVIDIA driver still lists <c>cuda</c> and then dies with
/// "Cannot load libcuda.so.1". Every VM this project is tested on is
/// headless, so the default has to be the software path.
///
/// The method is a free-form name rather than a boolean because CUDA is not the
/// only answer: VA-API on Linux, D3D11VA/DXVA2 on Windows, QSV on Intel,
/// VideoToolbox on macOS. Whatever <c>ffmpeg -hwaccels</c> reports can be asked
/// for by name, and <c>auto</c> picks the first one that really works.
/// </summary>
public static class Hwaccel
{
    public const string Off = "off";
    public const string Auto = "auto";

    /// <summary>Methods the binary was compiled with. Never throws.</summary>
    public static List<string> CompiledIn(string ffmpeg)
    {
        var methods = new List<string>();
        try
        {
            CaptureResult p = ProcessRunner.Run(ffmpeg, "-hide_banner", "-hwaccels");
            bool listing = false;
            foreach (string raw in (p.Stdout + "\n" + p.Stderr).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("Hardware acceleration methods:", StringComparison.Ordinal))
                {
                    listing = true;
                    continue;
                }
                if (!listing) continue;
                // The trailer is a sentence ("Hardware acceleration is ..."), not
                // a method name; anything with a space is not a method.
                if (line.Contains(' ')) continue;
                methods.Add(line);
            }
        }
        catch (Exception)
        {
            return methods;
        }
        return methods;
    }

    /// <summary>
    /// What <c>auto</c> tries, in order. Most specific to the platform first:
    /// cuda on Windows boxes that have an NVIDIA card, vaapi on Linux, and the
    /// vendor-agnostic fallbacks last.
    /// </summary>
    public static IReadOnlyList<string> PreferenceOrder =>
        OperatingSystem.IsWindows()
            ? ["cuda", "d3d11va", "dxva2", "qsv", "vulkan", "opencl"]
            : ["vaapi", "cuda", "qsv", "vdpau", "vulkan", "opencl"];

    /// <summary>
    /// The only test that means anything: decode one frame through the method.
    /// Returns null when it works, otherwise the reason.
    ///
    /// The source is a string because it may be an HTTP(S) URL (v7.9).
    /// </summary>
    public static string? Probe(string ffmpeg, string method, string sourcePath)
    {
        try
        {
            CaptureResult p = ProcessRunner.Run(
            [
                ffmpeg, "-hide_banner", "-loglevel", "error",
                .. Remote.HttpSource.ReadArgs(sourcePath),
                // v7.9.3.1: the probe reads the source like any other pass, so a
                // share gets its rw_timeout here too -- without it a stalled CIFS
                // mount turns a one-frame trial into a 120 s timeout, which reads
                // as "this hwaccel method does not work" for the wrong reason.
                .. Remote.Smb.RemoteReadArgs(sourcePath),
                "-hwaccel", method,
                "-i", sourcePath,
                "-frames:v", "1",
                "-f", "null", "-",
            ], 120);

            if (p.ExitCode == 0) return null;

            string detail = p.Stderr.Trim();
            return detail.Length == 0 ? $"exited with {p.ExitCode}" : FirstLines(detail, 2);
        }
        catch (Exception exc)
        {
            return exc.Message;
        }
    }

    /// <summary>
    /// Turn a request ("off" / "auto" / a method name) into a method that is
    /// known to work here, or <see cref="Off"/>.
    /// </summary>
    public static string Resolve(string? requested, string ffmpeg, string sourcePath, Action<string>? log)
    {
        string ask = (requested ?? Off).Trim();
        if (ask.Length == 0 || ask.Equals(Off, StringComparison.OrdinalIgnoreCase)) return Off;

        List<string> compiled = CompiledIn(ffmpeg);

        if (ask.Equals(Auto, StringComparison.OrdinalIgnoreCase))
        {
            foreach (string method in PreferenceOrder.Where(compiled.Contains))
            {
                if (Probe(ffmpeg, method, sourcePath) is null)
                {
                    log?.Invoke($"Hardware decode: {method}");
                    return method;
                }
            }
            log?.Invoke("Hardware decode: none of the compiled-in methods could decode a "
                + "frame on this machine; using software decode");
            return Off;
        }

        if (!compiled.Contains(ask))
        {
            log?.Invoke($"Hardware decode: '{ask}' is not in this FFmpeg build "
                + $"({string.Join(", ", compiled)}); using software decode");
            return Off;
        }

        if (Probe(ffmpeg, ask, sourcePath) is { Length: > 0 } why)
        {
            log?.Invoke($"Hardware decode: '{ask}' is compiled in but cannot decode here "
                + $"({why}); using software decode");
            return Off;
        }

        log?.Invoke($"Hardware decode: {ask}");
        return ask;
    }

    private static string FirstLines(string text, int count)
    {
        string[] lines = text.Split('\n').Select(x => x.Trim())
            .Where(x => x.Length > 0).Take(count).ToArray();
        return string.Join(" | ", lines);
    }
}
