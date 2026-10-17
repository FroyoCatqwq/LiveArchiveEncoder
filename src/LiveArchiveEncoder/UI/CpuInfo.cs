using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace LiveArchiveEncoder.UI;

/// <summary>
/// Diagnostic: prints what the .NET runtime can actually use on this machine.
///
/// This exists to answer one question honestly: does the C# side of the tool have
/// any vectorisable work? It does not -- the controller only moves raw frame
/// buffers between two FFmpeg pipes -- but <c>Vector&lt;T&gt;.Count</c> is a
/// build-time constant, so it is worth recording that a NativeAOT build defaults
/// to the SSE2 width (16) while the JIT picks up AVX2 (32) at runtime.
/// </summary>
public static class CpuInfo
{
    public static void Print()
    {
        Console.Out.WriteLine($"runtime              : {RuntimeInformation.FrameworkDescription}");
        Console.Out.WriteLine($"arch                 : {RuntimeInformation.ProcessArchitecture}");
        Console.Out.WriteLine($"OS                   : {RuntimeInformation.OSDescription}");
        Console.Out.WriteLine($"processor count      : {Environment.ProcessorCount}");
        Console.Out.WriteLine($"GC server            : {System.Runtime.GCSettings.IsServerGC}");
        Console.Out.WriteLine($"GC latency mode      : {System.Runtime.GCSettings.LatencyMode}");
        Console.Out.WriteLine();

        Console.Out.WriteLine($"Vector<byte>.Count          : {Vector<byte>.Count} bytes");
        Console.Out.WriteLine($"Vector.IsHardwareAccelerated: {Vector.IsHardwareAccelerated}");
        Console.Out.WriteLine($"Sse2.IsSupported            : {Sse2.IsSupported}");
        Console.Out.WriteLine($"Sse42.IsSupported           : {Sse42.IsSupported}");
        Console.Out.WriteLine($"Avx.IsSupported             : {Avx.IsSupported}");
        Console.Out.WriteLine($"Avx2.IsSupported            : {Avx2.IsSupported}");
        Console.Out.WriteLine($"Avx512F.IsSupported         : {Avx512F.IsSupported}");
        Console.Out.WriteLine($"Fma.IsSupported             : {Fma.IsSupported}");
        Console.Out.WriteLine($"Bmi2.IsSupported            : {Bmi2.IsSupported}");
        Console.Out.WriteLine($"Popcnt.IsSupported          : {Popcnt.IsSupported}");
        Console.Out.WriteLine($"Lzcnt.IsSupported           : {Lzcnt.IsSupported}");
        Console.Out.WriteLine($"Aes.IsSupported             : {Aes.IsSupported}");
        Console.Out.WriteLine($"X86Serialize.IsSupported    : {X86Serialize.IsSupported}");
        Console.Out.WriteLine();

        Console.Out.WriteLine("note: the encoding itself runs in FFmpeg child processes, which do");
        Console.Out.WriteLine("their own runtime CPU dispatch. This process never touches pixels.");
    }
}
