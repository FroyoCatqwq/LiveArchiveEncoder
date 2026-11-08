# Measured capability matrix (v7.9.6 snapshot, archived)

[English](capability-matrix.en.md) · [中文](capability-matrix.md)

> **Archived.** These are v7.9.6 measurements, kept as evidence of *how* the
> capability lists were derived. The living numbers are in
> `Core/PixelFormatCatalog.cs` and `Constants.ContainerChoicesByEncoder`, pinned by
> unit tests. For the port's acceptance results see
> [`test-report-v7.en.md`](test-report-v7.en.md).

What `ffmpeg -h encoder=NAME` declares is not necessarily true. This project has been
bitten by that three times (mpeg1video frame rates and pal8 frame length in v7.9.4,
RealMedia integer frame rates in v7.9.5). So since v7.9.5 the declaration table is not
trusted: `tools/capability_sweep.py` actually encodes every combination of
**container** and **frame rate**.

## How it was measured

```bash
python tools/capability_sweep.py > /tmp/capsweep.json
```

For every legacy encoder:

1. **Containers**: encode a 0.6 s `testsrc` into the encoder's default container plus
   avi / matroska / mov / mp4 / mpeg / flv / asf / ogg / rm / mpegts / webm, then
   verify three ways:
   - the muxer accepts it (exit 0, non-empty output);
   - `ffprobe` **reads it back under the encoder's own name** (`codec_name` must match);
   - one frame's luma/chroma is measured (`signalstats` YAVG/UAVG) to confirm the
     picture still exists.

   Steps 2 and 3 are mandatory. Testing only "the muxer accepts it" produces a fake
   table:

   | Combination | Mux-only | Truth |
   | --- | --- | --- |
   | `rv20 → avi` | succeeds | the file stores `rawvideo` (AVI has no RV20 fourcc); probing does not yield rv20 at all |
   | `mpeg4 → rm` | succeeds | rm writes the codec tag as `rv20` while the payload is MPEG-4; a player decodes it as RealVideo → garbage |
   | `h261 → mpeg` | succeeds | probes as `codec=unknown`, `nb_frames` does not match |
   | `msrle → matroska` | succeeds | **picture is entirely black** (YAVG 16 vs 123 in AVI) — Matroska stores VfW encoders as `V_MS/VFW/FOURCC` + `BITMAPINFOHEADER`, and that header has no palette |
   | `msrle → asf` | succeeds | same, also black |

2. **Frame rates**: 13 values (5 / 10 / 12 / 15 / 20 / 23.976 / 24 / 25 / 29.97 / 30 /
   50 / 59.94 / 60), encoded once into the default container and once into AVI,
   reading `avg_frame_rate`. A fractional request rewritten to another value is
   recorded as `x->y`.

   `avg_frame_rate` rather than `r_frame_rate`: for MPEG-1/2 `r_frame_rate` is the
   **field rate** (a 25 fps file reads as 50/1). That is ffprobe's MPEG-1 heuristic,
   not what the encoder did.

3. The raw `.h261` / `.h263` / `.a64` containers **store no timestamps**, so the frame
   rate is only meaningful in AVI — which is why `Constants.RateLessFormats` exists
   (the tool prints a note).

Environment: Windows 11, `ffmpeg version N-127941-g303510f9d1-20261022` (BtbN GPL
build), 2026-10-23.

## Containers (pairs passing all three checks)

| Encoder | Default | Passes | Note |
| --- | --- | --- | --- |
| `mpeg1video` | mpeg | mpeg, avi, **matroska, mov, mp4**, asf, mpegts | Before v7.9.5 only mpeg + avi |
| `mpeg2video` | mpeg | same | same |
| `mpeg4` | avi | avi, **matroska, mov**, mp4, mpeg, asf, mpegts | Before v7.9.5 only avi + mp4 |
| `libxvid` | avi | avi, **matroska, mov**, mp4, mpeg, asf, mpegts | same |
| `libx264` | mp4 | mp4, avi, **matroska, mov, flv**, mpeg, asf, mpegts | Before v7.9.5 only mp4 + avi + mkv |
| `libtheora` | ogg | ogg, **matroska, avi**, mov, asf | |
| `flv` | flv | flv, **avi, matroska**, mov, asf | |
| `mjpeg` | avi | avi, **matroska, mov, mp4**, asf | |
| `h261` | h261 | h261, avi, **matroska**, mov, asf | Raw stream carries no audio; AVI/MKV do |
| `h263` | h263 | h263, avi, **matroska**, mov, asf | same |
| `h263p` | avi | avi, **matroska**, mov, asf | |
| `msrle` | avi | avi, **mov** | **cannot go into Matroska / ASF**: the pal8 palette is lost and the picture is black |
| `msvideo1` | avi | avi, matroska, mov, asf | Pixel format is `rgb555le`, not indexed, so Matroska loses nothing |
| `cinepak` / `asv1` / `asv2` / `cljr` / `snow` / `zmbv` / `msmpeg4v2` | avi | avi, matroska, mov, asf | |
| `flashsv` | flv | flv, avi, matroska, mov, asf | |
| `flashsv2` | flv | flv | Only flv works |
| `qtrle` | mov | mov | `-c copy` into avi becomes rawvideo |
| `roqvideo` | roq | roq | |
| `rv10` / `rv20` | rm | rm | AVI stores rawvideo, ASF refuses to write, MP4/MOV has no tag |
| `smc` | mov | mov, matroska | |
| `svq1` | mov | mov, avi, asf | |
| `rpza` | mov | mov, avi, matroska, asf | |
| `wmv1` / `wmv2` | asf | asf, avi, matroska, mov | |
| `a64multi` / `a64multi5` | a64 | a64, avi, matroska, mov, asf | |

The tool **offers only a subset** (`Constants.ContainerChoicesByEncoder`). The
difference is "it can store" versus "anyone would do that": `mpeg4 → mpeg` works, but
stuffing MPEG-4 into MPEG-PS is a combination nobody wants; `a64multi → mov` is
likewise something no player expects. The criteria for the shipped table are (a) all
three checks pass, (b) it makes sense per player/container specs, (c) pal8 encoders
never go into a container that drops the palette.

## Frame rates

| Encoder | Rejected values | Rewritten | Conclusion |
| --- | --- | --- | --- |
| `mpeg1video` | 5, 10, 12, 15 | 20 → 23.976 | The declared 5/10/12/15 are libmpeg3's unofficial rates; only the eight real `frame_rate_code` values work. Snapped in v7.9.4 |
| `mpeg2video` | — | — | Has a frame-rate extension; accepts 1–60 |
| `rv10` / `rv20` | — | 23.976 → 23.000, 29.97 → 29.000, 59.94 → 59.000 | **RealMedia headers store integer fps**. v7.9.5: rounds to 24/30/60 so the file and the report agree |
| `roqvideo` | 23.976, 29.97, 59.94 | — | **RoQ headers are integer too**; fractions are refused outright. v7.9.5: same rounding |
| `amv` | 12, 20, 24, 29.97, 59.94, 60 | — | The real rule is `22050 % fps == 0` (5/10/14/15/18/21/25/30/50 all work); the tool lists common rungs |
| `flv` / `flashsv` / `flashsv2` / `wmv1` / `wmv2` | — | 29.97 → 29.917, 60 → 59.940 | Container timebase is 1/1000; 0.18% error, treat as known-benign |
| Everything else (asv, cinepak, mpeg4, libx264, mjpeg, msrle, msvideo1, qtrle, smc, snow, svq1, zmbv, libtheora, libxvid, h263p …) | — | — | All 13 values accepted |

The raw containers (`.h261` / `.h263` / `.a64`) read back as 25/1 at **any** frame
rate (ffprobe's default guess): the file has no frame-rate field at all. The picture is
fine, but the rate does not travel with the file — the tool prints a note and suggests
AVI.

## Pixel formats (v7.9.6)

The third application of the same idea: which formats `--pix-fmt` can write is not
decided by "FFmpeg has this format" but by **whether the frame length can be computed
exactly** — the encoder reads a raw `rawvideo` pipe, one frame being
`width×height×bytes-per-pixel` (plus 1024 for palette formats). One wrong byte and the
picture slides frame by frame.

```bash
python tools/pixfmt_sweep.py > /tmp/pixfmt.json
```

Each format writes three 64×48 raw frames, divided by the frame count. All 86 formats
were measured; the result table lives in `Core/PixelFormatCatalog.cs` and is pinned
row by row by `PixelFormatCatalogTests`.

Two old mistakes the measurement caught:

| Format | Old table | Measured | Consequence |
| --- | --- | --- | --- |
| `yuv440p` (4:4:0) | 1.5 B/px | **2.0 B/px** | 1/2 byte short per frame, the pipe desynchronises; chroma is full-width half-height, not 4:2:0 |
| `yuv440p` divisibility | (2, 2) | **(1, 2)** | Would trim one extra column |

The bit-packed `monob` / `monow` are computed by **row-byte alignment**: 65 pixels wide
is 9 bytes per row (0.125 B/px is only a coincidence when it divides evenly).

### Declared format ≠ written format

| Combination | Result |
| --- | --- |
| `libx264` + `-pix_fmt gray` | File says `yuvj420p` (the encoder accepts gray as input and writes 4:2:0) |
| `libx264` + `-color_primaries bt2020 -color_trc smpte2084` | File says `unknown`/`unknown` |
| Same + `-x264-params colorprim=bt2020:transfer=smpte2084` | `bt2020`/`smpte2084`, genuinely written |
| `mpeg4`/`mpeg2video` + primaries/transfer | The bitstream has no such fields; Matroska only keeps Range and matrix |

Hence: after encoding, the requested pixel format is read back with ffprobe and a
mismatch produces a note; libx264's primaries/transfer also go through `-x264-params`.

Supporting tests: `tests/e2e/test_picture_format.py` (7 cases: pal8 is not black,
10-bit stays 10-bit, colour tags reach the bitstream, container refusal, invalid format
exits 2, `--list-pix-fmts` marking, dither only where it belongs).

## Regression

These conclusions are guarded, not merely written down:

- `tests/LiveArchiveEncoder.Tests/EncoderRateAndContainerTests.cs` — integer
  frame-rate rules, container table self-consistency (extension ↔ muxer name), pal8
  kept out of palette-dropping containers, mux-only pairs still refused;
- `tests/LiveArchiveEncoder.Tests/PaletteAndRateTests.cs` — pal8 frame length, mpeg1's
  eight real frame rates;
- `tests/e2e/test_option_smoke.py` — every new container pair produces a file,
  fractional frame rates land as integers, msrle is not black;
- `tests/LiveArchiveEncoder.Tests/PixelFormatCatalogTests.cs` — all 86 rows match
  measured frame lengths, 4:4:0 is 2.0, bit-packed rows align, palette/container
  conflicts, dither only for low colour depth;
- `tests/e2e/test_picture_format.py` — pal8 is not black, 10-bit stays 10-bit, colour
  tags reach the bitstream, palette-dropping containers are refused.
