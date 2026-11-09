# Version history (v7.9.8 onwards) — English summary

[English](CHANGELOG.en.md) · [中文](CHANGELOG.md)

This is a **per-version summary**. The authoritative full record is the Chinese
edition, [`CHANGELOG.md`](CHANGELOG.md) — entries there are kept verbatim, with the
measurements and reasoning behind each decision. `⚠` marks a change a user can
notice in an output, a default, a file name or a CLI contract.

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
