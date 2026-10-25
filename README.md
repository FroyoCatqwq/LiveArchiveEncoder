# Live Archive Encoder v7 (.NET 10 / .NET 11)

CUI/CLI frontend for classic/legacy FFmpeg video encoders. Started as a
behaviour-preserving port of `live_archive_encoder.py` v6.3.3 (kept verbatim
under `reference/`) to C#, with the internals rewritten for lower
allocation and better pipe scheduling. **As of v7.6 it grows past the port**:
a "video site" recompress pass and per-era presets sit on top of the realtime
encoder, all of them off by default — with no flags given, the output is still
exactly what v6.3.3 would have produced.

> **v7.9.1:** the static text watermark stage was retired (code archived under
> `archive/retired/`, so it can come back as a separate project). Subtitle
> burning/copying, added in v7.9, covers the "put text on the picture" job.

Full user guide: [`docs/usage.md`](docs/usage.md); system and hardware requirements:
[`docs/requirements.md`](docs/requirements.md). Version history, from Python
v6.2.2 to the current release: [`docs/CHANGELOG.md`](docs/CHANGELOG.md). Where
the era-preset numbers come from: [`docs/era-presets.md`](docs/era-presets.md).

**The port keeps the realtime simulation semantics of v6.3.3.** The capture
thread feeds frames at the source cadence and the main loop emits one output
slot every `1/fps` seconds. Nothing here removes that pacing, so total wall time
remains tied to media duration.

## Platforms

| | Windows | Linux |
| --- | --- | --- |
| Status | primary | supported since v7.5 |
| Build | `tools\build.ps1` (JIT / AOT) | `tools/build.sh` (JIT, `--aot` optional) |
| NativeAOT | yes — needs MSVC + Windows SDK | experimental — needs `clang` + zlib headers |
| SMB | UNC paths (`\\host\share`) | CIFS mount points; needs `cifs-utils` |
| Player | `ffplay` or shell file association | `ffplay` or `xdg-open` |
| Managed FFmpeg | yes | yes (`.tar.xz`, needs `xz-utils`) |

Platform-specific bits a script has to know:

* The executable is `LiveArchiveEncoder.exe` on Windows and extension-less
  `LiveArchiveEncoder` elsewhere. The pytest suite handles this via `EXE_NAME`.
* Managed builds live under `%LOCALAPPDATA%\LiveArchiveEncoder\ffmpeg\<id>\bin`
  on Windows and `~/.local/share/LiveArchiveEncoder/ffmpeg/<id>/bin` elsewhere.
* The FFmpeg catalogue is filtered per platform — `gyan.dev` ships no Linux
  build, so Linux menus and completions never offer it.

## Build

Windows (PowerShell):

```powershell
dotnet build src/LiveArchiveEncoder/LiveArchiveEncoder.csproj -c Release
# -> src\LiveArchiveEncoder\bin\Release\net10.0\LiveArchiveEncoder.exe

tools\build.ps1 -Aot
# -> publish\jit\LiveArchiveEncoder.exe       (~230 KB + shared framework)
# -> publish\aot\LiveArchiveEncoder.exe       (8.5 MB, single file, no deps)
```

**Two flavours, two runtimes.** Only JIT and AOT are built. The JIT build targets
`net10.0` and is published with `RollForward=Major`, so the same archive starts
on a machine with .NET 10 *and* on one that only has .NET 11. To compile against
.NET 11 explicitly:

```bash
tools/build.sh --tfm net11.0      # or: dotnet build -p:LaeTfm=net11.0
```

`LaeTfm` is the one switch (see `Directory.Build.props`); CI runs the suite under
both frameworks, so "works on 10 and 11" is verified rather than assumed.
NativeAOT output is self-contained and unaffected by either choice.

v7.8.1 also stopped shipping a third flavour: an AOT build with
`-p:IlcInstructionSet=avx2`. Raising the instruction-set baseline only affects
the C# side of this tool, while the encoding happens in FFmpeg, which
runtime-dispatches its own SIMD — measured difference nil, and the result was an
exe that only runs on AVX2 CPUs.

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

Requires the .NET 10 or .NET 11 SDK (`winget install Microsoft.DotNet.SDK.Preview`
on Windows, `mise use -g dotnet@11.0.100-rc.2` on Linux). Windows NativeAOT additionally needs the **Desktop
development with C++** workload: it links with `link.exe` and the Windows SDK
import libraries. If `vcvarsall.bat` cannot locate the SDK on your machine, pass
the directories explicitly:

```powershell
tools\build.ps1 -Aot -WindowsSdkLibVersion 10.0.26100.0
```

At runtime you need an FFmpeg/ffprobe pair on `PATH` (or pass `--ffmpeg` /
`--ffprobe`), or let the tool download and manage one itself (`--ffmpeg-download`).

## Prebuilt binaries

Every release carries four builds, on both hosts:

| file | platform | runtime needed |
| --- | --- | --- |
| `…-win-x64.zip` | Windows | .NET 10 or .NET 11 (`RollForward=Major`) |
| `…-win-x64-aot.zip` | Windows | none (NativeAOT, single exe) |
| `…-linux-x64.tar.gz` | Linux | .NET 10 or .NET 11 |
| `…-linux-x64-aot.tar.gz` | Linux | none (NativeAOT, single file) |

The Linux archives keep the executable bit, so `tar xzf … && ./LiveArchiveEncoder`
works as-is. The AOT binaries link against the build host's glibc — older
distributions should take the JIT build. FFmpeg is still needed either way.

## Tests

```bash
# TUnit unit tests — identical command on both platforms (runs on MTP since v7.8.3)
dotnet test --solution LiveArchiveEncoder.V7.slnx -c Release      # 408 on Windows
# and the same suite against the other runtime:
dotnet test --solution LiveArchiveEncoder.V7.slnx -c Release -p:LaeTfm=net11.0
```

The unit test project (TUnit since v7.8.3, NUnit before) covers the pure-logic
layers that do not need a real encode:
SMB argument construction, the FFmpeg distribution catalogue, toolchain layout,
player argument parsing, CLI parsing, config persistence, hwaccel normalisation,
and localisation parity (every key must exist in both `en` and `zh`). Since v7.6
it also covers the post-processing layer: the recompress fragment tokenizer and
its rejections, era-preset consistency, the settings↔command-line converter, and
a version-1 settings file still loading with the stage off. It also runs under
NativeAOT-sensitive APIs, so a reflection-only code path shows up as a failure
here rather than as a crash in a published binary.

End-to-end equivalence against the Python reference lives in the pytest suite
(`tests/e2e/test_*.py`, since v7.8.3; six standalone scripts before), which
invoke a built exe and need FFmpeg on `PATH` (the Python
baseline uses the system FFmpeg too, so on a box that only has a managed build,
put that build's `bin` on `PATH` first):

```bash
pip install -r tests/e2e/requirements.txt   # pytest==9.1.1 + pytest-xdist
python tools/e2e.py                          # two-phase run (see below)
pytest                                       # same 162 cases, one at a time
pytest tests/e2e/test_compare_matrix.py -k wmv2   # a single case
CS_EXE=publish/aot/LiveArchiveEncoder pytest      # point at another build
```

| module | cases | what it checks |
| --- | --- | --- |
| `test_compare_matrix.py` | 12 | encoder/param matrix, ffprobe structure vs v6.3.3 |
| `test_error_paths.py` | 11 | failure paths, exit codes vs the baseline |
| `test_feature_sweep.py` | 35 | every legacy encoder, 2 s clip (2 skipped: no demuxer) |
| `test_option_smoke.py` | 63 | option plumbing, incl. the recompress stage and era presets |
| `test_cui_smoke.py` | 12 | interactive menu assertions (40+ checks, one extra on Linux) |
| `test_stream_selection.py` | 9 | stream picks by absolute index, plus time-range clipping |
| `test_subtitles.py` | 7 | burn / copy / off, sidecar and embedded, bad requests |
| `test_http_source.py` | 7 | `http(s)://` inputs, timeout and failure behaviour |
| `test_config_conversion.py` | 4 | settings file ↔ command line round trip |
| `test_ab_duration.py` | 2 | duration medians vs the baseline (mpeg1video / wmv2) |

`test_option_smoke.py` is the one that exercises the post-processing chain: it
encodes with an era preset and with the recompress stage armed, then probes the
`_web` product as well as the live file. The `era_flv` case, like the subtitle
module, needs a font in the `fonts/` directory.

`test_compare_matrix.py` compares against v6.3.3 and is kept as a regression check, but
the port no longer has to match the Python output bit for bit -- v7.6 features are
all opt-in, so with no flags the two still agree.

Every module resolves the executable through `EXE_NAME`, so the same files run
unchanged on Windows (`.exe`) and Linux (no extension).

All scratch products land in `tests/out/` (gitignored); `CM_OUT` / `FS_OUT`
move them if you want to keep two builds side by side.

`test_compare_matrix.py` and `test_ab_duration.py` depend on the realtime
frame-selection logic, so they measure a quiet machine — a parallel build or
another encode shifts the wall clock and changes which frames the adaptive
controller picks. `test_cui_smoke.py` is exclusive for a different reason: every
run writes the real settings file it asserts on.

Those two groups are marked (`timing`, `shared_config`) and `tools/e2e.py` runs
them alone first, then everything else under xdist:

```
python tools/e2e.py                  # 794 s serial -> 492 s on a 32-core box
python tools/e2e.py --phase serial   # only the 25 exclusive cases
python tools/e2e.py -k era_flv       # extra args go to pytest
```

A blanket `pytest -n` is refused by a guard in `conftest.py` rather than
silently turning the timing assertions flaky.

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

v7.7 registered a self-hosted one: `forgejo-runner` v13.2.0 in **host mode**
(label `ubuntu-latest:host`, so no container runtime is needed) with a systemd
unit and the mise SDK on `PATH`. See `tools/forgejo-runner.service` and
`tools/forgejo-runner-config.yml` — the only machine-specific parts are the home
directory in the env block and the `--uuid` / token file.

The e2e pytest suite runs on none of them on purpose — see the note in `## Tests`.

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
| `--recompress`, `--recompress-args ARGS` | re-encode the result the way a video site would. You write the middle of the FFmpeg command (`-vf scale=320:240,fps=15 -c:v libx264 -crf 28 …`); the program owns `-i` and the output path, and the fragment is tokenised and vetted so a stray path cannot overwrite your source. Off by default |
| `--recompress-codec NAME` | pick the second pass's codec by name instead of writing a fragment — `libx264`, `libx265`, `libvpx-vp9`, `libvpx`, `libaom-av1`, `libsvtav1`, `flv`, `mpeg4`, `mpeg2video`, `mjpeg`, `wmv2`, `h263`. The container follows the codec (VP9 → `.webm`, x265 → `.mp4`), and a pair the muxer cannot hold is refused before anything runs |
| `--recompress-quality N` | the codec's own knob: `-crf` for x264/x265/VP9/AV1, `-q:v` for the MPEG family |
| `--recompress-audio-codec NAME` | `aac`, `libmp3lame`, `libopus`, `libvorbis`, `ac3`, `flac` or `copy`, with the same container check |
| `--subtitle-font FAMILY`, `--subtitle-font-dir DIR` | force a font family over the one an ASS file names (also gives SRT/VTT one, which they never specify), and add an extra font directory |
| `--list-subtitle-fonts`, `--font-import PATH` | report the fonts a subtitle file asks for and whether this machine has them; copy font files into the program's own `fonts` directory |
| `--recompress-only` | run that pass on an existing file and stop — no live encode, no encoder discovery (`LiveArchiveEncoder clip.mkv --recompress-only --era youku07`) |
| `--era ID` | apply an era/site preset: one set of live + recompress settings per era of online video, 1997→2016 — dial-up RealVideo G2, early-ADSL RealMedia, the FLV years, early H.264, 720p. 18 **site** presets (`sina06`, `sohu06`, `qq06`, `tudou06`, `youku07`, `ku6_07`, `56_07`, `cctv06`, `acfun08`, `bilibili09`, `youtube07`, `bilibili11`, `youku11`, `iqiyi11`, `cctv12`, `youtube11`, `bilibili14`, `youtube15`) carry the codec that site served — Sorenson Spark in FLV, H.264 in MP4, VP9 in WebM — and use it for both passes unless you name an encoder. Since v7.9.6.2 a site preset fills the capture side as well (frame size, rate, ladder, ceiling, audio), so `--era youku07` records 352x264@15 at 320 kbps instead of transcoding a 2016 master with 2007 settings. Your own presets go in the settings file (`CustomEras`) and can override a built-in id. Sources in `docs/era-presets.md` |
| era ladders (v7.9.6.1) | each era preset also carries the adaptive frame-rate ladder of its period (`mp4_10`: 25→20→15→10, `hd14`: 30→25→20→15), so a 25 fps preset no longer drops onto the tool's 13/8/5/3 default — an explicit `--fps` or `--fps-levels` still wins |
| `--era-modern` / `--era-native` | render a preset's second pass with libx264 + AAC in MP4 instead of the site's own codec (the default), or say so explicitly |
| `--era-aspect 4:3\|16:9\|source` | v7.9.6.3: capture and deliver a preset in the other frame shape. Keeps the capture width and changes the height (352x264 → 352x198, 1280x720 → 1280x960) and only offers a shape the encoder can actually encode — H.261/H.263 exist at CIF sizes only, AMV wants multiples of 16, everything else wants even sides; an impossible request is refused at parse time (exit 2) instead of failing on the first frame twenty minutes in |
| `--net-test PATH\|URL` | v7.9.6.3: measure the link behind a source — throughput, jitter and first-byte latency, by reading the source the way the shards would — print the verdict `--parallel` would use, and exit (0 = shardable, 1 = not) |
| `--iperf3 HOST[:PORT]` | v7.9.6.3: measure the link with `iperf3 -c HOST -J` as a *reference*. Both ends need iperf3 and an open port, and a public iperf3 server only stands in for the WAN path — it cannot see a NAS's own disk or per-connection limits, so reading the source stays the authoritative measurement |
| `--video-stream N`, `--audio-stream N` | v7.9: pick which track of a multi-stream source (a VOB's second audio language, an MKV's duplicate encode) to encode, by the absolute index ffprobe prints; `--list-streams` lists them. Wrong index or type is refused before the encode, with the inventory |
| `--subtitle FILE`, `--subtitle-stream N` | v7.9: subtitles from a sidecar file (`.ass/.ssa/.srt/.vtt`, repeatable) or an embedded track. `--subtitle-mode burn` (default) renders them into the picture with libass and the tool's own font directory; `--subtitle-mode copy` muxes them as a real subtitle stream where the container has one, with the codec picked from what `ffmpeg -encoders` actually lists. Bitmap subtitles (dvdsub/dvbsub) can be carried but not burned |
| HTTP/HTTPS input | v7.9, initial support: the input may be a progressive `http(s)://` direct link (archive.org `/download/…` style). Carries the `-reconnect` family and a 30 s socket timeout; seeking needs a Range-capable server. No auth/cookies/HLS |
| `--emit-args`, `--emit-args=FILE` | v7.9.1: print the settings as one equivalent command line and exit. The bare form uses this run's resolved settings; `=FILE` reads settings without needing input/output or FFmpeg |
| `--save-config FILE` | v7.9.1: write this run's resolved settings to a settings file and exit **without encoding** — the way to turn a working command line into something the menu restores |
| `--pix-fmt FORMAT` | v7.9.6: write the picture in a chosen pixel format — `pal8` (256-colour indexed), `rgb8` (3:3:2), `rgb555le`/`rgb565le` (16-bit colour), `rgb48le`, `gray`, `yuv420p10le`/`12le`/`16le`, `nv12`, `p010le` … 86 formats, each with a measured frame size. A request is honoured exactly or refused with the encoder's own list; it is never silently downgraded (`docs/usage.md` §7.17) |
| `--list-pix-fmts` | v7.9.6: print the measured format table, with `OK` against the ones the selected encoder declares |
| `--color-range`, `--colorspace`, `--color-primaries`, `--color-trc` | v7.9.6: override the colour tags written onto the output (the picture itself is not converted). libx264 gets them through `-x264-params` as well, because this build ignores the plain flags and writes `unknown` |
| `--dither MODE` | v7.9.6: swscale dither for a low-colour target — `auto` (default), `none`, `bayer`, `ordered`, `random`, `error_diffusion`. Only emitted where the conversion actually loses colour (pal8, 5:5:5, 5:6:5, 3:3:2, 1-bit, 8-bit grey) |

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

## Changes since the port

Version-by-version history moved to **[`docs/CHANGELOG.md`](docs/CHANGELOG.md)** —
it covers Python v6.2.2 through the current release, with what changed, why, and
whether an output changes. This file stays about *how to use* the tool.

The short version of the last few releases: v7.9 added stream selection,
subtitles (burn or carry), HTTP input and removed Turbo; v7.9.1 retired the
watermark stage, added the subtitle menu, forced single-pass on network drives,
and added `--emit-args` / `--save-config`; v7.9.2 fixed a corrupted picture when
parallel shards were joined straight into FLV; v7.9.3 added the equivalent
command line (menu 18) and named settings files (menu 19); v7.9.3.1 is a
full-project review — presets no longer override explicit flags, and a handful
of crash paths are gone; v7.9.4 fixes three encoder defects (msrle's black
picture, AVI being refused for mpeg1video/mpeg2video, and mpeg1video's fps
check trusting a rate list the encoder itself rejects) plus the frame-rate
header of sharded AVI products. v7.9.5 stops trusting `ffmpeg -h encoder`
altogether: the whole legacy set was swept for real containers and real frame
rates (`docs/capability-matrix.md`), six 2000s video sites join the era presets
as bitrate profiles, the site pass can run on an existing file with
`--recompress-only`, and the capability table is cached so a run launches 3
processes instead of 8–10. v7.9.5.1 makes the presets look like the year they
describe: 18 sites, each naming the codec it actually served (Sorenson Spark in
FLV, H.264 in MP4, VP9 in WebM), plus user-written presets in the settings file;
subtitle fonts are read out of the ASS style table, reported, importable and
overridable; the second pass can name any of thirteen video and seven audio
codecs and gets a container that matches; and `docs/requirements.md` documents
what the machine needs. v7.9.6 adds the other half of "what the picture is":
`--pix-fmt` writes any of 86 measured pixel formats (pal8, 16-bit colour, 10/12/16-bit
YUV, grey, alpha variants) and refuses a request it cannot honour instead of
quietly downgrading it; colour tags can be overridden and are verified in the
output rather than assumed from the command line; and a full-project review fixed
seven latent defects — among them a 4:4:0 frame size that had been half wrong
since the port, per-rung bitrate thresholds dropped by the shard path, and the
palette/container rule that only applied when sharding. v7.9.6.3 is the
"first run and network sources" release: a machine with no FFmpeg is now asked
which distribution to install instead of being told to add one to PATH; era
presets can be captured in 4:3 or 16:9 as long as the encoder really encodes
that frame; a network source is measured before it is sharded instead of being
forced to a single pass; four Windows Media era presets cover 2002-2006; and
the documented floor drops to what .NET 10 supports — Windows Server 2019 and
glibc 2.28, i.e. the two machines CI runs on.

## Layout

```
src/LiveArchiveEncoder/
  Configuration/  Config, EncoderInfo, EncoderCaps, StreamInfo, constant tables
  Core/           timecode parsing, pixel-format maths, validation, high-res timer,
                  Ctrl+C interrupt, AudioCatalog (v7.8: per-codec sample-rate and
                  channel limits; v7.8.2: bitrate bands per codec and rate,
                  muxer-level rate limits, container compatibility), TextExtensions
                  (v7.8.1: C# 14 extension members), EnvCheck (v7.9.5.1: the
                  documented minimums, checked on the running machine)
  FFmpeg/         discovery, toolchain (multi-distro install/update/delete),
                  capability probing, ffprobe, process + pipe I/O
  Frames/         bounded pre-allocated frame pool
  RateControl/    rolling bitrate monitor fed by `ffmpeg -progress`
  Encoding/       argument builders, preflight, the realtime encode loop, audio
                  routing, optional parallel sharding, ShardPlanner (v7.7),
                  stream selection and subtitles (v7.9)
  Remote/         SMB/UNC argument construction for read and write,
                  HttpSource (v7.9: http/https input)
  Localization/   en/zh string tables (every key must exist in both)
  UI/             CLI parser + help, CUI menus, shared reporting, --cpu-info,
                  post-encode playback
  PostProcess/    v7.6: the stages that follow the live encode -- era presets
                  (v7.9.5.1: 18 sites that name their own codec, plus user
                  presets), the recompress argument validator and codec catalog
                  (v7.9.5.1: thirteen video / seven audio encoders with their
                  legal containers), subtitle font lookup (v7.9.5.1: ASS style
                  table + font name tables), font resolution, and the chain that
                  orders them (the watermark stage retired in v7.9.1)
```

Two data directories live next to each other under the user's profile, so both
survive an upgrade and neither is inside the repo:

```
%LOCALAPPDATA%\LiveArchiveEncoder\        ~/.local/share/LiveArchiveEncoder/
  ffmpeg/<id>/bin/                          managed FFmpeg distributions
  fonts/                                    fonts for subtitle burning (user-supplied)
  config.json                               persisted settings
```

tests/
  LiveArchiveEncoder.Tests/   TUnit unit tests (no real encode needed)
  e2e/                        pytest end-to-end equivalence suite
  media/test_src.mp4          generated 20 s 320x240 h264+aac fixture
  out/                        scratch products, gitignored
tools/
  build.ps1                   JIT and/or NativeAOT build helper (Windows)
  build.sh                    the same for Linux/macOS
  benchmark.ps1               controller CPU / memory measurement
  forgejo-runner.service      systemd unit for the self-hosted Codeberg runner
  forgejo-runner-config.yml   its config (host mode, mise SDK on PATH)
reference/
  live_archive_encoder.py     v6.3.3 — the Python baseline the port is measured against
docs/
  CHANGELOG.md                version history, Python v6.2.2 -> now
  usage.md                    full user guide (CLI, CUI, FFmpeg toolchain, playback)
  test-report-v7.md           measured porting results
  runs/                       archived harness output from past verification runs
```

## How this differs from v6.3.3

The port is behaviour-equivalent by default: FFmpeg command lines (including the
position of `-threads`), the adaptive FPS state machine, frame-selection rules,
the rolling bitrate maths and every default stay as they were. What changed is
below the waterline — pre-allocated frame buffers instead of a `deque` of fresh
`bytes`, three async pipe readers instead of three blocking threads, a 1 ms
timer instead of a 15.6 ms `sleep`, and typed results instead of dicts.

The full table, plus the six baseline defects this rewrite deliberately fixes
(AMV produced 0 bytes, CUDA on by default, two wrong SMB options, audio limits
checked only for sample rate, fonts never resolved), is in
[`docs/CHANGELOG.md`](docs/CHANGELOG.md) under "v7.0 → v7.2" and "v6.3.3".

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
  it never touches pixels. NativeAOT compiles for the SSE2 baseline by default
  (`Vector<byte>.Count` 16 vs 32 under the JIT) and `-p:IlcInstructionSet=avx2`
  can raise that, but v7.8.1 stopped building that flavour: no measurable gain,
  and an exe that only starts on AVX2 CPUs.
* **AOT is about packaging, not speed.** It yields a single self-contained
  executable and a faster process start; the encode is unchanged because it runs
  in FFmpeg and is paced against the wall clock. ReadyToRun is the flavour that
  would sit between the two; it is not built here because it needs the runtime
  packs of the *exact* release, which is worth revisiting once .NET 12 ships.
* **Retired code is archived, not deleted.** `archive/retired/` keeps members no
  call site reaches any more, each with the version it was retired in and what
  replaced it. Nothing under `archive/` is compiled.
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
