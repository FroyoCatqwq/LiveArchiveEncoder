# Version history (v6.2.2 → v7.9.7) — English index

[English](CHANGELOG-archive.en.md) · [中文](CHANGELOG-archive.md)

The authoritative full record for these versions is the Chinese edition,
[`CHANGELOG-archive.md`](CHANGELOG-archive.md). It is **archived, not translated**:
the entries carry period-specific measurements and judgments that must stay
verbatim, and rewriting them would destroy their value as a record. This page lists
every version so an English reader can locate one and follow the link.

## Milestones

| Version | What it was |
| --- | --- |
| v6.2.2 → v6.3.3 | The Python era. v6.3.3 is the porting baseline, kept verbatim in `reference/live_archive_encoder.py` |
| v7.0 → v7.2 | The C# port itself: realtime pipeline, adaptive FPS ladder, sharding, and the six baseline defects deliberately fixed |
| v7.3 | First C# baseline |
| v7.6 | The tool starts growing past the port: era presets and the video-site recompress pass, both off by default |
| v7.8.1 | Stops shipping the `-p:IlcInstructionSet=avx2` AOT flavour (no measurable gain, AVX2-only exe) |
| v7.9.5 | `--recompress-only`: skip the live pass and transcode an existing file |
| v7.9.6.3 | Platform floors lowered to what .NET 10 supports (Windows Server 2019, glibc 2.28), decided by the test machines |

## Every version

| Version | Date |
| --- | --- |
| v7.9.7 | 2026-10-27 |
| v7.9.6.3 | 2026-10-25 |
| v7.9.6.2 | 2026-10-24 |
| v7.9.6.1 | 2026-10-24 |
| v7.9.6 | 2026-10-24 |
| v7.9.5.1 | 2026-10-24 |
| v7.9.5 | 2026-10-23 |
| v7.9.4 | 2026-10-22 |
| v7.9.3.1 | 2026-10-22 |
| v7.9.3 | 2026-10-22 |
| v7.9.2 | 2026-10-22 |
| v7.9.1 | 2026-10-21 |
| v7.9.0 | 2026-10-21 |
| v7.8.3 | 2026-10-20 |
| v7.8.2 | 2026-10-20 |
| v7.8.1 | 2026-10-19 |
| v7.8.0 | 2026-10-19 |
| v7.7.0 | 2026-10-19 |
| v7.6.0 | 2026-10-18 |
| v7.5.0 | 2026-10-19 |
| v7.4.1 | 2026-10-18 |
| v7.4.0 | 2026-10-18 |
| v7.3 (C# baseline) | — |
| v7.0 → v7.2 (port) | — |
| v6.3.3 (Python baseline) | — |
| v6.2.2 → v6.3.3 (Python era) | — |

For anything after v7.9.7, see [`CHANGELOG.en.md`](CHANGELOG.en.md), which links
each summary back to the full Chinese entry.
