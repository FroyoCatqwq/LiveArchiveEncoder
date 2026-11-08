# Era presets: site → year

[English](era-presets.en.md) · [中文](era-presets.md)

> Since v7.9.7 a preset is **one site at one year**, not an "era" list plus a
> separate "site" list. The id *is* `site/year`, e.g. `youku/2007`,
> `bilibili/2012`, `generic/1998`.

## How to use

```
LiveArchiveEncoder in.mp4 out.flv --era youku/2007
LiveArchiveEncoder in.mp4 out.mp4 --era bilibili/2012 --h264 libx264
LiveArchiveEncoder in.mp4 out.mp4 --era generic/2014 --era-aspect 16:9
```

- Menu 14 is two levels: pick a site, then one of its years.
- `--era` only **fills parameters**; it never turns the recompress stage on. That
  stays your decision.
- A preset is a **fallback, not an override**: an explicit `--width` / `--fps` /
  `--encoder` always wins.

## Built-in table

`live` is the capture side (size@frame rate / bitrate ceiling); `delivery` is what
the site actually served viewers that year. The `encoders` column reads "live
side → recompress side"; after the arrow is the encoder the site really shipped.

| Preset | Year | Live | Delivery (video/audio) | Encoders | Evidence | Note |
| --- | --- | --- | --- | --- | --- | --- |
| `generic/1998` | [1998](http://web.archive.org/web/19980703151556/http://www11.real.com:80/) | 160x120@10 / 45k | 160x120@10 45k 16k/16kHz mono | rv20 → libx264/aac .mp4 | envelope | Typical RealVideo G2 rung on a 28.8K modem; no site page survives to cite |
| `generic/2003` | [2003](http://web.archive.org/web/20030531193112/http://www6.real.com:80/) | 320x240@15 / 225k | 320x240@15 225k 24k/22.05kHz mono | rv20 → libx264/aac .mp4 | envelope | Common RealVideo 8 settings at the start of ADSL adoption |
| `generic/2007` | [2007](http://web.archive.org/web/20070601120905/http://youtube.com/) | 352x264@15 / 300k | 352x264@15 300k 32k/22.05kHz mono | flv → libx264/aac .mp4 | envelope | The generic FLV-era rung; specific sites have their own rows |
| `generic/2010` | [2010](http://web.archive.org/web/20100602002509/http://www.youtube.com/) | 640x480@25 / 900k | 640x480@25 900k 64k/44.1kHz mono | libx264 → libx264/aac .mp4 | envelope | The year 480p H.264 became the web default |
| `generic/2014` | [2014](http://web.archive.org/web/20140602000310/http://www.youtube.com/) | 1280x720@30 / 2500k | 1280x720@30 2500k 96k/44.1kHz stereo | libx264 → libx264/aac .mp4 | envelope | 720p is common and stereo survives |
| `cctv/2003` | [2003](http://web.archive.org/web/20030528121336/http://www.cctv.com:80/) | 320x240@15 / 290k | 320x240@15 290k 32k/11.025kHz mono | wmv2 → wmav2 .asf | **page + measured** | From `title/copyright=cctv.com` and an original 2004-09-20 WMV; see below |
| `cctv/2006` | [2006](http://web.archive.org/web/20060528214736/http://cctv.com:80/) | 320x240@15 / 300k | 320x240@15 300k 32k/11.025kHz stereo | flv → libmp3lame .flv | **measured + sources** | From original 2007–09 cctv.com FLVs (LAME3.97/3.98); see below |
| `cctv/2011` | [2011](http://web.archive.org/web/20110602005532/http://www.cntv.cn:80/) | 640x360@25 / 700k | 640x360@25 700k 96k/44.1kHz stereo | libx264 → aac .mp4 | envelope | After CNTV moved to its own domain |
| `sina/2003` | [2003](http://web.archive.org/web/20030527035526/http://video.sina.com.cn:80/) | 320x240@12 / 200k | 320x240@12 200k 24k/22.05kHz mono | wmv2 → wmav2 .asf | envelope |  |
| `sina/2006` | [2006](http://web.archive.org/web/20051208020436/http://video.sina.com.cn:80/) | 320x240@12 / 200k | 320x240@12 200k 32k/22.05kHz mono | flv → libmp3lame .flv | envelope | Sina Podcast; 12 fps is its signature |
| `sina/2010` | [2010](http://web.archive.org/web/20100605124737/http://video.sina.com.cn/) | 640x360@25 / 600k | 640x360@25 600k 96k/44.1kHz stereo | libx264 → aac .mp4 | envelope |  |
| `sohu/2003` | [2003](http://web.archive.org/web/20080820023714/http://video.sohu.com/) | 320x240@15 / 220k | 320x240@15 220k 32k/22.05kHz mono | wmv2 → wmav2 .asf | envelope |  |
| `sohu/2007` | [2007](http://web.archive.org/web/20080820023714/http://video.sohu.com/) | 352x264@15 / 320k | 352x264@15 320k 32k/22.05kHz mono | flv → libmp3lame .flv | envelope |  |
| `sohu/2012` | [2012](http://web.archive.org/web/20120601045515/http://tv.sohu.com/) | 640x360@25 / 800k | 640x360@25 800k 96k/44.1kHz stereo | libx264 → aac .mp4 | envelope | Sohu Video's own domain, tv.sohu.com |
| `netease/2003` | [2003](http://web.archive.org/web/20090310164907/http://v.163.com:80/) | 176x144@12 / 120k | 176x144@12 120k 24k/22.05kHz mono | wmv2 → wmav2 .asf | envelope | QCIF player in a small window |
| `netease/2008` | [2008](http://web.archive.org/web/20090310164907/http://v.163.com:80/) | 320x240@15 / 240k | 320x240@15 240k 32k/22.05kHz mono | flv → libmp3lame .flv | envelope |  |
| `qq/2006` | [2006](http://web.archive.org/web/20061017063235/http://video.qq.com:80/) | 320x240@25 / 260k | 320x240@25 260k 32k/22.05kHz mono | flv → libmp3lame .flv | envelope | The only player of its day running at 25 fps |
| `qq/2010` | [2010](http://web.archive.org/web/20110301075944/http://v.qq.com/) | 640x360@25 / 700k | 640x360@25 700k 96k/44.1kHz stereo | libx264 → aac .mp4 | envelope |  |
| `tudou/2006` | [2006](http://web.archive.org/web/20060612223905/http://www.tudou.com:80/) | 352x264@12 / 295k | 352x264@12 295k 32k/22.05kHz mono | flv → libmp3lame .flv | measured | ffprobe of a real downloaded FLV (recorded in v7.9.5) |
| `tudou/2009` | [2009](http://web.archive.org/web/20090616230533/http://www.tudou.com:80/) | 352x264@15 / 320k | 352x264@15 320k 32k/22.05kHz mono | flv → libmp3lame .flv | measured | Same source; frame rate and bitrate both slightly up |
| `youku/2007` | [2007](http://web.archive.org/web/20070602010215/http://www.youku.com/v_show/id_1001059.html) | 320x240@15 / 280k | 320x240@15 280k 32k/22.05kHz mono | flv → libmp3lame .flv | **sources** | 352x264 was an envelope guess; Youku's material proved to be a later x264 re-encode, so it is not evidence |
| `youku/2011` | [2011](http://web.archive.org/web/20110601214528/http://www.youku.com:80/) | 640x360@25 / 800k | 640x360@25 800k 96k/44.1kHz stereo | libx264 → aac .mp4 | envelope |  |
| `ku6/2007` | [2007](http://web.archive.org/web/20070529140512/http://ku6.com:80/) | 320x240@15 / 240k | 320x240@15 240k 32k/22.05kHz mono | flv → libmp3lame .flv | envelope |  |
| `56/2007` | [2007](http://web.archive.org/web/20070707111454/http://www.56.com/) | 320x240@15 / 220k | 320x240@15 220k 32k/22.05kHz mono | flv → libmp3lame .flv | envelope |  |
| `acfun/2009` | [2009](http://web.archive.org/web/20090503141141/http://www.acfun.cn:80/) | 320x240@15 / 260k | 320x240@15 260k 32k/22.05kHz mono | flv → libmp3lame .flv | envelope |  |
| `bilibili/2010` | [2010](http://web.archive.org/web/20110222044540/http://bilibili.tv:80/) | 384x288@24 / 320k | 384x288@24 320k 32k/22.05kHz mono | flv → libmp3lame .flv | envelope | 4:3 animation material, 24 fps |
| `bilibili/2012` | [2012](http://web.archive.org/web/20120602093610/http://www.bilibili.tv:80/) | 640x360@30 / 700k | 640x360@30 700k 96k/44.1kHz stereo | libx264 → aac .mp4 | envelope |  |
| `bilibili/2014` | [2014](http://web.archive.org/web/20140530212159/http://www.bilibili.com:80/) | 1280x720@30 / 1800k | 1280x720@30 1800k 128k/44.1kHz stereo | libx264 → aac .mp4 | envelope |  |
| `iqiyi/2011` | [2011](http://web.archive.org/web/20110809132204/http://www.iqiyi.com:80/) | 640x360@25 / 800k | 640x360@25 800k 96k/44.1kHz stereo | libx264 → aac .mp4 | envelope |  |
| `youtube/2007` | [2007](http://web.archive.org/web/20070601120905/http://youtube.com/) | 320x240@15 / 200k | 320x240@15 200k 64k/22.05kHz mono | flv → libmp3lame .flv | docs | YouTube's 2006–2008 standard: 320x240 + 22 kHz mono 64 kbps |
| `youtube/2011` | [2011](http://web.archive.org/web/20110602000854/http://www.youtube.com//) | 640x360@30 / 700k | 640x360@30 700k 96k/44.1kHz stereo | libx264 → aac .mp4 | envelope |  |
| `youtube/2015` | [2015](http://web.archive.org/web/20150601235840/https://www.youtube.com/) | 1280x720@30 / 1500k | 1280x720@30 1500k 96k/48kHz stereo | libvpx-vp9 → libopus .webm | docs | VP9 + Opus becomes the default 720p delivery format |

### What "evidence" means

- **measured** — ffprobe output from a real file of that period (the Tudou rows).
- **docs** — YouTube's own specification / help pages (the 320x240 + 22 kHz mono set).
- **envelope** — what that encoder/container could do at that bitrate, bounded by an
  envelope estimate.
- **page** — concrete numbers read from the archived player parameters.

**Why so many rows are "envelope"**: 2002–2005 Chinese portals streamed over
MMS/RTSP, and the Wayback Machine never archives those streams — only the pages. From
2006 on, most playback pages were Flash + JS, with parameters assembled at runtime
inside the `swf`; the archived HTML keeps only an `<embed>`. The result is "the site
existed, the year is provable, the exact bitrate is not". In those cases the table
gives **that encoder's typical value at that size**, never a fabricated measurement.

## Source forensic work and the 2026-11-07 correction

A user recovered 20 pieces of 2003–2013 cctv / Youku material from archive.org.
**A file-name year is not an encoding year**: read ffprobe / MediaInfo metadata first,
then decide whether it can be evidence at all.

### Method

| Tool | Use |
| --- | --- |
| `ffprobe -show_format -show_streams -of json` | codec, size, frame rate, bitrate, **and container/stream tags** |
| `MediaInfo --Output=JSON` | `Encoded_Date`, `Encoded_Library`, ASF `WM/*`, RealMedia `ASMRuleBook` |
| opencv frame grabs | station logos, site watermarks, player/browser UI |
| period web pages / blogs / forums | the site's own statements and technical communities' transcode notes |

### Site clues in metadata

| File | Key fields | Conclusion |
| --- | --- | --- |
| `cctv-2004/2004年9月20日新闻.wmv` | `title = cctv.com`, `copyright = cctv.com`, `creation_time = 2004-09-20T11:42:05Z` | **Grade A**: original from the site — wmv1 320x240@25 / 290 kbps / wmav2 11.025 kHz mono |
| `cctv-2004/[岩松看霓虹国02]....rmvb` | `ASMRuleBook = #($Bandwidth >= 0), Stream0Bandwidth = 64082, Stream1Bandwidth = 385918` | RealMedia multi-bitrate rule book; rv40 + cook, 320x240 / 388 kbps / 44.1 kHz stereo |
| `cctv-2007~2009/*.flv` | `Encoded_Library = LAME3.97 / LAME3.98 (alpha\|beta)`; `CCTV.com` watermark and programme logo in frame grabs | **Grade A**: flv1 (Sorenson) 320x240@15 + MP3 11.025 kHz stereo; LAME version matches the year |
| `cctv-2003/《新闻调查》...wmv` ×2 | `creation_time = 2006-04-20` (both) | **Grade B**: batch-converted in 2006, cannot represent 2003 |
| `cctv-2007/101575_...wmv` | `creation_time = 1970-01-01` | Timestamp zeroed, Grade B |
| `cctv-ixiyou-2010later/...20130505.mp4` | `encoder = Lavf60.10.100` | FFmpeg 6.x (2023+) remux, Grade B |
| `youku-2008/手把手教你...mp4` | `creation_time = 2010-05-24`, `GPAC ISO ... Handler`, `x264 core 54`; frame grabs show an XP + IE screen capture | Redistributed in 2010, Grade B |
| `youku-early/*.flv`, `youku-ku6-2005-flv/*.flv` | `x264 core 54 / core 56 svn-680`, H.264 + AAC inside the container | **Grade B**: re-encoded after Flash 9; the 2005–2007 file names do not hold |

### Period sources (why video alone is not enough)

1. **CCTV.com 2003 "video-on-demand notes"** (`cctv.com/advertisement/20030521/`):
   the site's audio and video came "in both Real and Windows Media formats" —
   **there was no FLV in 2003**.
2. **CCTV.com 2006 "the first ten years"**: the 2004-03-01 relaunch moved the live
   stream from a single 56K to **28K–56K–128K adaptive** (live adaptation, not
   on-demand resolution).
3. **Cash Chou's blog "FLV 小檔案"**: first-generation FLV was Sorenson H.263 (FLV1);
   Flash 8 moved to On2 VP6 ("what Tudou in China uses"); only Flash 9 supported
   H.264 + AAC. → Direct proof that those H.264/AAC "early Youku FLVs" are not
   period site output.
4. **cnblogs "video parameter primer" / ChinaUnix / CSDN**: video-sharing sites
   commonly used 320×240, 12–15 fps, H.263, under 300 Kbps, in a ~400×300 player.
5. **Forum measurements**: Tudou/Youku "converts automatically to the standard
   320×240"; one FLV measured 295 Kbps / 320×240 / 4:3 / 11.99 fps.
6. **Baidu Zhidao 2009**: uploaded video "is automatically turned into FLV".

### Correction list (what this release actually changed)

| Preset | Was | Now | Main evidence | Confidence |
| --- | --- | --- | --- | --- |
| `cctv/2003` | 320x240@15 / 250k / 32k 22.05kHz | 320x240@15 / **290k** / 32k **11.025kHz** | Grade A 2004 `cctv.com` WMV + the site's 2003 note | High (bitrate/audio); frame rate remains an envelope |
| `cctv/2006` | 384x288@25 / 350k / 48k 22.05kHz mono | **320x240@15 / 300k / 32k 11.025kHz stereo** | Grade A 2007–09 cctv.com FLVs + blogs/forums | High |
| `youku/2007` | 352x264@15 / 320k | **320x240@15 / 280k** | Period blogs/forums: 320x240, <300 Kbps | Medium (sources first, material only weak support) |

**What was not changed:**

- Delivery encoders stay `flv` (Sorenson H.263) + `libmp3lame`. Sources 1, 3 and 4
  all point at that generation; the H.264+AAC FLVs on archive.org are later
  re-encodes and **are not trusted**.
- `tudou/2006`'s 352x264 / 295 Kbps stands: it is labelled "measured", matches the
  forum's 295 Kbps, and is that site's HD rung — not the 320x240 default of others.
- `cctv/2003`'s live frame rate stays 15: the Grade A material is PAL 25 fps, but the
  live layer describes the capture-side rung, not the delivery frame rate. No
  capture-side parameters are provable, so the envelope value stays rather than
  copying the file's frame rate.
- `ku6/2007` and the like are untouched: `ku6-2005-flv` uses x264 core 56 (roughly
  2008+), which is not enough to move the preset year forward.

## Live quality and machine parameters (v7.9.8.2)

The table above answers "how big, how many frames, what bitrate". A row also has to
carry **picture quality** — and quality is not one number: `q:v` and `crf` are
different rulers, and the same 9 is 375 kbps under mpeg4 but 935 kbps under libx264
(measured at 352x264@15). So every row carries two numbers, chosen by the runtime
encoder:

| Preset | `q:v` | CRF |
| --- | --- | --- |
| `generic/1998` | 12 | 28 |
| `generic/2003` | 11 | 22 |
| `generic/2007` | 11 | 22 |
| `generic/2010` | 9 | 19 |
| `generic/2014` | 8 | 18 |
| `cctv/2003` | 11 | 22 |
| `cctv/2006` | 11 | 24 |
| `cctv/2011` | 10 | 21 |
| `sina/2003` | 11 | 22 |
| `sina/2006` | 11 | 22 |
| `sina/2010` | 10 | 22 |
| `sohu/2003` | 11 | 22 |
| `sohu/2007` | 11 | 22 |
| `sohu/2012` | 10 | 20 |
| `netease/2003` | 11 | 22 |
| `netease/2008` | 11 | 22 |
| `qq/2006` | 11 | 23 |
| `qq/2010` | 10 | 21 |
| `tudou/2006` | 11 | 22 |
| `tudou/2009` | 11 | 22 |
| `youku/2007` | 11 | 22 |
| `youku/2011` | 10 | 20 |
| `ku6/2007` | 11 | 22 |
| `56/2007` | 11 | 22 |
| `acfun/2009` | 11 | 23 |
| `bilibili/2010` | 11 | 24 |
| `bilibili/2012` | 10 | 21 |
| `bilibili/2014` | 9 | 21 |
| `iqiyi/2011` | 10 | 20 |
| `youtube/2007` | 11 | 22 |
| `youtube/2011` | 10 | 21 |
| `youtube/2015` | 9 | 23 |

**How they were chosen**: encode `tests/media/test_src.mp4` at each row's size and
frame rate, then take the value whose resulting bitrate lands under that row's
ceiling while staying as close as possible. The CRF column lands between 0.76 and
1.04× the ceiling; `q:v` steps are too coarse (adjacent values look identical), so it
only guarantees "not far above the ceiling". The reference material is synthetic
colour bars, which are harder for MPEG-4 than real footage — real material comes out
smaller, so the direction is safe.

- **Evidence grade**: the table is **measured** (this machine's FFmpeg snapshot plus
  that material, 2026-10-27).
- Switching encoders does not substitute one number for the other:
  `--era youku/2007 --encoder libx264` takes CRF 22; without `--encoder` it takes
  `q:v 11`.
- Same for `--h264 libx264` on post-2010 rows that default to OpenH264. OpenH264 has
  no quality knob at all; it gets the bitrate ceiling.

Window, buffer frames and deinterlacing follow the year (part of the same measurement):

| Year | `--window` | `--buffer-frames` | Deinterlace | Why |
| --- | --- | --- | --- | --- |
| ≤2003 | 3 s | 4 | `off` | RealServer/WMS needed several seconds to step down; yadif only arrived in 2006; a 320x240 frame is not worth eight |
| 2006–2009 | 2 s | 6 | `yadif` | FLV portals reacted inside their own two-second buffer; yadif entered FFmpeg the same year |
| ≥2010 | 2 s | 8 | `yadif` | A 640x360+ frame is five or six times a 320x240 one, so the encoder became the slow half |

Every row also carries "only touch frames marked interlaced" (`--deint-marked-only`):
an era preset is about how **that year's broadcast** was handled, and a progressive
source should not be softened by yadif for no reason.

## Old ids still work

Every name from v7.9.6.3 and earlier survives as an alias; using one prints a
"renamed" line and behaves identically:

| Old id | Current id |
| --- | --- |
| `dialup98` | `generic/1998` |
| `broadband03` | `generic/2003` |
| `flv07` | `generic/2007` |
| `mp4_10` | `generic/2010` |
| `hd14` | `generic/2014` |
| `cctv03` | `cctv/2003` |
| `cctv06` | `cctv/2006` |
| `cctv12` | `cctv/2011` |
| `sina03` | `sina/2003` |
| `sina06` | `sina/2006` |
| `sohu03` | `sohu/2003` |
| `sohu06` | `sohu/2007` |
| `netease03` | `netease/2003` |
| `qq06` | `qq/2006` |
| `tudou06` | `tudou/2006` |
| `youku07` | `youku/2007` |
| `youku11` | `youku/2011` |
| `ku6_07` | `ku6/2007` |
| `56_07` | `56/2007` |
| `acfun08` | `acfun/2009` |
| `bilibili09` | `bilibili/2010` |
| `bilibili11` | `bilibili/2012` |
| `bilibili14` | `bilibili/2014` |
| `iqiyi11` | `iqiyi/2011` |
| `youtube07` | `youtube/2007` |
| `youtube11` | `youtube/2011` |
| `youtube15` | `youtube/2015` |

A hyphenated form such as `generic-1998` is also accepted, equivalent to
`generic/1998`.

## Which H.264 implementation

H.264 rows from 2010 onwards default to **OpenH264** (`--h264 libopenh264`); earlier
rows use libx264.

Why: x264's psychovisual tuning makes a 700 kbps 360p look cleaner than the period
did — instantly recognisable as a remaster. OpenH264 is that era's reference
implementation, and the softness at a fixed bitrate is what the period looked like.
Its only knob is `-b:v` (no CRF), so the preset's bitrate takes effect directly.

The build must include `libopenh264`: this project's BtbN GPL snapshot does. If yours
lacks it, switch with `--ffmpeg-download nonfree` to `BnqDzj/FFmpeg-Builds-nonfree`.

## Adding your own

Custom presets still live in `CustomEras` in the settings file (menu 14: `n` / `e` /
`d`). The id may collide with a built-in one — yours wins. Since v7.9.7 a custom id
may contain one `/`, so `mysite/2012` can sit next to the built-in rows.
