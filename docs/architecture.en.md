# LiveArchiveEncoder v7 — Technical Description

**English** · [中文](architecture.md)

Version anchor: **v7.9.9.4** (2026-11-08).

This document is written for three readers: whoever maintains v7 next, whoever has to
audit why it is shaped the way it is, and whoever will rewrite it as v8 in Rust. It
describes the present only. Usage lives in [`usage.en.md`](usage.en.md), history in
[`CHANGELOG.en.md`](CHANGELOG.en.md), process in [`MAINTENANCE.en.md`](MAINTENANCE.en.md).

> **v8 is decided**: a thorough, in-depth rewrite in Rust. v7.9 therefore enters
> maintenance mode — bugfixes, hotfixes and small features only, no structural work.
> Section 8 states what v8 must inherit, what it must fix, and how to get there.
> Everything else describes v7 as it stands.

Scale (excluding `bin`/`obj`): 83 C# files, ~29,500 lines; 52 unit-test files,
~9,600 lines; 23 e2e Python files, ~4,400 lines; 11 bilingual document pairs. In other
words **the specification corpus is the same order of magnitude as the product code**.
That is the first thing to accept about this project: it is a tool whose value lies in
a dense web of behavioural contracts and measurement records, not merely in a working
encoder front end.

---

## 1. What it is

### 1.1 One sentence

Take a modern video source, re-encode it the way a given site in a given year would
have, and do so **at the source's own wall-clock cadence** instead of as fast as the
machine allows.

### 1.2 Three non-negotiable semantics

1. **Realtime pacing.** The decoder emits a frame every `1/fps`; the output loop
   consumes one slot every `1/fps`. Total wall time is therefore tied to media
   duration. This is not a performance defect: the adaptive controller decides whether
   the bitrate is over budget in *programme* seconds, and only realtime pacing makes
   wall time equal programme time.
2. **Behaviour equivalence by default.** With no new flags the output matches the
   Python reference v6.3.3. `reference/live_archive_encoder.py` is kept verbatim: it is
   both the provenance and the e2e baseline.
3. **Failure before the long run.** A realtime pass can last an hour, so parameters,
   capabilities, container, pixel format, write permission, player locks and the
   encoder/muxer combination are all verified before the first byte is committed.

### 1.3 What it is not

* Not an encoder: every pixel is handled inside FFmpeg child processes.
* Not a batch transcoder by default (`--no-realtime`, added in v7.9.9.1, changes frame
  selection and rate control — see §7.1).
* Not a glitch/damage tool. The manual says so explicitly: "this tool encodes, it does
  not damage."
* Not a GUI: CLI plus a terminal menu.

### 1.4 Lineage

| Stage | Shape | Key fact |
| --- | --- | --- |
| v6.2.2 → v6.3.3 | single-file Python | only v6.3.3 survived |
| v7.0 → v7.5 | C# port, internals rewritten | frame pool, async pipes, 1 ms timer, typed results |
| v7.6 → v7.9.6 | product features | recompress, era presets, subtitles, stream picks, HTTP, pix-fmt/colour |
| v7.9.7 → v7.9.9.4 | evidence fixes + engineering | site/year rows, self-update, bilingual docs, structured logs, `--no-realtime` |
| v8 (planned) | Rust rewrite | structural; v7 goes into maintenance |

---

## 2. Processes and data flow

### 2.1 Topology

This process is the **control plane**; FFmpeg is the **data plane**. A single pass runs
two children concurrently; split audio starts three (video and audio in parallel, then
one remux); `--parallel N` runs `N × 2` children concurrently, plus one join and one
remux.

```
                         ┌──────────────────── this process (control) ──────────────┐
  source.mp4 / URL / UNC │                                                          │
        │                │  ffprobe ──► SourceInfo / duration / stream inventory     │
        ▼                │      │                                                    │
 ┌──────────────┐        │      ▼                                                    │
 │ ffmpeg decode│ stdout │  build profile (cached on disk)                            │
 │ -f rawvideo  ├───────►│      │                                                    │
 └──────┬───────┘  raw   │      ▼                                                    │
        │ stderr         │  ArgsBuilder (pure) ──► decCmd / encCmd                   │
        ▼                │      │                                                    │
   diagnostics (64)      │      ▼                                                    │
                         │  preflight (3 frames / 0.5 s, real chain)                  │
                         │      │                                                    │
                         │      ▼                                                    │
                         │  capture thread ──► FrameBufferPool ──► slot loop (1/fps)  │
                         │      ▲                    │                    │            │
                         │      │                    └── newest frame ───►│ stdin      │
                         │      │                                        ▼            │
                         │      │        -progress (stderr)      ┌──────────────┐    │
                         │      └──── OutputMonitor ◄─────────────┤ ffmpeg encode│    │
                         │             │                        │ muxer        │    │
                         │             ▼                        └──────┬───────┘    │
                         │      adaptive FPS ladder                   ▼            │
                         │             │                    clip.<tag>.part.<ext>    │
                         │             └────► cadence ──────────► Promote           │
                         └──────────────────────────────────────────────────────────┘
```

### 2.2 Sequence of one single-pass encode

1. **Parse** into `CliOptions` (with `XxxExplicit` markers).
2. **Local actions first** — `--help`, `--version`, `--cpu-info`, `--emit-args`,
   `--save-config`, font import/query, `--ffmpeg-list`, `--ffmpeg-env`,
   `--ffmpeg-delete`, `--ffmpeg-download`, NNEDI3 weights, `--completion` — all return
   before FFmpeg discovery, because "install an FFmpeg" must not require one.
   `Program.Run` *is* that ordering.
3. **Discovery** via `FFmpegDiscovery`; if the input is interactive and nothing is
   installed, `FirstRun` offers a managed install.
4. **Capabilities** via `BuildProfileStore.For`: cache, or one sweep on a cold cache
   (four launches once, zero afterwards), with per-encoder caps loaded lazily and
   written back.
5. **Resolve the run**: era preset → encoder → container (from `--container` or the
   output extension) → size/fps/ladder/audio → pix-fmt/colour/dither → stream picks →
   subtitle plan → hwaccel probe.
6. **Preflight**: a real-chain trial of 3 frames / 0.5 s; on failure, retry in software
   or refuse.
7. **Pre-write checks**: destination writable, target not held, stale `.part` cleanup.
8. **Encode** (capture thread + slot loop).
9. **Promote** the `.part`; on failure `Keep` it and print where it is.
10. **Post-process** (optional recompress chain) → report → optional playback.

### 2.3 Why this topology

* **No pixels on the managed heap.** C# only moves `byte[]`; no colour conversion, no
  scaling. That is why "SIMD is already fully used" is a sound conclusion rather than a
  guess — the vector work happens inside FFmpeg, which dispatches at runtime.
* **The control plane must observe the data plane.** `-progress` goes to stderr, frames
  go to stdout; neither can block the other.
* **Child processes are the isolation boundary.** A hung, crashed or stalled FFmpeg has
  a defined reclamation path (`Kill(entireProcessTree)`, sliced waits, a write
  deadline).

---

## 3. Code organisation

```
src/LiveArchiveEncoder/
  Program.cs          startup ladder (local actions → toolchain → discovery → UI/CLI)
  Configuration/      Config / EncoderInfo / EncoderCaps / UserConfig / constant tables
  Core/               timecode, pixel-format maths, validation, high-res timer,
                      interrupt, audio catalogue, filter args, output transaction
  FFmpeg/             discovery, toolchain install, capability probe + cache, ffprobe,
                      process and pipe I/O
  Frames/             bounded pre-allocated frame pool
  RateControl/        rolling bitrate monitor fed by -progress
  Encoding/           argument builders, preflight, realtime loop, audio routing,
                      sharding, stream/subtitle planning
  PostProcess/        recompress chain, era presets, fragment tokenizer, fonts
  Remote/             SMB/UNC args, HTTP sources, link measurement
  Update/             two-phase self-update, version compare, update state
  Localization/       en/zh string tables
  UI/                 hand-written CLI parser, CUI menus, option catalogue,
                      completion generator, reports, playback
  Diagnostics/        rolling file log, path resolution
```

### 3.1 Dependency direction

`Program → UI → Encoding → {FFmpeg, Core, Configuration, Frames, RateControl}` and
`Encoding → PostProcess`; `PostProcess → Configuration`. There are no cycles, but
`Configuration` is referenced by everything and `Config` doubles as the mutable UI form
and the encoder's input DTO — the structural debt discussed in §7.4.

### 3.2 Two front ends, one core

`Cui.Run` and `Cli.Run` both build the same `RealtimeOptions` and enter
`RealtimeEncoder.RunSplit`; only the *source* of the values differs. Historically most
bugs came from the two paths interpreting one field differently (era-preset audio
codec, `--era-native` parsed but never read), which is why v7.9 added single points of
truth such as `MergeStoredPreferences` and `EraNativeOf`.

---

## 4. Core mechanisms

### 4.1 Realtime pacing

Two pacing points, both conditional on `RealTime`:

* **Capture thread**: after each full frame, `nextDeadline += interval`; when overdue it
  **resets the baseline** instead of catching up. Catching up would inject a burst of
  frames and change what the controller sees.
* **Slot loop**: `nextSlot += slotInterval`, same reset rule.

Time base is `Stopwatch.GetTimestamp()`. Windows defaults to a ~15.6 ms timer, which is
coarse for 30 fps (33 ms/slot), so `HighResolutionTimer.Enable()` calls
`timeBeginPeriod(1)`. It is **reference counted**: under `--parallel`, a shard that
finishes first must not hand the coarse timer back to shards still running.

`WaitUntil` is **sleep-based**: ≥1.5 ms left → `Sleep(remaining-1)`, 0.5–1.5 ms →
`Sleep(1)`, <0.5 ms → return (oversleeping costs more than undersleeping). CPU usage
during an encode is therefore near zero — the measured "98.6 % of wall time asleep" is
this design, and it is exactly why sharding helps: it overlaps sleep with real work.

### 4.2 Frame pool: explicit ownership, zero steady-state allocation

`FrameBufferPool(buffer_frames, frameSize)` pre-allocates `capacity + 2` arrays. The
`+2` covers the frame being read by the capture thread and the frame held as
`previous`; neither may be evicted by `Publish`.

* Four operations (`TryRent`, `Publish`, `TryTakeLatest`, `Release`) under one
  `System.Threading.Lock`.
* `Publish` evicts the oldest when full; `TryTakeLatest` returns the newest and releases
  the rest — the C# equivalent of `frame = captured[-1]; captured.clear()`.
* A `_borrowed[]` bitmap records which slots are really lent out. Before v7.9.8.6 the
  duplicate-release guard was `_freeCount >= _free.Length`, which is only true when the
  pool is fully idle — the dangerous case slipped through, the same buffer was handed to
  two writers, and the picture was silently corrupted.
* Releases run outside the gate because `Lock` is not reentrant; `TryTakeLatest` is
  called by the single pacing thread, so its scratch array is safe.

Why not `ArrayPool<byte>`: a returned array may be overwritten by the next renter, while
here the previous frame must stay valid until the next write completes. Ownership has to
be explicit, not a rental convention.

### 4.3 The adaptive frame-rate ladder

`EncoderMath.BuildLadder` turns `--fps` and `--fps-levels` into `FpsRung` records:

```csharp
readonly record struct FpsRung(double Fps, double DropAboveKbps, double RiseBelowKbps);
```

* Rung 0 is the base rate with an infinite drop threshold (there is nothing above it).
* Thresholds may be given explicitly as `FPS:drop:rise` (v7.7), otherwise derived from
  `limit`/`recover` (the pre-v7.7 rule).
* Era presets carry the ladder of their period (`bilibili/2012`: 30→25→20→15), but only
  when the user typed neither `--fps` nor `--fps-levels`.

Control law, once per slot:

| Condition | Action | Hysteresis |
| --- | --- | --- |
| `videoRate ≥ limit`, target rung higher | step down | 0.18 s if ≥1.25× limit, else 0.30 s |
| same, target lower | step up | 0.80 s |
| level > 0 and `videoRate ≤ rung.RiseBelow` | step up | 0.75 s |
| otherwise | clear timers | — |

Output cadence is accumulated: `cadence += effectiveFps / baseFps`; at ≥1 a fresh frame
is emitted and 1 is subtracted, otherwise the previous frame is repeated. Dropping fps
therefore **repeats**, never discards: the container always runs at `--fps` and the
viewer sees stutter, not a shorter file. `fresh` / `repeated` are reported.

Special case: intra-only encoders (MJPEG and the `-q:v` family) go through
`IntraQuality` since v7.9.8.11.1 — calibrate the quantiser, raise the accepted ceiling
to 1.15× the measured baseline, and keep the ladder out unless the estimate is 3× over.
Dropping fps cannot shrink a JPEG, it only makes the picture stutter.

### 4.4 Bitrate observation

FFmpeg writes `key=value` lines to stderr with `-progress pipe:2`. The reader consumes
three keys (`total_size`, `out_time_us`, `progress`, whose value `end` closes the
stream); everything else is a diagnostic and goes into a 64-line ring buffer.

`OutputMonitor` keeps a 1024-slot sample ring and updates `windowBytes` /
`windowMediaUs` only when a sample enters or leaves the window, so every query is O(1).
Rate prefers **media time** as the denominator and falls back to wall time only when
media time is unavailable. A `total_size` regression (muxer rewrite, truncation) clears
the window instead of producing a negative delta. The diagnostic ring is what supplies
the FFmpeg excerpt in "encoder exited with code 1; …".

### 4.5 Pipe I/O

* Decoder stdout is raw `rawvideo`; the encoder stdin receives the same bytes in whole
  `frameSize` blocks.
* Both stderr streams must be **drained continuously**; otherwise the child blocks on a
  full pipe and the encode stops. `PipeReaders` uses `PipeReader` +
  `SequenceReader<byte>` (since v7.9.9, replacing a hand-rolled splitter).
* One line is capped at 256 KiB. Output with no newline at all (some `\r` progress
  streams) is consumed in chunks so `PipeReader` cannot buffer an unbounded prefix.
* `onLine` returning `false` means "stop delivering", **not** "stop draining": stopping
  the read fills the child's stderr pipe and deadlocks it (fixed in v7.9.9.3).
* Writing a frame has a 30 s deadline. A synchronous write blocks forever when the
  encoder refuses to drain, during which Ctrl+C does nothing and the `finally` never
  runs — leaving two orphan FFmpeg processes.
* Waits are sliced at 250 ms so cancellation is observed while the child is alive;
  `Kill(entireProcessTree: true)` prevents grandchildren surviving.

### 4.6 Command construction and preflight

`ArgsBuilder` is a set of **pure functions**: given encoder, quality, ceiling, pixel
format and colour it returns `List<string>`. It is the only place that translates a
quality number into FFmpeg flags, and therefore the meeting point of the three quality
scales (`Qv`, `Crf`, `Bitrate`).

Order is semantic and deliberately preserved from v6.3.3:

```
ffmpeg -threads N -f rawvideo -pix_fmt F -s:v WxH -framerate FPS -i pipe:0
       [-i source] [-map ...] -c:v NAME <quality> [-r FPS -fps_mode cfr]
       <audio args> -t DUR -f MUXER -y out.part.ext
```

`-threads` sits **before the raw-video input**, exactly as in Python. FFmpeg scoping
means it may not reach the output encoder as a user expects; that is a known constraint
(§7.11) and must be benchmarked per encoder before moving.

`Preflight.Video` runs the **short version of the same command**: same filters, pixel
format, encoder flags and audio encoder, with duration clamped to 0.5 s and video to 3
frames. Hardware decode is first proven by decoding one real frame
(`Hwaccel.Resolve`); if the preflight still fails, the run retries once in software.

### 4.7 Capability probing and caching

The motive is **process startups**: before v7.9.5 a single CLI run launched FFmpeg six
times before decoding a frame (two `-encoders`, `-filters`, `-h filter=scale`,
`-h encoder=NAME`, `-version`), each ~210 ms on Windows.

Now:

* Identity = SHA-256 of `path | size | last write time`, first 16 hex chars. Not the
  version string, because asking for it *is* one of the launches being avoided.
* One sweep collects version, `-encoders` (video/audio/subtitle), `-filters` and
  `-h filter=scale`; per-encoder caps are lazy and written back.
* Memory cache plus `%LOCALAPPDATA%\LiveArchiveEncoder\cache\caps-<id>.json`. A broken
  file is a miss; `LAE_NO_CAPS_CACHE=1` forces a sweep.
* `SchemaVersion` is bumped on shape changes, and an old cache is ignored rather than
  deserialised into false defaults — exactly the trap v7.9.8.8 avoided when
  `HasRcMode` was added.

### 4.8 Sharded encoding (`--parallel N`)

```
source ──► ShardPlanner (whole-slot boundaries, clamped)
             ├─ shard 0 ──► single realtime pass ──► s000.mkv
             ├─ shard 1 ──► single realtime pass ──► s001.mkv
             └─ ...
                                   │
                     -f concat -c copy ──► joined.mkv
                                   │
                     check duration + video packet count
                                   │
                     -c copy remux ──► clip.part.<ext> ──► Promote
```

Four hard constraints, each paid for:

1. **Only whitelisted containers** (`avi`, `mp4`, `flv`, `asf`, `mpeg`). RoQ loses the
   second shard; RM drifts ~0.03 s per join.
2. **A shard must be at least 4 s** (`MinShardSeconds`); below that, two process spawns
   cost more than they save.
3. **Shards are always staged in Matroska** and remuxed after the join. Joining FLV
   directly yields a file whose picture stops partway while the audio continues, and
   FLV stores no per-stream duration, so a duration check cannot see it.
4. **Palette formats cannot be sharded**: the Matroska VfW header carries no palette, so
   msrle goes through one `.mkv` and comes out black (measured YAVG 123 → 16).

Two post-join checks: duration within `max(0.5 s, 2 %)`, and video packet count within
`max(4, 2 %)` of the summed `Slots`. Either failure produces "this format cannot be
concatenated safely; rerun without `--parallel`" rather than a silently damaged file.
`LAE_KEEP_SHARDS=1` keeps the staging directory, because "the picture stops 4/5 of the
way in" cannot be diagnosed after the shards are deleted.

### 4.9 Audio routing

`inline` (default) feeds audio as a second input to the encoder. `separate` encodes
video and audio separately and joins with `-c copy`. `auto` is separate with a fallback
to inline when the muxer refuses the split.

Before splitting, `CanSplitMux` spends 1–2 s encoding 3 synthetic video frames and 0.5 s
of sine, muxing them for real, and decoding the result with `-f null`. Some legacy
muxers (RoQ, RealMedia) **return 0 while dropping the whole video stream**, so an exit
code alone would declare success on an audio-only file.

The manual is explicit that `separate` **is not faster**: the video pass is still paced,
so the audio pass and the remux are pure added cost, and the extra process competes with
the capture thread and changes frame selection. It exists for verifiable A/V alignment
and for containers that require it.

### 4.10 Output transaction

```
clip.mp4                     requested name (may be held by a player)
clip.<pid><rand>.part.mp4    what is actually written
clip.mp4                     renamed into place on success
clip (2).mp4                 alternate name when the target stays locked
clip.<pid><rand>.part.mp4    deleted on failure; KEPT when the encode finished but
                             the rename could not be done
```

* The `.part` name carries the PID and a random segment: a fixed `clip.part.mp4` could
  be a user's own resumed file, and on Windows two outputs differing only in case share
  it — FFmpeg's `-y` would truncate one run's product for another.
* `EnsureTargetAvailable` opens the target with `FileShare.None` before the long run. On
  failure it closes the players **this program started for this file**
  (`ChildProcesses.KillAll(path)`, not a global kill) and retries once.
* It also removes `.part` files left by **earlier** processes that are not locked,
  comparing creation time with process start so a concurrent encode's file is not
  deleted (on POSIX deleting an open file succeeds and the other FFmpeg silently loses
  its output).
* `Promote` makes up to four attempts (sleeping 250/500/750 ms after the first three
  failures), then falls back to the first free `name (2).ext`. v7.9.8.13.2 once treated a Windows sharing violation as an ACL refusal
  and threw away a finished long encode; both are now retryable, and real ACL problems
  are caught by the pre-write check.
* Every downstream consumer (report, `_web` chain, player) follows
  `EncodeResult.PlacedPath`, not the name the user typed.

### 4.11 The recompress chain

A chain is N FFmpeg processes, each reading the previous hop's product:

```
clip_live.mp4 ──► clip_live_web.mp4 ──► clip_live_web2.flv ──► clip_live_web3.rm
```

* The program owns both ends: `ffmpeg -y -i <input> <user fragment> <output>`. The user
  can only write the middle; `-i` and the output path cannot be overridden.
* `PostArgs.Parse` splits on whitespace (honouring quotes; backslashes are **not**
  escapes, because Windows paths inside filter arguments need to stay literal) and then
  vets every token: an empty fragment, `-i`, `-o`, and any free-standing non-option
  token **at any position** are rejected as an extra input or output. Nothing is handed
  to a shell. `BuildArgv` is separate, so the shape can be asserted without running
  FFmpeg.
* Failure policy: **never touch the live product**. A failed hop deletes its own `.part`
  and reports `recompress 2/3`; earlier hops stay, because each is a real artefact of
  the process.

### 4.12 Era presets

32 built-in rows: 5 `generic/<year>` (1998/2003/2007/2010/2014) and 27 `site/year` rows
across 13 sites (cctv, sina, sohu, netease, qq, tudou, youku, ku6, 56, acfun, bilibili,
iqiyi, youtube).

Each row describes two layers:

* **live**: encoder, size, rate, ladder, bitrate ceiling, both quality scales (`q:v` and
  CRF), averaging window, frame-queue depth, deinterlacer, audio encoder/bitrate/rate/
  channels;
* **second pass**: the site's transcode — size, rate, bitrate, profile, audio, container
  and `H264Impl` (OpenH264 from 2010 onward, libx264 before).

Design points:

* **Two quality scales.** MPEG `q:v 9` and x264 `crf 9` are not the same picture
  (measured 375 vs 935 kbps on the same frame), so every row stores `LiveQv` and
  `LiveCrf`. The formula `crf = 7.5 + 1.17 × q:v` is only used where no calibration
  exists (a user switching encoders by hand).
* **A preset is a fallback, not an override.** Explicit `--fps`, `--limit`, `--qv`,
  `--window`, `--deinterlace` always win. v7.9.3.1 had this inverted
  (`era?.Fps ?? args.Fps` with a non-nullable `era.Fps`), so the preset won every time.
* **Data, not code.** User presets live in `config.json` under `CustomEras` and override
  a built-in id; pre-v7.9.7 ids resolve through `Aliases` with a one-line rename note.
* **Evidence grading.** `docs/era-presets.md` marks every row A/B/C with its source;
  C means "inside the period's envelope, estimated", not measured.

### 4.13 Configuration and state

```
%LOCALAPPDATA%\LiveArchiveEncoder\        ~/.local/share/LiveArchiveEncoder/
  config.json                               interactive settings snapshot (UserConfig v6)
  configs/                                  named profiles
  cache/caps-<id>.json                      build profiles
  ffmpeg/<id>/bin/                          managed FFmpeg
  fonts/                                    subtitle fonts
  output/                                   automatic destination for network sources
  nnedi/                                    NNEDI3 weights + metadata
  logs/                                     rolling logs (LAE_LOG_DIR overrides)
```

* `UserConfig.Version` is **written but never read**: every field is optional, and a
  missing field means the old behaviour. `Normalise()` holds the patches (for example
  `Crop43: bool` → `FitMode: string`).
* Saves are atomic (temp file + replace), so a crash cannot leave a truncated file that
  resets everything.
* A CLI run **only changes fields it was told about** (v7.9.8.6). Before that, one
  `--encoder mpeg4` silently reset the recompress chain, subtitle block, GitHub front,
  NNEDI3 weights path, auto-update switch, toolchain source and player.
* "Not mentioned" and "explicitly empty" are different: `GhProxy` uses `null` (undecided,
  may probe) versus `""` (off, stop asking).
* Three roots can be redirected: `LAE_CONFIG_DIR` (`config.json`, `configs/`, `cache/`,
  `output/`), `LAE_LOG_DIR` (logs) and `LAE_FONT_DIR` (fonts; the legacy
  `LAE_WATERMARK_FONT_DIR` still resolves). Managed FFmpeg (`ffmpeg/`) and NNEDI3
  weights (`nnedi/`) stay under the real data root and deliberately do **not** follow
  `LAE_CONFIG_DIR` — relocating settings must not make installed components disappear.
  The e2e suite isolates what it writes through `LAE_CONFIG_DIR` + `LAE_FONT_DIR` and
  never touches a developer's real `config.json`.

### 4.14 CLI / CUI / localisation

* **Hand-written parser** rather than `System.CommandLine`, to reproduce argparse's help
  text, error text and exit code 2 exactly. The cost is a 1,561-line `CliOptions` and a
  1,309-line `Cli`.
* **`OptionCatalog` is the single source for completion.** v7.9.8.5 shipped scripts that
  knew 69 of the parser's 112 spellings, so two tests now hold the table and the parser
  against each other in both directions.
* **CUI** is a pure terminal menu: main menu → parameters/encoder/audio/streams/
  subtitles/pixel format/recompress/era/parallel/playback/update/language/profiles. It
  writes `config.json`; the CLI reads it — that is how both share "last settings".
* **Localisation**: two `Dictionary<string,string>` tables whose key sets and placeholder
  counts must match (unit-tested). A missing key renders as the key, so a typo fails
  loudly. FFmpeg's own diagnostics are deliberately **not** translated.
* 663 `Console.*` calls are the only user-visible output; logs do not mirror to the
  console (except the `--self-update-apply` process, which must stay quiet).

### 4.15 Toolchain and network

* `FfmpegCatalog` lists six sources (BtbN, nonfree, ytdlp, autobuild, gyan, avbuild),
  filtered per platform (gyan and autobuild are Windows-only); each knows whether it can be
  downloaded automatically, its archive format and its install layout.
* Download → verify (when a digest exists) → unpack (7-Zip or `tar.xz`) → write
  `build.json` → publish `bin` onto the current shell's `PATH`
  (`ToolchainPath.Publish`). `--ffmpeg-env bash` prints an `export PATH=…` line for a
  terminal that is already open.
* `--gh-proxy` exists for networks where GitHub is unreachable (measured: 0 bytes in 20 s
  direct, `206` with a correct `Content-Range` through a front). Three states: unset
  (probe for 5 s), set (use), `--no-gh-proxy` (remembered as "stop probing").
* `NetworkProbe` reads the source itself to measure throughput, jitter and first-byte
  latency; `--iperf3` is only a reference because iperf3 cannot see a NAS's disk or its
  per-connection limits.
* `Smb` builds UNC arguments (`rw_timeout`, read/write differences, unreachable hints);
  `HttpSource` adds the `-reconnect` family and a 30 s socket timeout, and explicitly
  does not support auth, cookies or HLS.

### 4.16 Self-update

Two phases, because Windows cannot replace an image that is mapped as an executable:

1. Old process: read the feed → pick this build's asset → download → verify `sha256:` →
   unpack into `<install>/.update-<tag>/` → run the new binary's `--version` to prove it
   works → start it with `--self-update-apply` → exit.
2. New process (from staging): wait for the parent to exit → copy itself into place →
   optionally relaunch.

Rules: https only (loopback http for the local test harness), never downgrade, never
touch FFmpeg, and only accept an asset whose name ends with the exact `AssetSuffix()`
(`-win-x64.zip`, `-win-x64-self-contained.zip`, `-win-x64-aot.zip`). There is no code
signing, no automatic rollback and no `.old` backup — a deliberate trade-off (§7.14).

### 4.17 Diagnostics

* Serilog rolling file, configured in code (no XML/JSON) so it survives the Release
  trimming switches and stays NativeAOT-friendly.
* One correlated log per run: `process.exit`, `ffmpeg.discovered`, `output.promoted`
  with structured fields; `LAE_LOG_LEVEL` sets the level and `LAE_LOG_MIRROR` mirrors to
  the console.
* `ffmpeg.stderr` is **sampled** (`AppLog.Sampled`) so a long run cannot fill the log
  disk with hundreds of thousands of lines.
* User-facing text still goes through `Console`; the log is a supplement, because the
  e2e suite asserts on the console text.

### 4.18 Platforms and publish shapes

| | Windows | Linux |
| --- | --- | --- |
| executable | `LiveArchiveEncoder.exe` | `LiveArchiveEncoder` |
| data root | `%LOCALAPPDATA%\LiveArchiveEncoder` | `~/.local/share/LiveArchiveEncoder` |
| network path | UNC `\\host\share` | CIFS mount |
| player | `ffplay` / file association | `ffplay` / `xdg-open` |
| NativeAOT | needs MSVC + Windows SDK | experimental (glibc bound) |

Three shapes: framework-dependent JIT (`net10.0` + `RollForward=Major`, so the same
archive starts on a .NET 11-only machine; CI compiles `-p:LaeTfm=net11.0` to prove it),
self-contained JIT, and NativeAOT (single file, fastest start, same encode speed).
Releases ship four JIT archives (Win/Linux × framework-dependent/self-contained) with
SHA-256, per `MAINTENANCE.en.md`; the AOT path stays in the code and in the updater's
suffix table.

---

## 5. Quality assurance

### 5.1 Unit tests (TUnit, 52 files / 9.6k lines)

Only what needs no real encode: SMB arguments, the FFmpeg catalogue, toolchain layout,
player arguments, CLI parsing, config round-trips, hwaccel normalisation, localisation
parity; the recompress tokenizer and its rejections, era-preset consistency, settings ↔
command-line conversion, the option catalogue in both directions, completion generation.
They also run on NativeAOT-sensitive APIs, so a reflection-only path fails here instead
of in a published binary. `InternalsVisibleTo` is scoped to the test project.

### 5.2 End-to-end (pytest, 23 files / 4.4k lines)

| module | cases | what it checks |
| --- | --- | --- |
| `test_compare_matrix.py` | 12 | encoder/param matrix, ffprobe structure vs v6.3.3 |
| `test_error_paths.py` | 11 | failure paths and exit codes |
| `test_feature_sweep.py` | 35 | every legacy encoder, 2 s clip |
| `test_option_smoke.py` | 63 | option plumbing, recompress and era presets |
| `test_cui_smoke.py` | 12 | interactive menu assertions (40+ checks) |
| `test_stream_selection.py` | 9 | absolute stream indexes and time clipping |
| `test_subtitles.py` | 7 | burn/copy/off, sidecar and embedded, bad requests |
| `test_http_source.py` | 7 | HTTP(S) inputs, timeouts, failures |
| `test_config_conversion.py` | 4 | settings ↔ command line round trip |
| `test_ab_duration.py` | 2 | duration medians vs baseline |

Two phases: the `timing` and `shared_config` groups run **serially and exclusively**
(the first is wall-clock sensitive, the second writes the real settings file), the rest
under `pytest -n`. A guard in `conftest.py` refuses "`-n` plus an exclusive group",
because that silently turns timing assertions flaky. Full run: ~492 s on a 32-core box
(794 s serial).

### 5.3 Repository gates

* `tools/check-slnx.py`: the `.slnx` manifest must match the tree in both directions,
  and every `docs/*.md` must have a `docs/*.en.md` twin.
* GitHub Actions: Windows + Linux build, unit tests, manifest check, and a second
  build/test pass against `net11.0`.
* Codeberg: Woodpecker (Linux) and Forgejo Actions (framework-dependent win/linux
  publishes as artifacts).
* e2e is **deliberately not in CI**: it needs FFmpeg with legacy encoders, a quiet
  machine and a real clock.

### 5.4 Documentation

Eleven bilingual pairs (two of them CHANGELOGs) plus an archive note. The language contract is enforced:
`X.md` is Chinese, `X.en.md` is English, relative links may not cross languages, and
history documents keep Chinese as the sole authoritative full text. Documentation is
part of the behavioural contract — comments cite sections such as
`docs/usage.md §7.17`.

---

## 6. Strengths

1. **A reproducible port methodology.** The reference implementation is kept verbatim,
   e2e compares against it, and "byte-identical with no flags" is an executable
   assertion. Every later feature can be layered safely as opt-in.
2. **A clean control/data boundary.** C# never touches pixels; the optimisation targets
   are allocation count, pipe throughput and timer precision, and conclusions about SIMD
   become verifiable rather than speculative.
3. **Zero steady-state allocation on the frame path.** Pre-allocated buffers, a borrow
   bitmap and newest-frame semantics; the `+2` slots keep the in-flight and previous
   frames out of eviction.
4. **Real timing engineering.** Reference-counted `timeBeginPeriod(1)`, sleep-based
   waiting, and reset-not-catch-up pacing keep a long run's timeline stable while using
   almost no CPU.
5. **Observation drives control.** Streaming `-progress` parsing, O(1) rolling-window
   aggregation and media-time-first rate measurement mean the controller sees real bytes
   and real programme time.
6. **Fail-early everywhere.** Parameters, capabilities, extension, palette, container,
   write permission, player lock, a real-chain preflight, audio splittability, NNEDI3
   weights — all before the first byte.
7. **Capability caching with the right identity.** 6 launches → 4 on a cold run → 0
   afterwards; identity is size+mtime, not the version string that would cost a launch.
8. **Sharding refuses rather than corrupts.** Whitelist, minimum shard, Matroska
   staging, duration + packet-count checks, fallback to a single pass.
9. **Transactional output.** `.part` + `Promote` + `Keep`/`Discard` + `(2)` fallback +
   pre-flight lock detection: never overwrite the source, never lose a finished capture.
10. **Concrete security boundaries.** The recompress fragment is tokenised and vetted,
    URL file names are sanitised, extension correction never overwrites an existing file,
    and updates accept https only.
11. **Single-source-of-truth as ongoing work.** Option catalogue ↔ parser tests,
    completion generated from the table, en/zh key parity, dual-TFM CI, `.slnx` and
    bilingual gates.
12. **Operability.** Structured rolling logs with a run correlation id, `--emit-args` /
    `--save-config` round trips, `--net-test`, `--cpu-info`, `--ffmpeg-env`, and
    `--update-check`'s 0/3/2 exit codes.
13. **Honest technical writing.** Almost every non-obvious constant carries a measurement
    date, a control group and a conclusion — including a record of having been wrong.
14. **Complete release engineering.** Four archives, SHA-256, three-way asset matching
    for the updater, three CI systems and a bilingual documentation gate.

---

## 7. Weaknesses and risks

### 7.1 Design trade-off: wall-clock binding

By default total runtime equals media duration. That is necessary for "how it looked
then" and fatal for "transcode an archive". `--no-realtime` (v7.9.9.1) is an escape
hatch, but it **changes semantics**: the bitrate window no longer maps to programme
seconds, so the caller pins a single rung, and frame selection becomes one frame per
slot. Fast and period-accurate are mutually exclusive, and the project offers no
side-by-side reproducible baseline for the two modes.

### 7.2 Structural debt: very large files

| file | lines | problem |
| --- | --- | --- |
| `UI/Cui.cs` | 3,641 | menu flow, editing, reporting, updates, playback in one class |
| `Localization/Strings.cs` | 1,634 | two full string tables |
| `Encoding/RealtimeEncoder.cs` | 1,562 | preparation, preflight, building, executing, splitting, verifying |
| `UI/CliOptions.cs` | 1,561 | fields + explicit markers + parse state |
| `UI/Cli.cs` | 1,309 | normalisation, preset merging, reporting, post-processing |
| `Update/SelfUpdate.cs` | 1,171 | HTTP, unpacking, verification, process replacement |

Beyond ~1,000 lines the blast radius of a change lives only in someone's memory.
`RealtimeEncoder.Execute` is the extreme case: two processes, two stderr readers, a
capture thread, a slot loop, a ladder state machine and a cancellation path in one
method.

### 7.3 Structural debt: global mutable state

`EraPresets.CustomRows`, `BuildProfileStore.Memory`, `L`'s current language,
`ConfigStore` reading the disk directly, the `AppLog` singleton and
`HighResolutionTimer`'s static counter. Two differently-configured runs cannot coexist
in one process, and tests must be serial or directory-isolated. Acceptable for a CLI,
a blocker for a library or a service.

### 7.4 Structural debt: four overlapping models

`CliOptions` (with `XxxExplicit`) → `Config` (UI form) → `RealtimeOptions` (encoder
input) → `UserConfig` (persistence), heavily overlapping, connected by hand-written
mapping plus `with`. `with` fixed "forgot to copy a new field" but not "precedence rules
scattered everywhere":

```csharp
double fps = args.FpsExplicit ? args.Fps : era?.Fps ?? args.Fps;
int    w   = args.WidthExplicit ? args.Width : eraSize?.W ?? era?.Width ?? args.Width;
```

Dozens of these exist; each is a potential CLI/CUI divergence, and divergence has
happened repeatedly (`--era-native` parsed and never read; the preset's audio codec
honoured in the menu but ignored by the CLI).

### 7.5 Engineering debt: exceptions as control flow

`ArgumentException` means usage error (exit 2); `InvalidOperationException` means FFmpeg
failure, configuration conflict, child-process anomaly or internal inconsistency
indistinguishably. Exit codes are 0/1/2/3/130. A script cannot tell "source unreachable"
from "encoder unsupported" from "disk full". Messages are specific but not
machine-consumable.

### 7.6 Engineering debt: no port abstractions

`Execute` calls `Process.Start` directly; `Probe` launches ffprobe directly. There is no
`IFrameSource`, `IEncoderSink`, `IClock` or `IProcessRunner`, so the core loop cannot be
unit-tested and only e2e covers it. The test pyramid is therefore inverted: 4.4k lines of
e2e, ~13 minutes, timing-sensitive, barred from CI.

### 7.7 Engineering debt: heuristic constants scattered

0.18 / 0.30 / 0.75 / 0.80 / 1.10 / 1.25 / 3.0 (control law), 30 s (write deadline), 4 s
(minimum shard), 256 KiB (line cap), 1024 (sample capacity), 64 (diagnostics). No central
parameter table; verbose mode prints one line of direction but never records *why* a
decision was made at a given slot. Tuning requires reading code and guessing.

### 7.8 Engineering debt: logs but no metrics

No structured event stream (slots, ladder changes, dropped frames, probe durations,
process launches) and no metrics/tracing. Long-term regression can only be "e2e passed
or failed"; it cannot answer "did this change lower the average ladder position".

### 7.9 Engineering debt: localisation and output shape

`Dictionary<string,string>` with `{0}`, no plural rules, terminal width, colour or
capability detection. All 663 `Console.*` calls are direct writes, and there is no
`--json`: scripts parse human text or trust exit codes.

### 7.10 Platform bias

Windows is primary: `winmm` P/Invoke (guarded, but not abstracted), SMB/UNC semantics,
`File.Move` sharing-violation handling, AOT linking against MSVC/SDK. Linux NativeAOT is
explicitly experimental (glibc-bound), and Linux timer precision relies on the actual
`Sleep` granularity without a dedicated verification.

### 7.11 FFmpeg behaviour is a comment-level contract

Many facts (`-threads` scoping, MJPEG's full-range requirement, usable mpeg1video frame
rates, Matroska dropping palettes, FLV concatenation damage) are recorded as comments
with measurement dates, and they **do not regress automatically**. An FFmpeg upgrade is
discovered through e2e or a user report.

### 7.12 Configuration has no explicit migrator

`UserConfig.Version` is written, never read; compatibility rests on "missing field =
old behaviour". That has held across six schema revisions, but semantic changes
(`Crop43: bool` → `FitMode: string`) live as patches in `Normalise()`. The more fields,
the harder completeness is to prove.

### 7.13 Parallelism is process-level brute force

`Parallel.For` starts N shards, each with two FFmpegs and a capture thread; no CPU/IO
quota, no backpressure, no degradation when the machine is busy elsewhere. Realtime
pacing is therefore load-sensitive — the docs admit timing tests need a quiet machine.

### 7.14 Update trust model and rollback

Only the release JSON's `sha256` is checked (trusting GitHub, TLS and the feed); there is
no code signing, no automatic rollback and no `.old` backup; `--self-update-apply`
depends on platform file-lock semantics. Reasonable for a small GPL tool, insufficient
for enterprise distribution.

### 7.15 Documentation ≈ code in size

Eleven bilingual pairs (two of them CHANGELOGs); spec/code sync is entirely manual.
`check-slnx.py` verifies existence and pairing, not freshness. The more detailed the
docs, the harder stale entries are to spot.

### 7.16 Comments as archaeology

Many comments begin `v7.9.8.13.2:` and explain an old bug. Traceability is the upside;
the downside is that reading new code requires understanding old bugs, and
`archive/retired/` adds search noise.

---

## 8. Designing v8 (Rust)

v8 is a rewrite, so this section is about what must be inherited and what must be
redesigned — not about patching v7.

### 8.1 Contracts that must survive

1. **Output equivalence with no flags**, byte-for-byte FFmpeg argv (argument order,
   `G6` float formatting, `-threads` position), using v7.9.9.4 as the golden baseline.
2. **Realtime semantics**: `1/fps` on both ends, reset-not-catch-up, repeat-not-drop,
   and the `fresh`/`repeated`/`slots` counters.
3. **Ladder thresholds and hysteresis** (0.18/0.30/0.75/0.80/1.25/1.10/3.0) and
   "preset is a fallback, not an override".
4. **The fail-early checklist**: capabilities, extension, palette/container conflict,
   writability, target lock, real-chain preflight, audio splittability, NNEDI3 weights.
5. **Transactional output**: `.part` naming, `Promote`/`Keep`/`Discard`, the `(2)`
   fallback, and following `PlacedPath`.
6. **Exit-code and text contract**: 0/1/2/3/130, `ERROR:` / `WARNING:` prefixes, and the
   exact strings the e2e suite asserts.
7. **Configuration compatibility**: read `config.json` v6 as-is, ignore unknown fields,
   write only what was told, and preserve the three-state "unset / empty / set" meaning.

### 8.2 Suggested crate layout

```
lae-cli         clap definitions, subcommands, completion from the same source
lae-core        domain types, timecode, quality scales, validation, Plan
lae-ffmpeg      spawn, pipes, -progress parsing, capability probe + cache, ffprobe
lae-pipeline    Realtime and Batch modes, frame pool, ladder, sharding, output txn
lae-post        recompress chain, era presets, subtitles/fonts
lae-net         HTTP/SMB, toolchain downloads, proxy probing, self-update
lae-config      serde schema, explicit migrate chain, profiles
lae-i18n        string tables plus terminal rendering (width, colour, plurals)
```

The key move is to collapse v7's four models into **one `Plan`**: the resolve phase
produces it, the encode phase consumes it, and CLI/CUI/future GUI are just builders.
Precedence rules live in `Plan::resolve`, not in dozens of `?:` expressions.

### 8.3 Mechanism mapping

| v7 (C#) | v8 (Rust) suggestion | note |
| --- | --- | --- |
| `Process` + redirected streams | `tokio::process::Command` + `AsyncRead` | keep Windows' no-console behaviour |
| `PipeReader` + `SequenceReader` | `tokio_util::codec::LinesCodec` or `BytesMut` splitting | keep the 256 KiB cap and "stop delivering, keep draining" |
| `FrameBufferPool` | `Vec<Box<[u8]>>` + borrow bitmap + `parking_lot::Mutex`, or `rtrb` | keep `+2` slots and newest-frame semantics |
| `Stopwatch` + `HighResolutionTimer` | `Instant` + `tokio::time::sleep_until`; `timerfd`/`clock_nanosleep` on Linux, high-resolution waitable timer on Windows | do not use `thread::sleep` for 33 ms pacing |
| `OutputMonitor` | ring buffer + incremental aggregation | media time before wall time |
| `BuildProfileStore` (JSON) | `serde` + the same size+mtime identity | add `schema_version`; corruption = miss |
| hand-written parser | `clap` derive + custom parsers | reproduce argparse's exit 2 and error style |
| hand-written completion | `clap_complete` | table and parser become one source by construction |
| `L` dictionaries | `rust-i18n` / `fluent` + `anstream` | terminal width and colour come for free |
| `Serilog` | `tracing` + `tracing-appender` | spans per run/slot give correlation naturally |
| exceptions as control flow | `thiserror` enums + `ExitCode` mapping | one variant per class of failure |
| direct `Process.Start` | `trait ProcessRunner` + `FakeRunner` | makes the core loop unit-testable |

### 8.4 v7 defects v8 should fix on the way

1. **Two first-class modes**: `Realtime` and `Batch`, each with its own rate-control
   semantics and acceptance baseline; stop treating `--no-realtime` as "pin the ladder".
2. **One Plan**: delete the four-model chain and the scattered `XxxExplicit` precedence.
3. **Error taxonomy**: at least `Usage` / `SourceUnavailable` / `EncoderUnsupported` /
   `OutputBusy` / `Io` / `Internal`, mapped to stable exit codes.
4. **Concurrency budget**: shards and audio passes share a semaphore bounded by CPU/IO;
   realtime mode detects load and degrades explicitly.
5. **Observability**: a `tracing` event per slot (opt-in), ladder changes, drops and
   probe durations as fields — data for long-term regression, not just logs.
6. **Explicit config migration**: `migrate_v5_to_v6()` chains plus golden files instead
   of "missing = old behaviour".
7. **Machine-readable output**: `--json` NDJSON events.
8. **A real cross-platform clock layer**: one trait, separate Windows/Linux
   implementations, benchmarked precision.

### 8.5 Suggested migration path

1. **Golden baseline first**: record argv snapshots and output fingerprints (container
   structure, duration, packet count, keyframe count, pixel format, bitrate band) across
   every encoder × preset × option combination. Without it a rewrite cannot be accepted.
2. **`lae-core` + `lae-ffmpeg`**: Plan, capability probing, argv construction; snapshot
   tests to the byte.
3. **`lae-pipeline` in Batch mode** (no pacing, easiest to test), then add Realtime
   pacing as a separate strategy.
4. **Port post-processing and presets**: they are data + argv, the lowest-risk part.
5. **Finally the UI and self-update**: `clap` for the CLI, a menu crate for the CUI (or
   CLI only at first).
6. **Run both for a while**: v7 and v8 execute the same e2e suite in CI; every
   difference must be explained, not merged away.

### 8.6 Risks

* **argv order and float formatting**: FFmpeg is position-sensitive; `G6` (`13`) differs
  from Rust's default (`13.000000`) and must be reproduced exactly.
* **Windows file-lock semantics**: the `.part`/`Promote`/`(2)` logic depends on sharing
  violations and ACL refusals both surfacing as access-denied; Rust must re-derive that
  from `io::ErrorKind`.
* **UNC and long paths**: `\\?\` prefixes, SMB timeouts and `rw_timeout` need per-item
  verification.
* **AOT / glibc**: a statically linked musl binary interacting with a dynamically linked
  FFmpeg needs its own release design; do not copy v7's three shapes blindly.
* **Self-update replacement**: Windows still cannot replace a running image; keep the
  two-phase scheme, possibly via a crate such as `self-replace`.
* **Documentation and contract**: v8 must inherit §8.1 or the rewrite becomes a different
  tool and the e2e suite loses its meaning.

---

## 9. Appendix

### 9.1 Where to read what

| question | file |
| --- | --- |
| startup and dispatch | `src/LiveArchiveEncoder/Program.cs` |
| one encode end to end | `Encoding/RealtimeEncoder.cs` (`RunSingle` → `Execute`) |
| frame ownership | `Frames/FrameBufferPool.cs` |
| bitrate observation | `RateControl/OutputMonitor.cs`, `FFmpeg/PipeReaders.cs` |
| argument construction | `Encoding/ArgsBuilder.cs`, `Encoding/Preflight.cs` |
| capability probe/cache | `FFmpeg/BuildProfileStore.cs` |
| sharding and joining | `Encoding/ParallelEncoder.cs`, `Encoding/ShardPlanner.cs` |
| placing the product | `Core/OutputFile.cs` |
| recompress | `PostProcess/Recompress.cs`, `PostProcess/PostArgs.cs` |
| era presets | `PostProcess/EraPreset.cs`, `docs/era-presets.en.md` |
| settings | `Configuration/UserConfig.cs` |
| CLI and completion | `UI/CliOptions.cs`, `UI/OptionCatalog.cs`, `UI/Completion.cs` |
| self-update | `Update/SelfUpdate.cs` |
| logs | `Diagnostics/AppLog.cs`, `Diagnostics/LaePaths.cs` |

### 9.2 Key constants

| constant | value | location | meaning |
| --- | --- | --- | --- |
| `WriteDeadlineSeconds` | 30 | `RealtimeEncoder` | one frame's write budget |
| `MinShardSeconds` | 4.0 | `Constants` | below this sharding costs more |
| `ConcatSafeFormats` | avi/mp4/flv/asf/mpeg | `Constants` | shardable containers |
| `SampleCapacity` | 1024 | `OutputMonitor` | rate-window samples |
| `MaxLine` | 256 KiB | `PipeReaders` | one stderr line |
| `DefaultFps` / `DefaultFpsLevels` | 13 / 13,12,10,8,5 | `Constants` | default rate and ladder |
| `SchemaVersion` | 4 | `BuildProfileStore` | capability cache shape |
| `CurrentVersion` | 6 | `UserConfig` | settings shape (written, not read) |

### 9.3 Environment variables

| variable | effect |
| --- | --- |
| `LAE_CONFIG_DIR` | redirect config/cache/fonts/output root |
| `LAE_LOG_DIR` / `LAE_LOG_LEVEL` / `LAE_LOG_MIRROR` | log directory, level, console mirror |
| `LAE_FONT_DIR` | subtitle font directory |
| `LAE_NO_CAPS_CACHE` | force a capability re-sweep |
| `LAE_KEEP_SHARDS` | keep the shard staging directory |
| `LAE_LOG=0` | disable the file log entirely |
| `LAE_UPDATE_FEED` | override the update feed |
| `LAE_UPDATE_TOKEN` | bearer token for an authenticated feed (never stored, never logged) |
| `LAE_WATERMARK_FONT_DIR` | legacy font-directory variable, still honoured |

### 9.4 Glossary

* **slot** — one `1/fps` position on the output timeline; it carries a fresh frame or a
  repeat.
* **rung** — one step of the adaptive frame-rate ladder, with drop and rise thresholds.
* **build profile** — the encoder/filter/algorithm inventory of one FFmpeg binary,
  cached by binary identity.
* **era preset** — a `site/year` or `generic/year` row describing both the capture layer
  and the site's transcode.
* **realtime simulation** — advancing the encode at the source's wall-clock cadence
  rather than as fast as possible.
