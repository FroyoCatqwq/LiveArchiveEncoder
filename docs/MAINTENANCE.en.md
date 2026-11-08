# Maintenance guide

[English](MAINTENANCE.en.md) · [中文](MAINTENANCE.md)

For whoever maintains v7: how the tree is split, which commands to run, what must
be checked after a change, and how a release is cut. User-facing documentation is
[`usage.en.md`](usage.en.md); history is
[`CHANGELOG.en.md`](CHANGELOG.en.md) / [`CHANGELOG-archive.en.md`](CHANGELOG-archive.en.md).

## 1. Directory responsibilities

| Path | Holds | Must not hold |
| --- | --- | --- |
| `src/LiveArchiveEncoder/` | All product code, split into `Encoding` / `FFmpeg` / `UI` / `Configuration` / `Core` / `PostProcess` / `Update` / `Remote` / `RateControl` / `Frames` / `Diagnostics` / `Localization` | Generated files, one-off scripts |
| `tests/LiveArchiveEncoder.Tests/` | TUnit unit tests (pure logic, offline) | Cases needing real FFmpeg or wall-clock timing |
| `tests/e2e/` | pytest end-to-end (real encoders, real files, CUI interaction) | Pure functions the unit suite already covers |
| `tools/` | Build, e2e orchestration, capability probes, manifest check | Product logic |
| `docs/` | Current specifications and archived records | Scratch analysis notes |
| `reference/` | v6.3.3 Python reference (**verbatim**, comparison only) | Any edit |
| `archive/` | Retired code, explicitly labelled | Anything still in use |
| `publish/`, each `bin/` `obj/` `TestResults/` | Generated, excluded by `.gitignore` | Anything committed |

The Solution Items in `LiveArchiveEncoder.V7.slnx` are an IDE list: **file entries
plus two `.csproj` references only**, no build settings. After adding or removing a
file, regenerate and verify it — see §3.

## 2. Common commands

```bash
dotnet build LiveArchiveEncoder.V7.slnx -c Release
dotnet test --solution LiveArchiveEncoder.V7.slnx -c Release --no-build
dotnet build LiveArchiveEncoder.V7.slnx -c Release -p:LaeTfm=net11.0   # second TFM
tools/build.ps1            # Windows: produce publish/jit
bash tools/build.sh        # Linux: the same
python tools/check-slnx.py # verify the IDE manifest has not drifted
python tools/e2e.py        # full e2e (two phases, see §4)
```

`--no-build` requires a preceding `build`; this matters most right after adding a
test file. The target framework comes from `LaeTfm` in `Directory.Build.props`
(default `net10.0`); the apphost sets `RollForward=Major`, so one
framework-dependent build starts on a machine that only has .NET 11. `global.json`
declares the MTP test runner only — it does **not** pin the SDK.

## 3. Post-change checklist

1. `dotnet build` and `dotnet test` once per platform (Windows and Linux).
2. `python tools/check-slnx.py` — after adding or removing any `.cs` / `.py` / `.md`
   / script, regenerate the Solution Items first, then verify, so the manifest
   cannot drift silently.
3. When touching encoder parameters, preset values or toolchain ids, update the
   matching hard-coded unit assertions and e2e expectations.
4. When touching localisation, the en/zh key sets in `Strings.cs` and their
   placeholder counts must match (a unit test enforces it).
5. When touching CLI options, `CliOptions` (local variable / switch / construction /
   `Usage()`) and `OptionCatalog.All` must move together (a bidirectional unit test
   enforces it).
6. When touching the configuration model, confirm an old `config.json` still loads
   and that `ConfigStore.Capture/Apply` and `CliArgs.FromUserConfig` round-trip
   without losing fields.
7. When touching self-update, verify with `--update-check`; asset names must match
   `AssetSuffix()`'s three forms exactly (`-aot` / `-self-contained` / no suffix).

## 4. Test layers and configuration isolation

* **Unit tests** need no FFmpeg and no real clock; they must be green everywhere.
* **e2e**: `tools/e2e.py` runs two phases — the `timing` and `shared_config` groups
  **serially**, everything else under `pytest -n`. The guard in
  `tests/e2e/conftest.py` refuses `-n` together with an exclusive group.
* **Config and font directories are isolated end to end**: `harness.py` points
  `LAE_CONFIG_DIR` at the suite's private directory, fonts at `LAE_FONT_DIR`.
  **No test may write the developer's real `config.json`** under
  `%LOCALAPPDATA%` / `~/.local/share`.
* Cases needing real FFmpeg get it from `apt install ffmpeg xz-utils` on Linux CI;
  the full legacy-encoder sweep still runs by hand on both platforms, because
  distribution FFmpeg does not ship those encoders.

## 5. Documentation naming and link contract

| File | Scope |
| --- | --- |
| `docs/README.md` | Index of every document with both language editions |
| `docs/CHANGELOG.md` | Current history, **v7.9.8 onwards**. New entries go after the header block, before the first `## v…` |
| `docs/CHANGELOG-archive.md` | **v7.9.7 and earlier** (including the v6.2.2 → v6.3.3 baseline). Archive only, never rewritten |
| `docs/usage.md` | Current feature documentation; version-prefixed paragraphs describe the release they were written in and must move when behaviour moves |
| `docs/era-presets.md` | Evidence for the era presets, source-credibility grading, correction list |
| `docs/toolchain-builds.md` | FFmpeg / 7-Zip build sources and codecs measured on both platforms |
| `docs/requirements.md` | System and hardware requirements |
| `docs/capability-matrix.md` | Container × frame-rate × pixel-format measurements (archived snapshot) |
| `docs/test-report-v7.en.md` | Measured port-acceptance report (archived, English original) |
| `archive/README.en.md` | Rule for retiring code and the current retired table |

**Naming and links (v7.9.9.4):**

* `X.md` is Chinese, `X.en.md` is English. The two are committed together; the
  first line of each is a language switcher.
* An English page's relative links point at `*.en.md`; a Chinese page's point at
  `*.md`. Mixing them drops the reader back into the other language after one click.
* `tools/check-slnx.py` verifies that every `docs/*.md` has a matching
  `docs/*.en.md` (and vice versa), so a half-translated pair fails CI.
* Historical documents keep Chinese as the authoritative full text; the English
  edition is a per-version summary that links back to the Chinese original.

The split criterion is the **version number**, not a date or a line count. Numbers,
links and conclusions inside historical entries stay verbatim; read them in their
period context. What needs correcting is current documentation and code comments.

## 6. Release flow (required after every change)

1. Set `Constants.Version` to the target version; add a `docs/CHANGELOG.md` entry
   (and the English summary in `CHANGELOG.en.md`).
2. Build and test both platforms (§2, §3).
3. Produce **four JIT packages** — Windows / Linux × (framework-dependent /
   self-contained):

   ```
   LiveArchiveEncoder-v<version>-win-x64.zip
   LiveArchiveEncoder-v<version>-win-x64-self-contained.zip
   LiveArchiveEncoder-v<version>-linux-x64.tar.gz
   LiveArchiveEncoder-v<version>-linux-x64-self-contained.tar.gz
   ```

   Names must match `SelfUpdate.AssetSuffix()` exactly, or users update to the wrong
   runtime flavour.
4. Write `publish/release/notes-<version>.md` with the four SHA-256 digests.
5. `git add -A` → commit → `git -c http.proxy=http://127.0.0.1:10808 push origin main`
   (a direct connection is often reset; use a temporary option, never change the
   git config).
6. `gh release create v<version> <four assets> --notes-file … --target main`.
   Large uploads can stall: create the release first, then upload assets in batches.
7. Verify: each asset's GitHub `digest` equals the local SHA-256, the release is not
   a draft, `--update-check` returns 0, the tag is fetched, the work tree is clean.
8. **Restore the developer's real configuration file** if an unisolated run touched
   it, and compare its hash with the pre-work backup.
9. Delete one-off scripts and packaging leftovers from the working directory.

CI (`.github/workflows/ci.yml`) only builds and runs unit tests on both platforms,
plus the manifest check and the net11 target; e2e stays out because it needs a real
clock and legacy encoders.

## 7. Run log and diagnostics (v7.9.9)

Every run writes a rolling log: `<data root>/logs/lae.log`, size-rolled at 16 MB with
7 files retained. **It is not inside `LAE_CONFIG_DIR`** — that is the user's settings
directory; the log has its own `LAE_LOG_DIR`.

| Variable | Effect |
| --- | --- |
| `LAE_LOG_DIR` | Custom log directory; e2e's `harness.py` points it at the suite's private root |
| `LAE_LOG_LEVEL` | `verbose` / `information` / `warning` / `error` — **`error` by default** (since v7.9.9.1 an ordinary run writes nothing; on failure the last 200 context events are attached) |
| `LAE_LOG_MIRROR=0` | Do not mirror console output into the log |
| `LAE_LOG=0` | Disable logging entirely |

Line format: `<ISO time> [level] <RunId>/<OperationId> <message>`. `RunId` is unique
per process; `OperationId` advances on every `Interrupt.BeginOperation()` (i.e. every
encode).

Follow the chain with: `run.start` → `ffmpeg.discovered` → `child.spawn` /
`child.exit` (full argv, exit code, elapsed, last 400 chars of stderr) →
`output.promoted` → `process.exit`. Every line the user sees is recorded as
`ui.stdout` / `ui.stderr`. `AppLog.Sampled` samples high-frequency FFmpeg stderr at
40 tokens/second and reports how many lines it suppressed; key events are never
sampled.

**Implementation constraints** (read before touching logging code):

* The `Console.*` calls are user-visible output and e2e asserts their exact text.
  The log mirrors them through a `TextWriter` decorator — **do not add lines, do not
  change the encoding**; Serilog gets no Console sink.
* Realtime encoding is paced by a high-resolution clock. The calling thread only does
  `Channel.Writer.TryWrite`; a single background consumer writes to disk. Any
  synchronous file I/O or lock changes the pacing.
* If log initialisation or writing fails, it degrades to the temp directory and then
  silently disables itself — it must never interrupt an encode.
* Credentials in the form `LAE_UPDATE_TOKEN` and `user:pass@host` must stay redacted.
* A new `--log-*` CLI option would also need `CliOptions` (local variable / switch /
  construction / `Usage()`) and `OptionCatalog.All`; today everything is
  environment-only.

## 8. Dependencies and AOT

Since v7.9.9 the main project is no longer dependency-free: `Serilog` /
`Serilog.Extensions.Logging` / `Serilog.Sinks.*` / `Microsoft.Extensions.Logging` /
`System.Threading.RateLimiting` are explicit `PackageReference`s.
`System.Threading.Channels` and `System.IO.Pipelines` ship with the net10.0 shared
framework — an explicit reference raises NU1510, so **do not add one**.

`nuget.config` clears to a single source (nuget.org) and no proxy is committed; a
machine that needs one uses `HTTP_PROXY` or a user-level `NuGet.Config`.
`dotnet restore` requires network access; offline builds need a pre-seeded cache.
