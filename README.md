# Live Archive Encoder v7 (.NET 11 / C# 15)

CUI/CLI frontend for classic/legacy FFmpeg video encoders. Started as a
behaviour-preserving port of `live_archive_encoder.py` v6.3.3 (kept verbatim
under `reference/`) to C# on .NET 11, with the internals rewritten for lower
allocation and better pipe scheduling. **As of v7.6 it grows past the port**:
post-processing stages (text watermark, a "video site" recompress pass) and
per-era presets sit on top of the realtime encoder, all of them off by default —
with no flags given, the output is still exactly what v6.3.3 would have produced.

Full user guide: [`docs/usage.md`](docs/usage.md); where the era-preset numbers
come from: [`docs/era-presets.md`](docs/era-presets.md).

**The port keeps the realtime simulation semantics of v6.3.3.** The capture
thread feeds frames at the source cadence and the main loop emits one output
slot every `1/fps` seconds. Nothing here removes that pacing, so total wall time
remains tied to media duration.

## Platforms

| | Windows | Linux |
| --- | --- | --- |
| Status | primary | supported since v7.5 |
| Build | `tools\build.ps1` (JIT / AOT / AOT-AVX2) | `tools/build.sh` (JIT, `--aot` optional) |
| NativeAOT | yes — needs MSVC + Windows SDK | experimental — needs `clang` + zlib headers |
| SMB | UNC paths (`\\host\share`) | CIFS mount points; needs `cifs-utils` |
| Player | `ffplay` or shell file association | `ffplay` or `xdg-open` |
| Managed FFmpeg | yes | yes (`.tar.xz`, needs `xz-utils`) |

Platform-specific bits a script has to know:

* The executable is `LiveArchiveEncoder.exe` on Windows and extension-less
  `LiveArchiveEncoder` elsewhere. The Python harnesses handle this via `EXE_NAME`.
* Managed builds live under `%LOCALAPPDATA%\LiveArchiveEncoder\ffmpeg\<id>\bin`
  on Windows and `~/.local/share/LiveArchiveEncoder/ffmpeg/<id>/bin` elsewhere.
* The FFmpeg catalogue is filtered per platform — `gyan.dev` ships no Linux
  build, so Linux menus and completions never offer it.

## Build

Windows (PowerShell):

```powershell
dotnet build src/LiveArchiveEncoder/LiveArchiveEncoder.csproj -c Release
# -> src\LiveArchiveEncoder\bin\Release\net11.0\LiveArchiveEncoder.exe

tools\build.ps1 -Aot
# -> publish\jit\LiveArchiveEncoder.exe       (162 KB + shared framework)
# -> publish\aot\LiveArchiveEncoder.exe       (3.8 MB, single file, no deps)
```

Linux / macOS:

```bash
tools/build.sh
# -> publish/jit/LiveArchiveEncoder

# NativeAOT needs clang and zlib headers first:
#   sudo apt install clang zlib1g-dev     (Debian/Ubuntu)
#   sudo dnf install clang zlib-devel     (Fedora)
tools/build.sh --aot
```

`DOTNET` overrides the `dotnet` executable, which matters when the SDK comes from
a shim manager (`mise`, `asdf`) that a non-interactive shell never puts on `PATH`:

```bash
DOTNET="$HOME/dot" tools/build.sh
```

Requires the .NET 11 SDK (RC2 is enough; `winget install Microsoft.DotNet.SDK.Preview`
on Windows, `mise use -g dotnet@11.0.100-rc.2` on Linux). Windows NativeAOT additionally needs the **Desktop
development with C++** workload: it links with `link.exe` and the Windows SDK
import libraries. If `vcvarsall.bat` cannot locate the SDK on your machine, pass
the directories explicitly:

```powershell
tools\build.ps1 -Aot -WindowsSdkLibVersion 10.0.26100.0
```

At runtime you need an FFmpeg/ffprobe pair on `PATH` (or pass `--ffmpeg` /
`--ffprobe`), or let the tool download and manage one itself (`--ffmpeg-download`).

## Tests

```bash
# NUnit 5 unit tests — identical command on both platforms
dotnet test LiveArchiveEncoder.V7.slnx -c Release      # 236 on Windows, 234 on Linux
```

The NUnit project covers the pure-logic layers that do not need a real encode:
SMB argument construction, the FFmpeg distribution catalogue, toolchain layout,
player argument parsing, CLI parsing, config persistence, hwaccel normalisation,
and localisation parity (every key must exist in both `en` and `zh`). Since v7.6
it also covers the post-processing layer: the recompress fragment tokenizer and
its rejections, the `drawtext` filter expression, era-preset consistency, and a
version-1 settings file still loading with both new stages off. It also runs under
NativeAOT-sensitive APIs, so a reflection-only code path shows up as a failure
here rather than as a crash in a published binary.

End-to-end equivalence against the Python reference lives in the Python
harnesses, which invoke a built exe and need FFmpeg on `PATH` (the Python
baseline uses the system FFmpeg too, so on a box that only has a managed build,
put that build's `bin` on `PATH` first):

```bash
python3 tests/e2e/compare_matrix.py     # 12 encoder/param cases, ffprobe structure
python3 tests/e2e/error_paths.py        # 11 failure paths, exit codes
python3 tests/e2e/feature_sweep.py      # every legacy encoder, 2 s clip
python3 tests/e2e/option_smoke.py       # option plumbing, incl. the v7.6 stages
python3 tests/e2e/cui_smoke.py          # interactive menu assertions
CS_EXE=publish/aot/LiveArchiveEncoder python3 tests/e2e/compare_matrix.py
```

`option_smoke.py` is the one that exercises the post-processing chain: it encodes
with a watermark, with both stages armed, and with an era preset, then probes the
`_wm` and `_web` products as well as the live file. Two of its cases need a font
in the `fonts/` directory.

`compare_matrix.py` compares against v6.3.3 and is kept as a regression check, but
the port no longer has to match the Python output bit for bit -- v7.6 features are
all opt-in, so with no flags the two still agree.

Every harness resolves the executable through `EXE_NAME`, so the same file runs
unchanged on Windows (`.exe`) and Linux (no extension).

All scratch products land in `tests/out/` (gitignored); `CM_OUT` / `FS_OUT`
move them if you want to keep two builds side by side.

`compare_matrix.py` depends on the realtime frame-selection logic, so run it
alone — a parallel build or another encode will shift the wall clock and change
which frames the adaptive controller picks.

## Continuous integration

| host | config | what it does |
| --- | --- | --- |
| GitHub Actions | `.github/workflows/ci.yml` | restore + build + unit tests on a `windows-latest` / `ubuntu-latest` matrix |
| Codeberg (Woodpecker) | `.woodpecker.yaml` | restore + build + unit tests on Linux — this is the pipeline Codeberg actually hosts |
| Codeberg (Forgejo Actions) | `.forgejo/workflows/ci.yml` | unit tests plus framework-dependent `linux-x64` / `win-x64` publishes uploaded as artifacts |

Codeberg provides hosted Actions only in a limited fashion and recommends
Woodpecker for hosted CI, so `.forgejo/workflows/ci.yml` needs a runner
registered under your account or the repository
(*Settings → Units → Actions*, then *Settings → Actions → Runners → Create new
Runner* with at least the `ubuntu-latest` label). Without one the job simply
stays queued; dispatch it by hand with `fj actions dispatch ci main`.

The Python harnesses run on none of them on purpose — see the note in `## Tests`.

## Usage

```powershell
# CLI: positional input/output plus flags (Windows)
LiveArchiveEncoder.exe input.mp4 output.rm --encoder rv20 --limit 200 --fps 13
```

```bash
# …same thing on Linux, minus the extension
./LiveArchiveEncoder input.mp4 output.rm --encoder rv20 --limit 200 --fps 13

# hardware decode is opt-in and probed before use: any name from
# `ffmpeg -hwaccels` works, "auto" picks one that decodes a frame
./LiveArchiveEncoder input.mp4 out.mp4 --encoder mpeg4 --hwaccel auto
```

No input argument opens the interactive menu. `--list-filters` shows the
deinterlacers/scalers the selected FFmpeg exposes, `--help` prints the
argparse-equivalent usage text, and `--cpu-info` prints the instruction sets and
`Vector<byte>.Count` the runtime reports.

All v6.3.3 flags are supported with identical names and defaults:
`--start-time/--start`, `--end-time/--end`, `--encoder`, `--audio-encoder`,
`--limit`, `--recover`, `--window`, `--fps`, `--fps-levels`, `--qv`, `--threads`,
`--buffer-frames`, `--audio-kbps`, `--audio-channels`, `--audio-rate`, `--width`,
`--height`, `--no-crop`, `--scale-algo/--scale-flags`, `--deinterlace`,
`--deint-mode`, `--deint-parity`, `--deint-marked-only`, `--x264-preset`,
`--x264-tune`, `--x264-profile`, `--x264-level`, `--hwaccel`, `--no-cuda`, `--ffmpeg`,
`--ffprobe`, `--list-filters`, `--quiet`, `--no-cui`.

Two flags are additions rather than ports:

| flag | purpose |
| --- | --- |
| `--help` / `-h` | argparse-equivalent usage text; exits 0 without touching FFmpeg |
| `--cpu-info` | prints the instruction sets and `Vector<byte>.Count` the runtime reports |
| `--audio-mode inline\|separate\|auto` | audio routing; `inline` (default) is v6.3.3 behaviour, see below |
| `--hwaccel NAME` | hardware decode; `off` (default), `auto`, or any method from `ffmpeg -hwaccels`. The method is probed by actually decoding one frame and falls back to software when it fails |
| `--watermark TEXT` | burn a text watermark into the live recording, before the recompress pass. Position/size/colour/font are separate flags; off by default |
| `--recompress`, `--recompress-args ARGS` | re-encode the result the way a video site would. You write the middle of the FFmpeg command (`-vf scale=320:240,fps=15 -c:v libx264 -crf 28 …`); the program owns `-i` and the output path, and the fragment is tokenised and vetted so a stray path cannot overwrite your source. Off by default |
| `--era ID` | apply an era preset: one set of live + recompress settings per era of online video, 1997→2016 — dial-up RealVideo G2, early-ADSL RealMedia, the FLV/Youku/Tudou years, early H.264, 720p. Sources in `docs/era-presets.md` |

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
`docs/test-report-v7.md` §7.

## Layout

```
src/LiveArchiveEncoder/
  Configuration/  Config, EncoderInfo, EncoderCaps, StreamInfo, constant tables
  Core/           timecode parsing, pixel-format maths, validation, high-res timer,
                  Ctrl+C interrupt
  FFmpeg/         discovery, toolchain (multi-distro install/update/delete),
                  capability probing, ffprobe, process + pipe I/O
  Frames/         bounded pre-allocated frame pool
  RateControl/    rolling bitrate monitor fed by `ffmpeg -progress`
  Encoding/       argument builders, preflight, the realtime encode loop, audio
                  routing, optional parallel sharding
  Remote/         SMB/UNC argument construction for read and write
  Localization/   en/zh string tables (every key must exist in both)
  UI/             CLI parser + help, CUI menus, shared reporting, --cpu-info,
                  post-encode playback
  PostProcess/    v7.6: the stages that follow the live encode -- era presets,
                  the recompress argument validator, the drawtext watermark,
                  font resolution, and the chain that orders them
```

Two data directories live next to each other under the user's profile, so both
survive an upgrade and neither is inside the repo:

```
%LOCALAPPDATA%\LiveArchiveEncoder\        ~/.local/share/LiveArchiveEncoder/
  ffmpeg/<id>/bin/                          managed FFmpeg distributions
  fonts/                                    watermark fonts (user-supplied)
  config.json                               persisted settings
```

tests/
  LiveArchiveEncoder.Tests/   NUnit 5 unit tests (no real encode needed)
  e2e/                        Python end-to-end equivalence harnesses
  media/test_src.mp4          generated 20 s 320x240 h264+aac fixture
  out/                        scratch products, gitignored
tools/
  build.ps1                   JIT and/or NativeAOT build helper (Windows)
  build.sh                    the same for Linux/macOS
  benchmark.ps1               controller CPU / memory measurement
reference/
  live_archive_encoder.py     v6.3.3 — the Python baseline the port is measured against
docs/
  usage.md                    full user guide (CLI, CUI, FFmpeg toolchain, playback)
  test-report-v7.md           measured porting results
  runs/                       archived harness output from past verification runs
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
* **Hardware decode is off by default and stays that way until probed.**
  `ffmpeg -hwaccels` lists methods that are merely *compiled in*; on a machine
  with no GPU (or no driver) `--hwaccel cuda` dies at the first frame with
  `Cannot load libcuda.so.1`. `--hwaccel auto` therefore decodes one real frame
  per candidate before committing, and falls back to software when nothing works.
* **Linux NativeAOT is experimental.** The ILCompiler has to link against system
  libraries whose layout differs per distribution; prefer the JIT build there.

See `docs/test-report-v7.md` for the measured comparison against v6.3.3.

## License

GPL-3.0 — see [`LICENSE`](LICENSE). The C# port inherits the licence of
`reference/live_archive_encoder.py`, and the default FFmpeg distribution it
downloads (BtbN) is a GPL build.
