# Version history (v7.9.8 onwards) — English summary

[English](CHANGELOG.en.md) · [中文](CHANGELOG.md)

This is a **per-version summary**. The authoritative full record is the Chinese
edition, [`CHANGELOG.md`](CHANGELOG.md) — entries there are kept verbatim, with the
measurements and reasoning behind each decision. `⚠` marks a change a user can
notice in an output, a default, a file name or a CLI contract.

## v7.9.9.10 (2026-11-13) — opt-in tearing, and the DVD bitrate fix

### `--tearing horizontal|random`

Reproduces a dropped vertical sync: the top of the picture takes the previous
frame, the bottom takes the current one.

```
LiveArchiveEncoder.exe in.mp4 out.mkv --encoder libx264 --tearing horizontal
LiveArchiveEncoder.exe in.mp4 out.mkv --encoder libx264 --tearing random
LiveArchiveEncoder.exe in.mp4 out.mkv --tearing off        # default
```

* Implemented as `tblend=all_expr='if(lt(Y,<split>),B,A)'`, appended at the **end**
  of the filter chain (after `format`) so the split lands on the delivered picture.
* `horizontal` splits at the middle (`H*0.5`); `random` walks the split down the
  frame (`H*mod(N*37\,100)/100`), so consecutive frames tear at different heights.
* Off by default: the tool encodes faithfully unless the artefact is requested.
* CUI parameter row `39.` sets it and remembers it in `config.json`.

### Fix: `--era dvd` (and `--disc dvd`) encoded at a third of its bitrate

Comparing against v7.9.9.9: a 10 s `--era dvd` product measured **2091 kbps** while
the preset says 6000. **`-q:v` wins over `-b:v`** inside FFmpeg, and the media row
carried both (`-b:v 6000k` from the extras, `-q:v 10` from `LiveQv`), so the encoder
picked the quantiser and silently dropped the target bitrate.

`ArgsBuilder.PinsBitrate` now detects `-b:v` / `-maxrate` in the extras and makes
`VideoQualityArgs` emit `-b:v <ceiling>` instead of `-q:v`. All three command builders
(real encode, preflight, the split-audio video pass) share the check, so the trial
validates the real command. Measured: the same 10 s clip went **2091 → 6319 kbps**.

Tests: `V79910Tests` (6 cases) and `tests/e2e/test_tearing.py` (4 cases, including a
`--era dvd` bitrate guard). Unit suite **768 passed**.

---

## v7.9.9.9 (2026-11-13) — disc targets become era presets; three image bugs

### The disc targets joined `--era`

`dvd` / `vcd` / `svcd` / `cd` (CD-i) are now four rows of the era table with the ids
`disc/dvd` … `disc/cd`, plus the shorthands `dvd` / `vcd` / `svcd` / `cd` / `cd-i` /
`cdi`:

```powershell
LiveArchiveEncoder.exe in.mp4 out.mpg --era dvd          # author as DVD-Video
LiveArchiveEncoder.exe in.mp4 out.mpg --era vcd          # author as Video CD
LiveArchiveEncoder.exe in.mp4 out.mpg --era disc/svcd    # the full id works too
```

* The numbers are derived from `DiscProfiles`, which stays the single source; a new
  `EraPreset.Disc` field marks the four rows and the CLI now takes the profile from the
  selected preset instead of three separate `args.Disc ?? stored.Disc` lookups.
* `--disc dvd` survives as a **shorthand** (it resolves to `--era disc/dvd`), so a script
  written against v7.9.9.8 keeps working; if both are given, `--disc` wins.
* CUI menu 14 gains a `disc` group automatically (dvd, vcd, svcd, cd); picking one applies
  size, rate, bitrate and audio and appends `-maxrate/-bufsize/-g` to the extra args.
  Parameter row `39.` and the `Config.Disc` field are gone — one entry point.

### Three defects in the v7.9.9.8 image code

* **7-Zip's exit code was ignored.** Only "did a `.DAT` appear" was checked, so a partial
  extraction (exit 1) or a failed one (exit 2) that happened to leave one file was
  encoded. Now `>= 2` fails and the tail of 7-Zip's output travels with the error.
* **`Path.GetFullPath` sat outside the try.** A too-long or malformed path escaped
  `Resolve`'s "an image or a reason" contract and crashed the CLI with a stack trace;
  the whole `ResolveCore` is guarded now.
* **The reason was thrown away.** When `7z l` returned non-zero (blocked binary, locked
  image, timed-out share) the code printed "not a readable disc image" and discarded the
  listing. It now appends `[7z <code>] <tail>`.
* Also: 7-Zip is polled every 250 ms and honours `Interrupt.CurrentToken`, so Ctrl+C
  stops a hung scan instead of waiting out the 180 s timeout.

### Tests

The four generic era invariants (a two-rung ladder, monotonically growing pixels and
bitrate, the `site/year` id shape) now explicitly apply to site rows only; media rows use
`disc/<id>` with a single rung. `test_disc.py` covers `--era vcd`, `--era disc/dvd` and
keeps `--disc cd` / `--disc dvd --fps 29.97` for the shorthand. Unit suite **762 passed**.

---

## v7.9.9.8 (2026-11-12) — disc images and disc targets

### Disc images (DVD-Video and Video CD)

`*.iso` / `*.img` are accepted as input. Recognition is **structural**, not by
extension or by "ffprobe opens it":

| Layout | Evidence | Handling |
| --- | --- | --- |
| **DVD-Video** | `VIDEO_TS/` plus `*.IFO` | The ISO is handed to FFmpeg: its `dvdvideo` demuxer reads MicroUDF, follows the IFO and stitches `VTS_zz_1..9.VOB`. Verified with `ffprobe -f dvdvideo`; no unpacking |
| **Video CD** | `MPEGAV/` plus `*.DAT` | No VCD demuxer exists, so 7-Zip unpacks `MPEGAV/*.DAT` into a temp directory that is deleted on exit |
| Anything else | neither | `exit 2`, "not a DVD-Video or VCD image" |

The output keeps the **ISO's** name, so a VCD is not reported as `AVSEQ01_live.mpg`.

### `--disc dvd|vcd|svcd|cd`

A new axis, orthogonal to `--era`: the era is "which site, which year", the disc is
"which physical format". Constraints follow the published specifications:

| Target | Codec/container | PAL / NTSC | Video | Audio | GOP |
| --- | --- | --- | --- | --- | --- |
| `dvd` | mpeg2video / mpeg | 720×576@25 / 720×480@30 | 6000 kbps, maxrate 8000, bufsize 1835k | ac3 384k 48 kHz 2ch | 15 |
| `vcd` | mpeg1video / mpeg | 352×288@25 / 352×240@30 | 1150 kbps CBR, bufsize 327k | mp2 224k 44.1 kHz 2ch | 15 |
| `svcd` | mpeg2video / mpeg | 480×576@25 / 480×480@30 | 2520 kbps, bufsize 917k | mp2 224k 44.1 kHz 2ch | 15 |
| `cd` (CD-i) | mpeg1video / mpeg | as VCD | as VCD | as VCD | 15 |

* The DVD numbers come from the Wikipedia article the user cited (MPEG-2 up to
  9.8 Mbit/s, 720×576 / 720×480, AC-3 48 kHz ≤448 kbit/s, 4:2:0 8-bit).
* The VCD numbers come from the same encyclopaedia's White Book entry (MPEG-1,
  352×288 / 352×240, 1150 kbit/s CBR, MP2 44.1 kHz 224 kbit/s).
* **CD-i and VCD share the MPEG-1 envelope**: the cited page only says the two
  differ by "minor differences between the standards", so no different numbers are
  invented; the id exists so a CD-i target can be named.
* **GOP length (15 frames, ~0.6 s) is not stated on those pages** — it is the
  long-standing practical limit, and both the code and this note say so.
* The television standard follows `--fps` (29.97/30/23.976 = NTSC, 25 = PAL);
  without it the disc defaults to PAL, so a 720×480 NTSC image needs
  `--fps 29.97`.
* The disc is a hard constraint: it overrides `--encoder`, `--container`, size,
  frame rate, bitrate and audio, and says so when it does. `-maxrate/-bufsize/-g`
  travel through `ExtraEncoderArgs`, so they share the v7.9.9.7 VBV pairing.
* The CUI remembers the choice in `config.json`; the CLI flag is per-run.

### Measured on the supplied material

`SJ01_SCN.iso` and `D2_SCN.iso` (DVD-Video PAL, 1501 s / 689 s) are read directly
and encode to 720×576@25 mpeg2video + ac3 48 kHz 384 kbps; `Gamehalo 271.iso`
(NTSC, two title sets) needs `--fps 29.97` and produces 720×480@30;
`西部大案纪实（1）.dat` (VCD 2.0 PAL) encodes to 352×288@25, 1150 kbps, mp2
44.1 kHz 224 kbps; a fake `.iso` exits 2.

**Known limit**: FFmpeg's dvdvideo demuxer reads the **first** title only — an
image with several VTS files (the `Gamehalo` disc) exposes about 21 s of it.
`--start-time/--end-time` crop within that title; selecting another title is not
supported yet.

### Tests

`DiscProfileTests` (9 cases) and `tests/e2e/test_disc.py` (5 cases). Unit suite
**762 passed**.

---

## v7.9.9.7 (2026-11-12) — flv EINVAL, CUI -b:v row, third audit

Fixes a failure reported from a real 46-minute encode, adds the requested `-b:v`
row to the recompress menu, and closes a third round of audit findings.

### 1. The reported bug: `encoder exited before final frame (code -22)`

The run used `--encoder flv` with `tudou/2006` and
`--extra-encoder-args "-maxrate 300k -minrate 128k"`. Parameter elimination against
the same FFmpeg binary gave:

```
-q:v 11 -maxrate 300k -minrate 128k                 => -22
  Either both buffer size and max rate or neither must be specified
-q:v 11 -maxrate 300k -minrate 128k -bufsize 600k   => 0
```

FFmpeg's MPEG-video-family encoders (flv1, h261, …) require `-maxrate` and
`-bufsize` **as a pair** in `avcodec_open2`. libx264 / mpeg4 / libopenh264 accept the
same half-pair, so a blanket rejection would break valid configurations. The 3-frame
preflight never carried `--extra-encoder-args`, so it validated a *different* command
and the real encode died when the first frame opened the encoder.

**Fix**: `FragmentEditor.NormalizeVbvPair` completes the missing half (a missing
buffer = two seconds of maxrate, the same rule the CRF path uses); both
`BuildEncoderCommand` and `Preflight.Video` call it, so the trial now proves the real
command. `ValidateCommonConfig` validates the final value (including one restored from
`config.json`) and rejects a `minrate > maxrate` typo at exit 2. The long-lived encoder
argv is now logged at Info level. Measured: the user's original parameters exit 0 and
produce a valid `.flv`.

### 2. CUI recompress: a `-b:v` row

Row `h. Video bitrate (-b:v)` is separate from row 6 (the codec's quality knob);
they are mutually exclusive. Also fixes `--recompress-video-kbps` being silently
ignored when `--recompress-args` was hand-written.

### 3. Audit fixes

* **S1**: `--parallel N` + `--audio-mode separate/auto` recursed forever via three
  `return Run(o, …)` paths in `RunSplitCore` — now `RunSingle`.
* **S2**: `--iperf3` replaced the source probe with a single sample, forcing jitter and
  latency to zero and enabling sharding on unstable links — iperf3 is a reference only.
* **S3**: `NetworkMeasurement.RejectReason` names the threshold that vetoed sharding;
  a 125.8x-headroom / 51%-jitter link no longer reports "not enough headroom" (six
  new en/zh strings).
* **S5**: stale `.part` cleanup now checks the pid encoded in `PartPath`'s tag, so a
  concurrent process's in-progress product is never deleted on POSIX / UNC.
* **M2/M3**: `Kbps` understands `1M` and an explicit `0`; `Merge` deduplicates flags
  repeated inside extras via `FirstWins`.
* **M9/M8**: the direct-mode frame handshake has a 30 s stall timeout; `Toolchain.RunLong`
  checks cancellation even when both pipes finish inside the first poll window.
* **L1/L2**: an unknown `--long` option is no longer swallowed as a value; `SetFlag`
  does not eat the next token after a value-less switch.

### 4. Tests and known leftovers

`V7997Tests.cs` (12 unit cases) and four new e2e cases (half-pair `flv` produces a
file, impossible bounds exit 2, real FFmpeg argv carries the explicit `-b:v`). Unit
tests **753 passed**.

Deliberately left for a later round with its own test matrix: network-probe cache key
mismatch (S4), the fixed `.rollback` directory (M7), jitter denominator / HTTP length
/ unit constant (M4–M6), KillAll counting / CTS disposal / AppLog fallback (L3–L5).
AOT remains unverified.

---

## v7.9.9.6 (2026-11-09) — second audit: menu promises, subtitle fields, shell survival

Second full-project audit. No default encode parameter changed; v6.3.3 equivalence
is intact. The theme is "a setting the UI accepts must reach FFmpeg, and one bad
answer must not end the shell".

**CUI**

* The OpenH264 dials (rows 32/33) now actually reach `RealtimeOptions`; they were
  accepted by the menu and ignored by the run.
* A hand-typed q:v is marked `QualityExplicit`, so intra-only calibration no longer
  rewrites it. Presets, encoder switches and profile loads reset the flag.
* The era, container and resolution menus all advertised `0 = keep` but only an
  empty line worked; typing 0 produced "unknown era preset '0'", "'0' is not a
  container" or a format error. All three now show a `0. Back` row and accept 0.
* The encoder list says "not probed" instead of "?", and the prompt says `r=back`
  (refresh is main-menu 5).
* An unreadable input directory is reported instead of crashing the shell; a
  directory with more than 200 videos says only the first 200 are shown.
* A `config.json` that exists but cannot be parsed is never overwritten: CUI uses
  `TryLoad`, refuses every write-back and warns at startup. Previously the first
  menu edit replaced the user's real file with defaults.
* Main-menu handlers are wrapped in a `try/catch` (except OOM), so one bad choice
  prints an error and returns to the menu.

**CLI**

* `--subtitle-mode`, `--subtitle-codec`, `--subtitle-font` and
  `--subtitle-font-dir` now take effect on their own. `TouchedSubtitles` only
  looked at files/stream, so `--subtitle-mode off` was ignored and
  `--subtitle-font` was dropped. A single field now merges over the stored block;
  naming a file or stream replaces the selection.
* `--save-config` no longer rebuilds the subtitle block a second time, which used
  to drop the stored font directory/family when a run named a file.
* `--recompress-only` honours `--play`.
* `--help` / `--version` short-circuit later arguments (`--help --bogus` is help,
  exit 0), and `--` ends option parsing for names beginning with `-`.
* `--parallel` with split audio says sharding does not apply and runs one pass;
  the inline fallback now calls `RunSingle` directly instead of recursing in Auto
  mode. `--parallel` with a copied subtitle track also falls back to one pass,
  because each shard would rebase the subtitle timeline.
* Platform-aware path comparison in the CUI output/input check, recompress-only
  and `CorrectExtension` (Linux `A.avi` and `a.avi` are different files).

**Consistency**

* `FirstRun` also refuses to overwrite an unparsable `config.json`.
* `ReadDouble` rejects NaN/Infinity.
* Pre-release notices are localized and shown by both `--update-check` and the CUI
  update menu.
* `PrintConfig` shows realtime/direct mode, OpenH264 rate control/skip frames and
  extra encoder args.

**Tests**: +7 unit tests (`V7996Tests.cs`, 734 → 741), both TFMs green with 0
warnings; e2e 213 passed + 2 skipped (parallel) and 29 passed (serial), 242 passed
/ 2 skipped in total; `tools/check-slnx.py` passes. Live runs with the requested
pwsh covered the 0 rows, the "not probed" column, a corrupt config, the split-audio
parallel note, `--help --bogus`, `--`, and subtitle overrides in `--save-config`.

## v7.9.9.5 (2026-11-09) — full audit, cancellation/concurrency fixes, v8 handover

**v7 enters maintenance mode** (bugfix / hotfix / small features); v8 is a full
rewrite in Rust. All 83 product source files, 52 unit-test files and 23 e2e files
were audited, plus live CLI and CUI runs.

* **Ctrl+C now reaches every child process.** `ProcessRunner.Run` links the
  process-wide interrupt token into every short-lived ffprobe/preflight/download
  call; pipe reads after child exit have a 5 s bound and abandoned tasks are
  observed. Download headers have a 60 s timeout; `RunLong`, `Execute`'s startup
  path, `WriteFrame` and the split-audio staging cleanup no longer orphan
  processes, handles or `%TEMP%` directories. `Program.Main` maps cancellation to
  130 and any other uncaught exception to `ERROR:` + 1.
* **Concurrency.** Capability-cache reads/writes and serialisation share a gate;
  config/cache/shell-profile writes use unique temporary files and atomic rename;
  the console mirror's buffer is locked; toolchain staging directories are unique;
  staging creation moved inside `try`; shard count clamped in the encoder layer.
* **Three-state settings.** `--no-realtime` reaches `Config` (the CUI toggle is
  honoured by the CLI, the CLI flag stays per-run); `H264Impl` no longer turns a
  preset choice into an explicit override; `--no-gh-proxy` survives
  `--emit-args`; `CliArgs.Num` uses `R` so numbers round-trip exactly; explicit
  `--subtitle` files are no longer treated as a one-run discovery.
* ⚠ **CLI contract**: encode options without an input file are now refused with
  exit 2 instead of being silently discarded before the menu opens. ⚠ `--update`
  and `--update-check` together are a usage error. ⚠ Pre-release tags (`-rc1`) are
  reported but never installed over a stable build. ⚠ A split-pipeline failure
  after the video pass has started is reported, not silently replayed inline.
* **Untrusted input.** Release tags and asset names are bounded/sanitised (no
  stack overflow, no drive-relative or ADS paths); URL-derived output names cannot
  become drive letters, dot segments or device names; `--gh-proxy` values are
  validated at parse time and no longer swallow the positional input.
* **Correctness.** Ladder migration runs once per file; broken `config.json` is
  never overwritten with defaults by an update check or toolchain install; POSIX
  path comparisons are case-sensitive; reported average bitrate uses the real
  file size; progress reader drains its tail before cancellation.
* **Completion catalog**: new `OptionalValue` kind, `--recompress-args` is free
  text again, PowerShell value completion reads `CommandElements[-2]`.
* **Tests**: 16 new unit tests (718 → 734), both TFMs green with 0 warnings,
  `test_realmedia.py` split into per-case scratch directories so xdist no longer
  collides on shared `.part` files. Full e2e: 242 passed / 2 skipped. Live smoke
  covers CLI exits, CUI banner+quit, four completion shells, a real encode and a
  `--save-config` → `--emit-args` round trip.
* **v8 preparation**: new `v8-handover.md` / `.en.md` records the frozen behaviour
  contract, the full fix list, the deliberate trade-offs left for the rewrite and
  the golden-baseline prerequisite.

## v7.9.9.4 (2026-11-08) — cleanup, documentation refresh, bilingual docs

* Removed what nothing used: `tools/benchmark.ps1`, `tools/probe_audio_limits.py`,
  four committed release notes under `publish/release/` (GitHub Releases holds the
  authoritative copies), three dead `Recompress`/`PostProcessOutcome` members and a
  self-referential `using`. `.pytest_cache/` is now gitignored.
* Every document ships in Chinese and English (`X.md` / `X.en.md`), with a language
  switcher, an index (`docs/README.md`) and a pair check in `tools/check-slnx.py`.
* Documentation de-duplicated: the tools/docs inventory, the FFmpeg source count
  ("six: five auto-downloadable plus one manual"), the release-asset table and the
  test report each have one home, and the rest link to it.
* `MAINTENANCE` had two stale facts corrected: the log file is `lae.log`
  (size-rolled), not `lae-yyyyMMdd.log`; and the release flow now says to create the
  release first, then upload assets in batches (a four-asset `gh release create`
  can stall).

## v7.9.9.3 (2026-11-08) — audit fixes and CUI generic encoder options

* The CUI finally exposes what only the CLI had: second-pass `b.` `-maxrate`,
  `c.` `-minrate`, `d.` `-bufsize`, `e.` `-preset`, `f.` `-profile:v`, `g.` `-ac`,
  and parameter-menu `36.`–`38.` for the live encoder. Menu `8.` was renamed to
  "Further passes (chain)" because it was being confused with `--recompress-extra`.
* New `PostProcess/FragmentEditor.cs` — one implementation of `SetFlag` / `Merge` /
  `ValidateVbv` / `ValidateBounds` / `FirstWins`, shared by CLI and CUI so the two
  cannot drift.
* ⚠ Fixed a v7.9.9.2 regression: the direct-mode capture thread spun forever once the
  slot loop stopped consuming, holding a frame buffer and burning a core.
* `PostArgs.Validate` now rejects a bare path anywhere in a fragment (FFmpeg would
  have treated it as an extra output and overwritten an unrelated file).
* `Recompress`/`PostPipeline`/`--recompress-only` now honour Ctrl+C end to end.
* Self-update now copies payload subdirectories (`runtimes/`, satellite resources);
  the old top-level-only walk could leave an install that would not start.
* The lock pre-flight only deletes `.part` files older than this process, and only
  closes players it started for that exact file.

## v7.9.9.2 (2026-11-08) — A/V sync in direct mode, era ladder, path memory

* ⚠ Fixed a serious v7.9.9.1 regression: `--no-realtime` produced 3 fresh frames and
  62 repeats out of 65 slots — a still picture over full-length audio. One decoded
  frame per slot now, with the same durations as the realtime mode.
* ⚠ The default FPS ladder changed from `13,8,5,3` (a Python-baseline artefact) to the
  era-standard rungs `[13,12,10,8,5]`; an old `config.json` carrying exactly the old
  value is migrated, a custom ladder is left alone.
* Recent-directory memory (8 entries) and `--output <existing dir>` auto-completion to
  `<stem>_live.<ext>`; the CUI offers the remembered directories as `r1..rN`.
* ⚠ Pre-flight lock check before encoding, stale `.part` cleanup, and a real fix for
  the `(2)` alternate-save path: a Windows sharing violation raises
  `UnauthorizedAccessException`, which v7.9.8.13.2 rethrew, silently discarding a
  finished encode.
* `--recompress-maxrate/-minrate/-bufsize`, `--recompress-extra`,
  `--extra-encoder-args`; the VBV triple is refused without `-b:v`.

## v7.9.9.1 (2026-11-08) — quieter logs, optional ladder, direct compression

* ⚠ Logging defaults to `error`; an ordinary run writes no log, and a failure carries
  the last 200 context events. `LAE_LOG_LEVEL=verbose|information|warning` restores
  the full trace.
* `--fps-levels none` removes the dynamic ladder.
* `--no-realtime` encodes as fast as the machine allows (direct compression) instead
  of replaying the source cadence; the choice is remembered by the CUI (menu `34.`).
* Second-pass bitrate now works for every codec; the CUI gained an audio-bitrate row.
* Fixed a CUI bug that reset `-b:a` / `-ar` / `-ac` to catalog defaults whenever a
  codec was changed.

## v7.9.9 (2026-11-08) — structured logging and explicit disable

* Serilog + `Microsoft.Extensions.Logging`, one correlated log per run
  (`<RunId>/<OperationId>`), console output mirrored byte-for-byte.
* `System.Threading.Channels` (log queue, installer pipes),
  `System.Threading.RateLimiting` (FFmpeg stderr sampling) and `System.IO.Pipelines`
  (line reader) are used in the real paths.
* CUI era menu `x.` clears the preset; recompress menu `9.` deletes the whole chain;
  CLI `--no-era` does both for a run.
* The main project stopped being dependency-free: six package references.

## v7.9.8.13.2 (2026-11-07) — full audit, split changelog

* `docs/CHANGELOG.md` split at v7.9.8; earlier history moved verbatim to
  `CHANGELOG-archive.md`. `docs/MAINTENANCE.md` added.
* Fixed NaN quality-calibration pollution, `Promote` return values being ignored,
  per-run subtitle discovery leaking into `config.json`, incomplete self-update
  rollback, PATH accumulation when switching FFmpeg sources, and dead `StableUrl`.
* `tools/check-slnx.py` added as a CI gate; the `.slnx` manifest was rebuilt
  bidirectionally (it had drifted).

## v7.9.8.13.1 (2026-11-07) — unified toolchain, corrected era presets

* Both platforms use the official `ip7z/7zip` build; five auto-downloadable FFmpeg
  sources plus the manual `avbuild`; `autobuild` resolves `releases/latest`
  dynamically because each commit gets its own tag.
* Era presets were re-derived from archive.org material metadata (`creation_time`,
  `Encoded_Library`, `copyright`) cross-checked against period sources; three rows
  were corrected and the re-encoded sources were graded B (not trusted).
* New `docs/toolchain-builds.md` with codec lists measured on both platforms.

## v7.9.8.13 (2026-11-07) — subtitles

* `--font-pack` (zip/7z/rar via 7-Zip) imports only the fonts an ASS actually names.
* `--auto-subtitle` finds sidecar subtitles named after the source, or takes an
  explicit file.

## v7.9.8.12 (2026-11-07) — five fixes found in use

* ⚠ `.rm` defaults back to `ac3` audio: RealAudio in RM has all-zero packet PTS.
* Self-update selects by runtime flavour (`-aot` / `-self-contained` / none).
* rv10/rv20 dimensions snap to a multiple of 16.
* `Promote` retries, then saves as `name (2).ext`.

## v7.9.8.11.1 (2026-11-07) — MJPEG audio/video, intra quality

* Audio `-t`, `-af asetpts`, separate-mode `-shortest`, and a fixed `CanSplitMux`.
* `IntraQuality` calibrates q:v against measured frame sizes.
* Five-part version numbers.

## v7.9.8.11 (2026-11-06) — MJPEG full range

* `--encoder mjpeg` writes `yuvj420p` (full range) as FFmpeg's own encoder does; it
  used to fail the pre-flight.
* Managed FFmpeg bins are published to the shell PATH after install.

## v7.9.8.10 (2026-11-04) — audit only

* Five read-only audits over correctness, resources/lifetimes, config round-trips,
  concurrency and UI text; no new features.

## v7.9.8.9 (2026-11-04) — three defaults in the wrong place

* ⚠ An unusable extension is corrected instead of refused; the corrected name never
  lands on the source or on a file nobody named.
* Network sources no longer drag their output onto the same share.

## v7.9.8.8 (2026-11-04) — OpenH264 knobs

* `--openh264-rc` reached the live pass but not the second pass; `-allow_skip_frames`
  was wired backwards. Both fixed.

## v7.9.8.7 (2026-11-04) — update check and PGS

* The update check used the wrong endpoint and always failed.
* PGS subtitles inside a container could not be burned in.

## v7.9.8.6 (2026-10-28) — robustness pass

* Fifteen crash/loss/leak defects fixed; e2e switched to a private
  `LAE_CONFIG_DIR` so it no longer touches the developer's settings.

## v7.9.8.5 (2026-10-28) — self-update

* The tool can replace itself; version tagging and the development-time update path
  added.

## v7.9.8.4 (2026-10-27) — one report, three fixes

* A user-reported failure plus two defects found while fixing it.

## v7.9.8.3 (2026-10-27) — NNEDI3, proxy front, chains

* NNEDI3 deinterlacing; GitHub downloads can go through a proxy front; the
  recompress chain runs every hop instead of only the first.

## v7.9.8.2 (2026-10-27) — era presets describe more

* ⚠ Era presets now carry live quality, window, buffer and deinterlace settings, not
  just resolution and bitrate.

## v7.9.8.1 (2026-10-27) — RealMedia and `.part`

* RealMedia output matches period files; the second pass also writes `.part`.

## v7.9.8 (2026-10-27) — code review, no new features

* The whole codebase reviewed for "what can go wrong on a user's machine"; fixes
  only.
