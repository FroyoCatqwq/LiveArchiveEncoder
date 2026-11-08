# System and hardware requirements

[English](requirements.en.md) · [中文](requirements.md)

One sentence: **the program is tiny; the bar is FFmpeg.**

LiveArchiveEncoder never touches pixels: decoding and encoding happen in FFmpeg
child processes. The tool throttles frame slots, controls bitrate, shards and
joins. Measured on a 60 fps / 640x480 / 720-slot run, the orchestrating process
used **1.5% of one core** (0.22 s out of 14.08 s wall clock). So "will it run"
depends on the CPU and memory FFmpeg needs, not on this 8 MB program.

---

## 1. Operating systems

| Platform | Status | Notes |
| --- | --- | --- |
| Windows 10 1809+ / Windows Server 2019+ / Windows 11 | **Supported**, x64 | Ships `win-x64` (JIT) and `win-x64-aot` (single file) |
| Linux x86_64 | **Supported**, glibc ≥ **2.28** | The JIT build runs on every distribution .NET 10 supports (AlmaLinux 8 / RHEL 8 onwards); the AOT build's floor equals the build machine's |
| Linux ARM64 / macOS | No published builds | The source compiles, but nothing is verified and nothing is promised |
| 32-bit Windows | Not supported | x64 only |

> **v7.9.6.3 lowered the floor from "Windows 11 / glibc 2.35" to what .NET 10
> itself supports**: the floor is no longer decided by a developer machine but by
> the test machines — Windows Server 2019 (10.0.17763) and AlmaLinux 8.10
> (glibc 2.28). Both ran the full unit suite.

The two Linux packages differ:

- `linux-x64-aot.tar.gz`: native single file, **no .NET runtime needed**, but it
  links glibc dynamically, so the target must have glibc ≥ the **build** machine's
  version (AlmaLinux 8.10 / glibc 2.28 since v7.9.6.3). Older distributions use JIT.
- `linux-x64.tar.gz`: ordinary JIT build; the target needs the **.NET 10 runtime**
  (or 11 — `RollForward=Major` lets one package serve both). The glibc floor comes
  from .NET (2.28 onwards).

Alpine / musl are **not supported**: there is no musl AOT build.

The runtime self-checks both limits (`Core/EnvCheck`): Windows compares the build
number, Linux reads `gnu_get_libc_version`. Below the floor it only **warns** —
a slow machine still produces correct files.

## 2. CPU

| Item | Minimum | Recommended |
| --- | --- | --- |
| Architecture | x86-64 (SSE4.2) | Same, AVX2 or better |
| Cores | **2** | 4–8 |

Why 2: under `--parallel` every shard is a separate FFmpeg process, so on one core
the shards only fight each other and the tool falls back to a single shard on a
single-core or network-share input. "Physical cores − 1" is usually the sweet spot,
leaving one core for orchestration and I/O.

FFmpeg's own encoders have requirements too: `libx264` at 640x480@25 is fine on
low-end CPUs; 1280x720 or `libx265` / AV1 wants four cores or more, otherwise the
realtime pacing cannot keep up.

## 3. Memory

| Item | Minimum | Recommended |
| --- | --- | --- |
| Available RAM | **2 GB** | 8 GB |

One 1080p `yuv420p` frame is about 3.1 MB; the tool keeps `--buffer-frames`
(default 6) capture buffers, multiplied by the shard count. 4 shards × 1080p ×
6 frames ≈ 75 MB of raw frames, plus FFmpeg's own reference frames and encoder
buffers. A gigabyte of headroom is plenty. The real consumers are encoders such as
x265 / AV1, whose lookahead can reach hundreds of MB.

## 4. Disk

| Item | Size |
| --- | --- |
| Program (JIT) | ≈ 0.25 MB archive, ≈ 0.6 MB unpacked |
| Program (AOT single file) | ≈ 3.8 MB |
| FFmpeg build (full GPL) | ≈ **161 MB** (BtbN nightly, all codecs) |
| Output space | At least **3× the output size** |

Why 3×: parallel mode writes each shard as `<name>.sNNN.mkv`, joins them into
`joined.mkv`, then remuxes into the target container — so shards, the join
intermediate and the product coexist at peak. Enabling the recompress stage adds
one more product-sized file. `LAE_KEEP_SHARDS=1` keeps the shard directory; delete
it by hand when debugging.

Network shares (UNC / mapped letters / CIFS) are read streaming, but **do not put
the output on a network share**: shard writes plus remux mean several large
rewrites, which SMB makes much slower.

## 5. Display and terminal

- The interface is plain text and needs any terminal that can read stdin: Windows
  Terminal / cmd / PowerShell, or any xterm-compatible terminal on Linux.
- The Chinese UI is padded by **display width** (full-width characters count as
  two columns); a window of 80 columns or more is recommended.
- No GUI is needed; only `--play` via `ffplay` or a file association needs one.

## 6. Fonts (burned-in subtitles)

Burned-in subtitles go through libass, and fonts are taken **only from the tool's
own `fonts` directory** (next to `ffmpeg/`):

| Platform | Directory |
| --- | --- |
| Windows | `%LOCALAPPDATA%\LiveArchiveEncoder\fonts\` |
| Linux | `~/.local/share/LiveArchiveEncoder/fonts/` |
| Override | `LAE_FONT_DIR` |

- Chinese subtitles need a Chinese font (`msyh.ttc` ≈ 19 MB, `simhei.ttf` ≈ 9.7 MB,
  `simsun.ttc` ≈ 18 MB).
- Omitting one does not crash: libass falls back to system fonts, which shows up as
  **tofu boxes** for Chinese text. `--list-subtitle-fonts --subtitle x.ass` reports
  which families an ASS asks for and whether this machine has them.
- Import with `--font-import <file-or-directory>`, or menu `17 → 5`.

## 7. Permissions and directories

The tool writes:

| Purpose | Location |
| --- | --- |
| Settings | `%LOCALAPPDATA%\LiveArchiveEncoder\config.json` (`LAE_CONFIG_DIR` overrides) |
| Capability cache | `<settings dir>/cache/caps-<hash>.json` (safe to delete) |
| Named profiles | `<settings dir>/configs/*.json` |
| Managed FFmpeg | `%LOCALAPPDATA%\LiveArchiveEncoder\ffmpeg\<source>\` |
| Fonts | Same root, `fonts\` |
| Output and shards | The output file's directory |

No administrator rights are required (unless you install FFmpeg into a system
directory yourself).

## 8. Network

| Scenario | Requirement |
| --- | --- |
| HTTP(S) input | The FFmpeg build needs the `http` protocol (standard BtbN/Gyan builds have it) |
| SMB / UNC / mapped drives | Mounted by the OS; the tool only detects the path and adds `-rw_timeout 30000000` to reads, writes and probes |
| Downloading FFmpeg | Access to GitHub / gyan.dev / SourceForge; `--proxy` sets a proxy |
| NNEDI3 weights (`--nnedi-update`) | GitHub only (`sekrit-twc/znedi3`), about 13.5 MB. Measured from mainland China: `raw.githubusercontent.com` connects but stalls; use `--gh-proxy` or let the tool pick a route |
| GitHub download front (`--gh-proxy`) | Must reach the front itself (default `gh-proxy.com`); it exists precisely to work without a proxy |
| Self-update (`--update` / startup check) | Needs `api.github.com` releases plus the asset host; honours `--proxy` and `--gh-proxy`. The startup check has a 3 s budget and a 24 h cache, and fails silently. If the release page answers 401/403/404, set `LAE_UPDATE_TOKEN` (environment only, never written to disk) |

Whether a network source is sharded is decided by `NetworkProbe` measuring the
link (since v7.9.6.3): N shards each opening the same shared connection is the
most fragile shape in this project, so a slow link still falls back to a single pass.

## 9. Measured environments

| Machine | Configuration | Result |
| --- | --- | --- |
| Windows 11 | 16 threads | 654 unit tests × {net10.0, net11.0}; full e2e |
| Ubuntu 22.04.5 (cia1) | **2 cores**, glibc 2.35 | 653 unit tests (the mapped-drive case is Windows-only) × both TFMs; full e2e, two phases 321 s + 209 s |

Two cores run the entire suite, including 4-shard encoding — so "minimum" is not a
paper number.

## 10. Performance expectations (same 20 s source)

| Approach | Wall clock |
| --- | --- |
| Single pass, realtime pacing (default, simulates a live cadence) | ≈ 21 s (source length + 1.2 s fixed overhead) |
| 4 shards, 2 cores | ≈ 1/3 of the source length |
| Plain `ffmpeg` one-shot transcode (no pacing) | **0.489 s** |

The first two rows are the design: this tool simulates a machine of the period
encoding while recording. If you want the fastest possible file, use
`--recompress-only` (pure transcode, no pacing) or `--no-realtime` (same pipeline,
no waiting). Realtime pacing is the core semantic of this tool, not an optional switch.

Fixed overhead ≈ 2 s: .NET startup, one ffprobe, one preflight. Since v7.9.5 the
capability probe is cached on disk per FFmpeg binary, so warm starts spawn no
process for it.

## 11. Unsupported setups

- 32-bit processes, ARM, musl, macOS packages;
- writing the output into a read-only or system directory (it reports an error, it
  never silently drops the file);
- `--parallel` reading a network share (forced to 1 with an explanation);
- producing deliberate damage — corruption, glitches, mosaic. This tool only
  encodes; those effects belong to a separate project, and `--help`, the report and
  the menus no longer claim a local script does it.
