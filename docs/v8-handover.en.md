# v8 handover (the frozen v7 baseline)

**English** · [中文](v8-handover.md)

Baseline version: **v7.9.9.5** (2026-11-09).

This is the handover point between v7 and v8. It does not repeat the architecture
description (see [`architecture.en.md`](architecture.en.md)); it answers four
questions only: **what state v7 is frozen in, which behaviours must be inherited,
which problems are deliberately left to the rewrite, and what counts as
acceptance**.

---

## 1. Status

| | v7.9 (current) | v8 (planned) |
| --- | --- | --- |
| language / runtime | C# / .NET 10 + .NET 11 (JIT, NativeAOT, self-contained) | Rust, full rewrite |
| goal | bugfix / hotfix / small features | structural redesign |
| behaviour baseline | Python v6.3.3 reference + this repo's e2e | must match v7.9.9.5 item by item |
| structural changes | **no longer accepted** | all of them happen here |

v7.9.9.5 was a whole-project audit (83 product source files, ~29.5k lines; 52
unit-test files; 23 e2e files). It fixed process lifetime, cancellation,
concurrency, three-state settings and untrusted-input defects and added regression
tests. The full list is the v7.9.9.5 entry in [`CHANGELOG.en.md`](CHANGELOG.en.md)
(summary) and [`CHANGELOG.md`](CHANGELOG.md) (authoritative).

**Why freeze now**: many v7 constraints (`-threads` position, realtime pacing, the
rate-control formula, the giant UI classes, global state) are part of the v6.3.3
equivalence contract. Changing them inside v7 would remove the basis for "default
output equals the reference"; changing them in v8 requires a golden baseline first.

---

## 2. Frozen behaviour contract (v8 must inherit)

Every item below is backed by code or an e2e assertion. Changing one changes the
product definition and must be registered explicitly in the baseline.

### 2.1 Time and frames

1. Realtime mode: the capture side emits a frame every `1/fps`, the output loop
   consumes a slot every `1/fps`; when overdue it **resets the baseline instead of
   catching up**; wall time ≈ media duration.
2. Dropping fps means **repeating the previous frame**, not discarding it; the
   container always runs at `--fps`.
3. `slots` / `fresh` / `repeated` are reported; sharded runs sum them.
4. Ladder: rung 0 is the base rate with `DropAbove = ∞`; hysteresis 0.18 / 0.30 /
   0.75 / 0.80 s; threshold factors 1.25 / 1.10;
   `cadence += effectiveFps / baseFps`.
5. Rate observation: `-progress` `total_size` / `out_time_us`; samples expire by
   **wall clock**, the rate denominator prefers **media time** (§4.1 — a known
   mismatch that only v8 may change).
6. `--no-realtime` pins a single rung, removes both pacing points and makes the
   capture thread and slot loop one-frame-per-slot.

### 2.2 Commands and products

7. **argv order is the contract**: `-threads` before the raw-video input;
   `-f rawvideo` → `pipe:0`; `-progress pipe:2`; `-flush_packets 1`;
   `-f MUXER -y <part>`. Floats use `G6` (encoder args) or `R` (settings
   round-trip), always `InvariantCulture`.
8. Output transaction: write `clip.<pid><rand>.part.<ext>`, `Promote` on success;
   retry four times when the target is held, then fall back to `name (2).ext`;
   `Keep` and print the path when the encode finished but the rename failed.
9. Recompress chain: `_web` / `_web2` / `_web3`, each hop reads the previous one's
   product, and a failure never touches the live product.
10. Extension correction, container inference from the extension, the automatic
    output directory for network sources, URL filename sanitisation.
11. Exit codes: `0` success / `1` run failure / `2` usage / `3` update available /
    `130` cancelled; the `ERROR:` and `WARNING:` prefixes are part of the e2e
    assertions.
12. `--help` text, CUI menu numbering and every printable string (the en and zh
    tables must have identical key sets).

### 2.3 Configuration and distribution

13. `config.json` v6 must load as-is; unknown fields are ignored; a CLI write-back
    changes only what this run was told about (`--no-realtime` is per-run,
    `--save-config` is an explicit snapshot).
14. Three states — unset / empty string / value — must survive for `GhProxy` and
    `H264Impl`.
15. Presets are fallbacks, not overrides; 32 built-in `site/year` + `generic/year`
    rows, old id aliases, user `CustomEras` override rule.
16. Four release archives: `LiveArchiveEncoder-v<ver>-win-x64.zip`,
    `-win-x64-self-contained.zip`, `-linux-x64.tar.gz`,
    `-linux-x64-self-contained.tar.gz`, each with SHA-256. The `-self-contained`
    suffix must never be matched by the updater's bare-suffix rule.
17. Self-update selects an asset by the three `AssetSuffix()` flavours
    (bare / `-self-contained` / `-aot`), accepts https only (loopback http for
    tests), verifies `sha256:`, runs the staged binary's `--version`, replaces in
    two phases and never downgrades.

---

## 3. What the audit actually did

| Phase | Content | Result |
| --- | --- | --- |
| static review | two independent read-only reviews (encode core/pipes; UI/CLI/config/update/network), each finding required a file:line and a trigger | 45 confirmed issues + 6 pending |
| live runs | `--version` / `--help` / `--cpu-info`, no-input and mutually exclusive switches, proxy positionals, four completion shells, CUI enter+quit, a real direct encode (901,294 B product), `--save-config` → `--emit-args` round trip | all as expected |
| unit tests | TUnit, both TFMs (`net10.0` / `net11.0`) | 734 / 734, 0 warnings |
| e2e | `tools/e2e.py` two phases (timing / shared_config serial, rest under xdist) | 242 passed / 2 skipped |
| gates | `tools/check-slnx.py` manifest drift + bilingual pairing | pass |

Fix themes: **cancellation reaching every short-lived child**, **bounded pipe reads
after child exit**, **capability-cache concurrency**, **three-state settings no
longer flattened**, **untrusted feed/URL names cleaned before they become paths**,
**atomic writes for temp dirs and shell startup files**.

---

## 4. Problems deliberately left to v8

These were **not** changed in v7.9.9.5, for one of three reasons: they would break
equivalence, they need structural work, or there is no verifiable criterion. The
rewrite must give an explicit "change / keep" decision for each.

### 4.1 Would change output bytes

| Item | Present behaviour | Why v8 |
| --- | --- | --- |
| `OutputMonitor` | samples expire by wall clock, rate denominator prefers media time; the first sample without a media delta still adds its bytes | changing it shifts ladder decisions, frame selection and the product. v7 has no golden baseline to prove "better" |
| `-threads` position | before the raw-video input, as in v6.3.3 | FFmpeg scoping means it may not reach the output encoder; benchmark per encoder first |
| `--audio-mode separate` | slower than inline and changes frame selection | part of the v6.3.3 semantics; v8 should decide whether the mode exists at all |
| intra-only calibration | measured baseline ×1.15, ladder only above 3× the ceiling | constants come from one measurement; v8 should turn them into a benchmarked experiment |

### 4.2 Need structural work

| Item | Present behaviour | What v8 should do |
| --- | --- | --- |
| CUI vs CLI encode options | no input + encode options is now an **explicit error** (v7.9.9.5), because CUI rebuilds state from `config.json` and never sees `CliOptions` | both front ends build one `Plan`; the menu can actually accept CLI pre-sets |
| giant files | `Cui.cs` 3,641, `RealtimeEncoder.cs` 1,562, `CliOptions.cs` 1,561, `Cli.cs` 1,309 lines | split by crate/module; make the main loop a unit-testable state machine |
| global mutable state | `EraPresets.CustomRows`, `BuildProfileStore.Memory`, `L`'s language, the `AppLog` singleton, the timer's static count | explicit per-run context; two configurations in one process |
| four overlapping models | `CliOptions` → `Config` → `RealtimeOptions` → `UserConfig`, precedence scattered over dozens of `?:` | one `Plan` + one `resolve` |
| exceptions as control flow | `InvalidOperationException` covers FFmpeg failures, config conflicts and child anomalies; only five exit codes | `thiserror` variants, stable exit-code mapping, `--json` NDJSON events |
| no port abstractions | `Execute` calls `Process.Start`; only real e2e covers the main loop | `trait ProcessRunner / FrameSource / Clock` with fakes |
| config migration | only a one-shot `LadderMigrated` flag, no migration chain | `schema_version` + explicit `migrate_*` + golden files |
| parallel quota | `Parallel.For` starts N shards (2 FFmpegs + 1 capture thread each), no backpressure | shared `Semaphore`, CPU/IO budget, load detection in realtime mode |
| observability | text logs and sampled stderr, no metrics/tracing | `tracing` spans (run/slot/shard) plus structured metrics |
| localisation | dictionary + `{0}`, 663 direct `Console.*` writes | one terminal layer (width/colour/plurals/machine-readable output) |

### 4.3 No verifiable criterion yet

| Item | Note |
| --- | --- |
| 7-Zip NSIS `/D="dir"` | FAQ and NSIS docs disagree about quoting; no reproducible failure here. v8 can unpack with a crate instead of running an installer |
| `Toolchain.CopyInto` | per-file `File.Copy` is not transactional; a mid-way failure leaves a mixed `bin/`. v8 should extract to a temp dir and swap atomically |
| `Probe.Source` first video stream | an attached_pic cover without `width` may throw `KeyNotFoundException`; needs a crafted fixture |
| `ShardPlanner` float tail | an early `break` pins the last `End` to `rangeEnd` but leaves `Slots`; needs a floating-point boundary case |
| update trust model | only the release JSON's `sha256`, no code signing, no automatic rollback, no `.old`; redesign for enterprise distribution |
| Linux NativeAOT / glibc | AOT output is bound to the build host's glibc; v8 must decide how static Rust linking meets a dynamic FFmpeg |
| pre-release comparison | `VersionTag.Parse` deliberately drops `-rc1` / `+build` (pinned by unit tests); v7.9.9.5 only refuses installation in `Install`. v8 should implement real SemVer precedence |

---

## 5. Prerequisite: the golden baseline

**Without this, v8 cannot be accepted.** Build it before the first crate:

1. **argv snapshots** — record `decCmd` / `encCmd` / `preflight` / `recompress`
   vectors from v7.9.9.5 for every encoder × preset × option combination,
   including order and float text. Store as `insta`/JSON snapshots; make them v8's
   first test group.
2. **Product fingerprints** — container structure (`ffprobe` stream/format fields),
   duration, video packet count, keyframe count, pixel format, bitrate band,
   `fresh`/`repeated`/`slots`. No byte comparison (encoder versions change), only
   structure and statistics.
3. **CLI contract snapshots** — full `--help`, the exit-code table, `ERROR:` /
   `WARNING:` prefixes, `--emit-args` round trips, four completion scripts.
4. **Configuration snapshots** — v6 `config.json` load/save, three-state fields, old
   id aliases, corrupt-file behaviour.
5. **Parallel run period** — v7 and v8 execute the same e2e suite; every difference
   must be explained. "The new implementation is better" is not an explanation.

`architecture.en.md` §8 gives a crate layout and a C#→Rust mechanism map; use it as
a starting point, but build the baseline first.

---

## 6. Suggested acceptance criteria

* Golden baseline 100% identical, or every difference has a change record and a
  reason.
* Unit tests cover the encode main loop (v7 could not; v8 must), e2e still green.
* Realtime and Batch are two first-class modes with separate baselines, not a
  `--no-realtime` patch.
* Cancellation has automated tests for Ctrl+C, timeouts, a hung child and an
  unclosed pipe — checking the process tree, temp dirs, `.part` files and exit code.
* Release: four-archive naming, SHA-256, `AssetSuffix()` matching and the full
  `--update-check` chain verified.
* Documentation: v8's own architecture description plus a diff table against this
  file.

---

## 7. Key files added/changed in the audit

| Purpose | File |
| --- | --- |
| startup, exit codes, update mutex, no-input refusal | `src/LiveArchiveEncoder/Program.cs` |
| child cancellation and pipe teardown | `FFmpeg/ProcessRunner.cs` |
| main loop, sharding, subtitle plan ownership | `Encoding/RealtimeEncoder.cs`, `ParallelEncoder.cs`, `ShardPlanner.cs` |
| capability cache concurrency | `FFmpeg/BuildProfileStore.cs` |
| three-state settings and snapshots | `Configuration/UserConfig.cs`, `UI/Cli.cs`, `UI/CliArgs.cs`, `UI/CliOptions.cs` |
| completion single source of truth | `UI/OptionCatalog.cs`, `UI/Completion.cs` |
| update safety | `Update/SelfUpdate.cs`, `Update/UpdateState.cs` |
| toolchain and shell profiles | `FFmpeg/Toolchain.cs`, `ToolchainPath.cs`, `SevenZip.cs` |
| regressions | `tests/LiveArchiveEncoder.Tests/V7995Tests.cs`, `tests/e2e/test_realmedia.py` |

Process and release flow: [`MAINTENANCE.en.md`](MAINTENANCE.en.md). Full fix record:
[`CHANGELOG.md`](CHANGELOG.md). Architecture and weaknesses:
[`architecture.en.md`](architecture.en.md).
