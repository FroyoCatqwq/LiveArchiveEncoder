# Documentation index

[中文](README.md) · **English**

Every document ships in two editions: `X.md` is Chinese, `X.en.md` is English. Each file
starts with a language switcher. Historical records (`CHANGELOG*`) keep Chinese as the
authoritative full text and provide an English summary — see
[Language policy](#language-policy).

| Document | What it covers |
| --- | --- |
| [`usage.en.md`](usage.en.md) | Full user guide: CLI, CUI, encoder tables, rate control, parallel encoding, configuration, completion, remote sources, FFmpeg toolchain, playback, recompress chain, era presets, subtitles, pixel formats, self-update, troubleshooting, self-test |
| [`requirements.en.md`](requirements.en.md) | OS / CPU / memory / disk / fonts / permissions / network, measured environments, performance expectations, unsupported setups |
| [`era-presets.en.md`](era-presets.en.md) | 32 presets (5 `generic/<year>` plus 27 `site/year` rows across 13 sites), evidence grading, q:v / CRF calibration, drop/rise windows, old id aliases |
| [`toolchain-builds.en.md`](toolchain-builds.en.md) | Six FFmpeg sources (five auto-downloadable) plus unified 7-Zip, codec differences measured on both platforms |
| [`architecture.en.md`](architecture.en.md) | Technical description: architecture and process model, core mechanisms (pacing, frame pool, ladder, rate control, sharding, post-processing), strengths and weaknesses, v8 (Rust) migration design |
| [`v8-handover.en.md`](v8-handover.en.md) | Frozen v7 baseline: behaviour contract to inherit, the v7.9.9.5 / v7.9.9.6 audits, trade-offs left to v8, golden-baseline acceptance criteria |
| [`MAINTENANCE.en.md`](MAINTENANCE.en.md) | Directory responsibilities, commands, post-change checklist, documentation contract, release flow, log and diagnostics |
| [`capability-matrix.en.md`](capability-matrix.en.md) | Container × frame-rate × pixel-format measurements (v7.9.6 snapshot, archived) |
| [`test-report-v7.en.md`](test-report-v7.en.md) | Measured v7 port-acceptance report (archived, English original) |
| [`CHANGELOG.en.md`](CHANGELOG.en.md) | Per-version summary for v7.9.8 → current, with behaviour-change markers |
| [`CHANGELOG-archive.en.md`](CHANGELOG-archive.en.md) | Version index for v6.2.2 → v7.9.7 |
| [`../archive/README.en.md`](../archive/README.en.md) | Rule for archiving retired code and the current retired table |

Landing pages: [`../README.md`](../README.md) (English) ·
[`../README.zh.md`](../README.zh.md) (Chinese).

## Language policy

* **Naming**: `X.md` is Chinese, `X.en.md` is English. An English page's relative links
  point at `*.en.md`; a Chinese page's point at `*.md`. Mixing them drops the reader into
  the other language after one click.
* **Switching**: the first line of every file is `[English](X.en.md) · [中文](X.md)`
  (ordered by that file's main language).
* **Historical documents**: `CHANGELOG.md` / `CHANGELOG-archive.md` keep Chinese as the
  single authoritative record; entries are preserved verbatim and never rewritten. The
  English edition is a per-version summary with `⚠ behaviour change` markers, linking back
  to the Chinese original for detail.
* **Archived documents** (`capability-matrix`, `test-report-v7`) are explicitly marked
  `> Archived` so nobody mistakes historical readings for current rules.
* **One exception**: the root `README.md` is English because it is the repository landing
  page; its Chinese edition is `README.zh.md`. Every other document follows
  `X.md` / `X.en.md`.
* **Enforcement**: `tools/check-slnx.py` verifies that every `docs/*.md` has a matching
  `docs/*.en.md` (plus `archive/README.md` and the root `README.md`); a half-translated
  pair fails CI.
