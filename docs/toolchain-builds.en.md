# FFmpeg toolchain and 7-Zip builds

[English](toolchain-builds.en.md) · [中文](toolchain-builds.md)

> v7.9.8.13.1. Every asset fact below was checked on **2026-11-07** with
> `gh api repos/<repo>/releases/latest` and HTTP HEAD; the codec lists come from
> `ffmpeg -hide_banner -encoders` on both machines. This file covers "what to
> install and how the builds differ"; command usage is in `docs/usage.en.md` §7.10.

## 1. Ground rules

| Item | Rule |
| --- | --- |
| FFmpeg sources | Six source ids, filtered per platform; `btbn` stays the default |
| Version policy | **Daily / rolling first**; a stable asset is only a fallback when the daily feed cannot be resolved (`FfmpegDist.StableUrl`) |
| Extractor | Both platforms use the **official `ip7z/7zip` build**; no system p7zip requirement |
| Old configs | `avbuild` (SourceForge, manual) and `gyan` are kept; an existing `config.json` needs no change |

v7.9.8.11 briefly called `gyan` and `avbuild` "compatibility only"; `gyan` is a
first-class Windows source again. `avbuild` stays manual because SourceForge
intercepts automated downloads with Cloudflare.

## 2. The six FFmpeg sources

| id | Project | Win | Linux | Release tag | Asset measured this round | Archive |
| --- | --- | --- | --- | --- | --- | --- |
| `btbn` | [btbn/ffmpeg-builds](https://github.com/btbn/ffmpeg-builds/releases) | ✅ | ✅ | `latest` (rebuilt 2026-11-06 13:11 UTC) | `ffmpeg-master-latest-win64-gpl.zip` / `...linux64-gpl.tar.xz` | `.zip` / `.tar.xz` |
| `nonfree` | [BnqDzj/FFmpeg-Builds-nonfree](https://github.com/BnqDzj/FFmpeg-Builds-nonfree/releases) | ✅ | ✅ | `latest` (2026-11-06 19:20 UTC) | `...win64-nonfree.zip` / `...linux64-nonfree.tar.xz` | `.zip` / `.tar.xz` |
| `ytdlp` | [yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds/releases) | ✅ | ✅ | `latest` (2026-11-06 20:29 UTC) | `ffmpeg-master-latest-win64-gpl.zip` / `...linux64-gpl.tar.xz` | `.zip` / `.tar.xz` |
| `autobuild` | [AnimMouse/ffmpeg-autobuild](https://github.com/AnimMouse/ffmpeg-autobuild/releases) | ✅ | ❌ | **one tag per commit**, e.g. `2026-11-07-00-17-e196e57-89ebf7b` | `ffmpeg-<hash1>-<hash2>-win64-nonfree.7z` | `.7z` |
| `gyan` | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) | ✅ | ❌ | no fixed tag; `ffmpeg-git-full.7z` is a git-master rolling file | HEAD 200, 74,442,465 B, Last-Modified 2026-11-05 10:23 UTC | `.7z` |
| `avbuild` | SourceForge | ✅ | ✅ | manual directory | Download in a browser, then `--ffmpeg-archive` | `.7z` |

**About AnimMouse**: it is often assumed to publish Linux builds, but all 30 most
recent releases contain **only `win32` / `win64` nonfree assets**. `autobuild` is
therefore Windows-only, and because its tag and asset names embed commit hashes a
fixed URL is impossible — the installer reads `assets[]` from `releases/latest` and
matches `win64.*nonfree.*\.7z$`.

**Stable fallback (corrected in v7.9.8.13.2)**: the four fixed-URL sources (`btbn` /
`nonfree` / `ytdlp` / `gyan`) are rolling files with no more stable alternative, so
they have no `StableUrl`. Only the feed-driven `autobuild` needs one: it points at a
verified fixed-commit asset (tag `2026-11-07-00-17-e196e57-89ebf7b`), used when
`releases/latest` cannot be read, its JSON is malformed, or no asset matches. Before
that the field was empty and "daily first, stable as fallback" was only a comment —
dead code, fixed in that release.

## 3. Codec differences

### 3.1 Measured on both machines (`ffmpeg -hide_banner -encoders`)

| Encoder | Windows `nonfree` N-128412 | Linux `btbn` GPL N-128532 | Purpose |
| --- | --- | --- | --- |
| `rv10` / `rv20` | ✅ | ✅ | RealVideo 1.0 / 2.0, `.rm` era presets |
| `roqvideo` | ✅ | ✅ | id RoQ |
| `wmv1` / `wmv2` / `wmav2` | ✅ | ✅ | 2003–2005 portal `.asf` / `.wmv` |
| `amv` / `adpcm_ima_amv` | ✅ | ✅ | MP4-player era |
| `mjpeg` / `huffyuv` / `ffvhuff` | ✅ | ✅ | Intra-only / lossless |
| `mpeg1video` / `mpeg2video` / `mpeg4` / `libxvid` | ✅ | ✅ | General legacy encoding |
| `flv` (Sorenson H.263) | ✅ | ✅ | 2006–2009 Chinese video-site FLV |
| `libmp3lame` / `ac3` / `real_144` | ✅ | ✅ | Audio of the same period |
| `libopenh264` / `libx264` / `libx264rgb` | ✅ | ✅ | The two H.264 implementations |
| `libx265` / `libvpx` / `libvpx-vp9` | ✅ | ✅ | Modern delivery |
| **`libfdk_aac`** | **✅** | **❌** | nonfree-only; the classic licence-difference example |
| `libx264_qsv` / `mjpeg_qsv` / `*_vaapi` | ✅ (Windows MF/QSV variants) | partial | Hardware acceleration, machine-dependent |

> The ✅/❌ above are results from **these two machines**, not promises from the
> projects. They can change across versions and build flags; re-check with the
> commands below after switching builds.

### 3.2 What each build is for

| Source | Typical configuration | Who it suits |
| --- | --- | --- |
| `btbn` GPL | `--enable-gpl` + libx264/libx265/libvpx etc.; LGPL and shared variants also published | The default: GPL encoders, GPL distribution accepted |
| `btbn` stable branch | `n8.1` / `n9.0` assets in the same release | When a reproducible version number matters |
| `nonfree` | btbn plus `--enable-nonfree` (measured: has `libfdk_aac`) | nonfree audio encoders; **check the licence before redistributing binaries** |
| `ytdlp` | GPL build aimed at post-processing yt-dlp output; covers win32/winarm64/linuxarm64 | Staying in step with the yt-dlp ecosystem; different size and library set |
| `autobuild` | nonfree build made per FFmpeg commit, Windows only | Tracking upstream, or reproducing one commit's behaviour |
| `gyan` | `full` (widest third-party library set), Windows x64, `.7z` | The "everything included" Windows package |
| `avbuild` | Hand-published on SourceForge | Old-config compatibility; manual download |

### 3.3 Check it yourself

```bash
ffmpeg -version                       # version, configuration line, build time
ffmpeg -hide_banner -buildconf        # every configure switch (is --enable-nonfree there?)
ffmpeg -hide_banner -encoders | grep -E 'rv10|rv20|roqvideo|wmv2|libfdk_aac|flv'
ffmpeg -hide_banner -decoders | grep -E 'rv40|cook|vp6f'
```

The legacy encoders this project needs (rv10/rv20/roqvideo/wmv2/amv/mjpeg) are
**present in both measured builds**. The builds that actually need choosing are the
nonfree ones (`libfdk_aac`) and the hardware-accelerated variants.

## 4. 7-Zip: one extractor for both platforms

| Platform | Official asset (release `26.05`, 2026-10-21) | Lands at | How |
| --- | --- | --- | --- |
| Windows | `7z2605-x64.exe` (also `.msi`, `7zr.exe`, arm/arm64) | `<data dir>/7zip/7z.exe` | Silent install per the official FAQ: `/S` plus `/D=<dir>` (case-sensitive), no administrator rights |
| Linux | `7z2605-linux-x64.tar.xz` (also x86/arm/arm64/mac) | `<data dir>/7zip/7zz` | `tar -xJf` then `chmod +x` |

`7zz` is the full 7-Zip CLI; the archive also ships `7zzs` and `MANUAL/`. Lookup
order: **system PATH (`7z` / `7zz` / `7za`) → Windows `Program Files\7-Zip` → the
copy we installed**; only when neither exists does it download. A normal machine
therefore never triggers the download.

It is used well beyond font packs: `.zip` font packs, `.7z` / `.rar` (including
Rar5), and the `gyan` / `avbuild` / `autobuild` `.7z` archives all go through the
same `SevenZip.Ensure()`. Linux no longer has an "install p7zip by hand" dead end;
`.zip` still has the built-in .NET reader as a last resort.

## 5. Choosing

- **Default / no special need** → `btbn` (same on both platforms, daily, GPL).
- **nonfree encoders (fdk-aac etc.)** → `nonfree`; mind redistribution licences.
- **Working with yt-dlp / need ARM assets** → `ytdlp`.
- **Widest library set on Windows** → `gyan`; **track one commit** → `autobuild`.
- **An old config names `avbuild`** → still works, still a manual archive download.

After switching sources, force a capability rescan with `LAE_NO_CAPS_CACHE=1`: the
encoder set may differ.
