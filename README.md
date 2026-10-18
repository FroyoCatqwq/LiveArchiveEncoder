# Live Archive Encoder v7 (.NET 10 / C# 14)

Windows CUI/CLI frontend for classic/legacy FFmpeg video encoders. This is a
behaviour-preserving port of `live_archive_encoder.py` v6.3.3 to C# on .NET 10,
with the internals rewritten for lower allocation and better pipe scheduling.

**The port keeps the realtime simulation semantics of v6.3.3.** The capture
thread feeds frames at the source cadence and the main loop emits one output
slot every `1/fps` seconds. Nothing here removes that pacing, so total wall time
remains tied to media duration.

## Build

```powershell
# JIT (framework-dependent) — the default build
dotnet build src/LiveArchiveEncoder/LiveArchiveEncoder.csproj -c Release
# -> src/LiveArchiveEncoder/bin/Release/net10.0/LiveArchiveEncoder.exe

# …or both builds, plus an optional NativeAOT publish
tests\build.ps1 -Aot
# -> publish\jit\LiveArchiveEncoder.exe       (162 KB + shared framework)
# -> publish\aot\LiveArchiveEncoder.exe       (3.8 MB, single file, no deps)
```

Requires the .NET 10 SDK and an FFmpeg/ffprobe pair on `PATH` (or pass
`--ffmpeg` / `--ffprobe`). NativeAOT additionally needs the **Desktop
development with C++** workload: it links with `link.exe` and the Windows SDK
import libraries. If `vcvarsall.bat` cannot locate the SDK on your machine, pass
the directories explicitly:

```powershell
tests\build.ps1 -Aot -WindowsSdkLibVersion 10.0.26100.0
```

## Tests

```powershell
# Unit tests (NUnit 5)
dotnet test tests/LiveArchiveEncoder.Tests/LiveArchiveEncoder.Tests.csproj -c Release

# …or from the solution (same thing, discovers every project)
dotnet test LiveArchiveEncoder.V7.slnx -c Release
```

The NUnit project covers the pure-logic layers that do not need a real encode:
SMB argument construction, the FFmpeg distribution catalogue, toolchain layout,
player argument parsing, CLI parsing, config persistence, and localisation
parity (every key must exist in both `en` and `zh`). It also runs under
NativeAOT-sensitive APIs, so a reflection-only code path shows up as a failure
here rather than as a crash in a published binary.

End-to-end equivalence against the Python reference lives in the Python
harnesses, which invoke a built exe and need FFmpeg on `PATH`:

```powershell
python tests/compare_matrix.py     # 12 encoder/param cases, ffprobe structure
python tests/error_paths.py        # 11 failure paths, exit codes
python tests/feature_sweep.py      # every legacy encoder, 2 s clip
python tests/option_smoke.py       # option plumbing
python tests/cui_smoke.py          # interactive menu assertions
CS_EXE=publish\aot\LiveArchiveEncoder.exe python tests/compare_matrix.py
```

`compare_matrix.py` depends on the realtime frame-selection logic, so run it
alone — a parallel build or another encode will shift the wall clock and change
which frames the adaptive controller picks.

## Usage

```powershell
# CLI: positional input/output plus flags
LiveArchiveEncoder.exe input.mp4 output.rm --encoder rv20 --limit 200 --fps 13

# CUI: no input argument opens the interactive menu
LiveArchiveEncoder.exe

# Show the deinterlacers / scalers exposed by the selected FFmpeg
LiveArchiveEncoder.exe --list-filters

# Print the help text (same exit code and contract as argparse in v6.3.3)
LiveArchiveEncoder.exe --help

# Print what the .NET runtime detects on this CPU (diagnostic, no I/O)
LiveArchiveEncoder.exe --cpu-info
```

All v6.3.3 flags are supported with identical names and defaults:
`--start-time/--start`, `--end-time/--end`, `--encoder`, `--audio-encoder`,
`--limit`, `--recover`, `--window`, `--fps`, `--fps-levels`, `--qv`, `--threads`,
`--buffer-frames`, `--audio-kbps`, `--audio-channels`, `--audio-rate`, `--width`,
`--height`, `--no-crop`, `--scale-algo/--scale-flags`, `--deinterlace`,
`--deint-mode`, `--deint-parity`, `--deint-marked-only`, `--x264-preset`,
`--x264-tune`, `--x264-profile`, `--x264-level`, `--no-cuda`, `--ffmpeg`,
`--ffprobe`, `--list-filters`, `--quiet`, `--no-cui`.

Two flags are additions rather than ports:

| flag | purpose |
| --- | --- |
| `--help` / `-h` | argparse-equivalent usage text; exits 0 without touching FFmpeg |
| `--cpu-info` | prints the instruction sets and `Vector<byte>.Count` the runtime reports |
| `--audio-mode inline\|separate\|auto` | audio routing; `inline` (default) is v6.3.3 behaviour, see below |

### Audio routing

`--audio-mode separate` encodes the video alone while a second FFmpeg process
encodes the audio track, then joins them with `-c copy`. It is complete and
A/V-aligned (both stages seek on the input, so both streams start at PTS 0), but
it is **not** the default and it is **not** faster:

* the video pass is paced against the wall clock, so removing audio does not
  shorten it — the audio pass and the remux are pure added cost (~0.7 s on a
  5 s segment);
* the parallel audio process competes with the capture thread, which shifts the
  adaptive controller's frame selection (mpeg1video: 56 fresh slots inline vs
  32 separate);
* the extra remux rewrites container duration metadata even when the frames are
  identical (wmv2/ASF: 5.046 s inline vs 5.015 s separate).

`--audio-mode auto` behaves like `separate` but falls back to the single-process
pipeline when the muxer rejects the split. Measured numbers are in
`tests/TEST_REPORT_v7.md` §7.

## Layout

```
src/LiveArchiveEncoder/
  Configuration/  Config, EncoderInfo, EncoderCaps, StreamInfo, constant tables
  Core/           timecode parsing, pixel-format maths, validation, high-res timer
  FFmpeg/         discovery, capability probing, ffprobe, process + pipe I/O
  Frames/         bounded pre-allocated frame pool
  RateControl/    rolling bitrate monitor fed by `ffmpeg -progress`
  Encoding/       argument builders, preflight, the realtime encode loop, audio routing
  UI/             CLI parser + help, CUI menus, shared reporting, --cpu-info
tests/
  compare_matrix.py   functional equivalence harness (12 encoder/parameter cases)
  error_paths.py      error-path equivalence harness (11 cases)
  benchmark.ps1       controller CPU / memory measurement
  ab_duration.py      isolates inline vs separate for container duration
  build.ps1           JIT and/or NativeAOT build helper
  media/test_src.mp4  generated 20 s 320x240 h264+aac fixture
```

## What was actually changed versus v6.3.3

| Area | Python v6.3.3 | C# v7 |
| --- | --- | --- |
| Frame storage | `deque(maxlen=N)` of freshly allocated `bytes` | pre-allocated pool of `capacity + 2` buffers, ownership transferred instead of copied; zero per-frame allocation |
| Pipe reads | 3 blocking threads | 3 async readers over raw byte buffers, no per-line `string` until a key matches |
| Slot timing | `time.sleep` (~15.6 ms granularity) | `timeBeginPeriod(1)` + sleep to an absolute deadline (~1 ms granularity) |
| Progress parsing | `str.split` per line | span-based parse, no intermediate strings |
| Cancellation | `KeyboardInterrupt` | `CancellationToken` propagated to the loop; child processes killed in `finally` |
| Result | dict | typed `EncodeResult` |

Deliberately **not** changed: the FFmpeg command lines (including the position of
`-threads`), the adaptive FPS state machine, frame-selection rules, rolling
bitrate maths, preflight steps, and every default value.

## Known constraints

* **Wall time is bounded by media duration.** This is the realtime simulation
  design, not a performance defect. Making a one-hour source finish in minutes
  requires dropping the wall-clock pacing, which changes frame selection and
  bitrate-control outcomes and must be re-validated.
* **`-threads` is passed before the raw-video input**, exactly as in v6.3.3.
  FFmpeg scoping means it may not reach the output video encoder as a user would
  expect; it was left alone to preserve behaviour, and should be benchmarked
  per-encoder before changing.
* **SIMD is already fully used and cannot be switched on further.** Encoding
  happens in the FFmpeg child processes, which dispatch on the CPU at runtime.
  Forcing `-cpuflags +avx2` is identical to the default; only *disabling* SIMD
  (`-cpuflags 0`) is slower. There is no vectorisable work in the C# controller —
  it never touches pixels. The one real knob is `-p:IlcInstructionSet=avx2` for
  the AOT build, because NativeAOT compiles for the SSE2 baseline by default
  (`Vector<byte>.Count` 16 vs 32 under the JIT); it makes the exe CPU-specific.
* **AOT is about packaging, not speed.** It yields a single self-contained
  executable and a faster process start; the encode is unchanged because it runs
  in FFmpeg and is paced against the wall clock.

See `tests/TEST_REPORT_v7.md` for the measured comparison against v6.3.3.
