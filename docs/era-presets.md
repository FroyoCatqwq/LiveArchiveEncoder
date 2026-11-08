# 时代预设：站点 -> 年份

[English](era-presets.en.md) · **中文**

> v7.9.7 起，预设不再是一份"时代"清单加一份"站点"清单，而是**一个站在一个年份**。
> id 就是 `site/year`，例如 `youku/2007`、`bilibili/2012`、`generic/1998`。

## 怎么用

```
LiveArchiveEncoder in.mp4 out.flv --era youku/2007
LiveArchiveEncoder in.mp4 out.mp4 --era bilibili/2012 --h264 libx264
LiveArchiveEncoder in.mp4 out.mp4 --era generic/2014 --era-aspect 16:9
```

- 菜单第 14 项是两级：先列站点，选完站点再列它有的年份。
- `--era` 只**填参数**，不会替你打开二次压缩；那一步仍然要你自己开。
- 预设是**兜底不是覆盖**：显式给的 `--width` / `--fps` / `--encoder` 一律优先。

## 内置表

`直播层` 是采集侧（尺寸@帧率 / 码率上限），`交付` 是站点当年给观众的规格。
`编码器` 一列写的是"直播层用什么 -> 二次压缩用什么"；箭头后面是站点当年真正发出的编码器。

| 预设 | 年份 | 直播层 | 交付（视频/音频） | 编码器 | 依据 | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| `generic/1998` | [1998](http://web.archive.org/web/19980703151556/http://www11.real.com:80/) | 160x120@10 / 45k | 160x120@10 45k 16k/16kHz mono | rv20 → libx264/aac .mp4 | 包络 | RealVideo G2 在 28.8K 调制解调器上的典型档位；当年无站点页面可引 |
| `generic/2003` | [2003](http://web.archive.org/web/20030531193112/http://www6.real.com:80/) | 320x240@15 / 225k | 320x240@15 225k 24k/22.05kHz mono | rv20 → libx264/aac .mp4 | 包络 | ADSL 普及初期 RealVideo 8 的常见参数 |
| `generic/2007` | [2007](http://web.archive.org/web/20070601120905/http://youtube.com/) | 352x264@15 / 300k | 352x264@15 300k 32k/22.05kHz mono | flv → libx264/aac .mp4 | 包络 | FLV 时代的通用档位；具体站点见 youku/tudou 等行 |
| `generic/2010` | [2010](http://web.archive.org/web/20100602002509/http://www.youtube.com/) | 640x480@25 / 900k | 640x480@25 900k 64k/44.1kHz mono | libx264 → libx264/aac .mp4 | 包络 | 480p H.264 成为网页默认尺寸的年份 |
| `generic/2014` | [2014](http://web.archive.org/web/20140602000310/http://www.youtube.com/) | 1280x720@30 / 2500k | 1280x720@30 2500k 96k/44.1kHz stereo | libx264 → libx264/aac .mp4 | 包络 | 720p 普及、立体声得以保留 |
| `cctv/2003` | [2003](http://web.archive.org/web/20030528121336/http://www.cctv.com:80/) | 320x240@15 / 290k | 320x240@15 290k 32k/11.025kHz mono | wmv2 → wmav2 .asf | **页面+实测** | 依据 `title/copyright=cctv.com`、2004-09-20 的原始 WMV；见下节 |
| `cctv/2006` | [2006](http://web.archive.org/web/20060528214736/http://cctv.com:80/) | 320x240@15 / 300k | 320x240@15 300k 32k/11.025kHz stereo | flv → libmp3lame .flv | **实测+史料** | 依据 2007–09 cctv.com 原始 FLV（LAME3.97/3.98）；见下节 |
| `cctv/2011` | [2011](http://web.archive.org/web/20110602005532/http://www.cntv.cn:80/) | 640x360@25 / 700k | 640x360@25 700k 96k/44.1kHz stereo | libx264 → aac .mp4 | 包络 | CNTV 独立域名之后 |
| `sina/2003` | [2003](http://web.archive.org/web/20030527035526/http://video.sina.com.cn:80/) | 320x240@12 / 200k | 320x240@12 200k 24k/22.05kHz mono | wmv2 → wmav2 .asf | 包络 |  |
| `sina/2006` | [2006](http://web.archive.org/web/20051208020436/http://video.sina.com.cn:80/) | 320x240@12 / 200k | 320x240@12 200k 32k/22.05kHz mono | flv → libmp3lame .flv | 包络 | 新浪播客；12 fps 是它的特征 |
| `sina/2010` | [2010](http://web.archive.org/web/20100605124737/http://video.sina.com.cn/) | 640x360@25 / 600k | 640x360@25 600k 96k/44.1kHz stereo | libx264 → aac .mp4 | 包络 |  |
| `sohu/2003` | [2003](http://web.archive.org/web/20080820023714/http://video.sohu.com/) | 320x240@15 / 220k | 320x240@15 220k 32k/22.05kHz mono | wmv2 → wmav2 .asf | 包络 |  |
| `sohu/2007` | [2007](http://web.archive.org/web/20080820023714/http://video.sohu.com/) | 352x264@15 / 320k | 352x264@15 320k 32k/22.05kHz mono | flv → libmp3lame .flv | 包络 |  |
| `sohu/2012` | [2012](http://web.archive.org/web/20120601045515/http://tv.sohu.com/) | 640x360@25 / 800k | 640x360@25 800k 96k/44.1kHz stereo | libx264 → aac .mp4 | 包络 | 搜狐视频独立域名 tv.sohu.com |
| `netease/2003` | [2003](http://web.archive.org/web/20090310164907/http://v.163.com:80/) | 176x144@12 / 120k | 176x144@12 120k 24k/22.05kHz mono | wmv2 → wmav2 .asf | 包络 | QCIF 小窗播放器 |
| `netease/2008` | [2008](http://web.archive.org/web/20090310164907/http://v.163.com:80/) | 320x240@15 / 240k | 320x240@15 240k 32k/22.05kHz mono | flv → libmp3lame .flv | 包络 |  |
| `qq/2006` | [2006](http://web.archive.org/web/20061017063235/http://video.qq.com:80/) | 320x240@25 / 260k | 320x240@25 260k 32k/22.05kHz mono | flv → libmp3lame .flv | 包络 | 同期唯一把帧率拉到 25 的播放器 |
| `qq/2010` | [2010](http://web.archive.org/web/20110301075944/http://v.qq.com/) | 640x360@25 / 700k | 640x360@25 700k 96k/44.1kHz stereo | libx264 → aac .mp4 | 包络 |  |
| `tudou/2006` | [2006](http://web.archive.org/web/20060612223905/http://www.tudou.com:80/) | 352x264@12 / 295k | 352x264@12 295k 32k/22.05kHz mono | flv → libmp3lame .flv | 实测 | 对真实下载的 FLV 文件 ffprobe 得出（v7.9.5 记录） |
| `tudou/2009` | [2009](http://web.archive.org/web/20090616230533/http://www.tudou.com:80/) | 352x264@15 / 320k | 352x264@15 320k 32k/22.05kHz mono | flv → libmp3lame .flv | 实测 | 同上，帧率与码率都略升 |
| `youku/2007` | [2007](http://web.archive.org/web/20070602010215/http://www.youku.com/v_show/id_1001059.html) | 320x240@15 / 280k | 320x240@15 280k 32k/22.05kHz mono | flv → libmp3lame .flv | **史料** | 352x264 原是包络估值；优酷素材经元数据证实为 x264 后期重编码，不作编码依据 |
| `youku/2011` | [2011](http://web.archive.org/web/20110601214528/http://www.youku.com:80/) | 640x360@25 / 800k | 640x360@25 800k 96k/44.1kHz stereo | libx264 → aac .mp4 | 包络 |  |
| `ku6/2007` | [2007](http://web.archive.org/web/20070529140512/http://ku6.com:80/) | 320x240@15 / 240k | 320x240@15 240k 32k/22.05kHz mono | flv → libmp3lame .flv | 包络 |  |
| `56/2007` | [2007](http://web.archive.org/web/20070707111454/http://www.56.com/) | 320x240@15 / 220k | 320x240@15 220k 32k/22.05kHz mono | flv → libmp3lame .flv | 包络 |  |
| `acfun/2009` | [2009](http://web.archive.org/web/20090503141141/http://www.acfun.cn:80/) | 320x240@15 / 260k | 320x240@15 260k 32k/22.05kHz mono | flv → libmp3lame .flv | 包络 |  |
| `bilibili/2010` | [2010](http://web.archive.org/web/20110222044540/http://bilibili.tv:80/) | 384x288@24 / 320k | 384x288@24 320k 32k/22.05kHz mono | flv → libmp3lame .flv | 包络 | 4:3 动画素材，24 fps |
| `bilibili/2012` | [2012](http://web.archive.org/web/20120602093610/http://www.bilibili.tv:80/) | 640x360@30 / 700k | 640x360@30 700k 96k/44.1kHz stereo | libx264 → aac .mp4 | 包络 |  |
| `bilibili/2014` | [2014](http://web.archive.org/web/20140530212159/http://www.bilibili.com:80/) | 1280x720@30 / 1800k | 1280x720@30 1800k 128k/44.1kHz stereo | libx264 → aac .mp4 | 包络 |  |
| `iqiyi/2011` | [2011](http://web.archive.org/web/20110809132204/http://www.iqiyi.com:80/) | 640x360@25 / 800k | 640x360@25 800k 96k/44.1kHz stereo | libx264 → aac .mp4 | 包络 |  |
| `youtube/2007` | [2007](http://web.archive.org/web/20070601120905/http://youtube.com/) | 320x240@15 / 200k | 320x240@15 200k 64k/22.05kHz mono | flv → libmp3lame .flv | 文档 | YouTube 2006-2008 的标准档：320x240 + 22 kHz 单声道 64 kbps |
| `youtube/2011` | [2011](http://web.archive.org/web/20110602000854/http://www.youtube.com//) | 640x360@30 / 700k | 640x360@30 700k 96k/44.1kHz stereo | libx264 → aac .mp4 | 包络 |  |
| `youtube/2015` | [2015](http://web.archive.org/web/20150601235840/https://www.youtube.com/) | 1280x720@30 / 1500k | 1280x720@30 1500k 96k/48kHz stereo | libvpx-vp9 → libopus .webm | 文档 | VP9 + Opus 成为 720p 默认交付格式 |

### "依据"是什么意思

- **实测** —— 拿当年真实的下载文件 ffprobe 出来的（土豆那几个）。
- **文档** —— YouTube 自己的规格说明/帮助页写着的（320x240 + 22 kHz 单声道那套）。
- **包络** —— 当年那个编码器/容器在这个码率段能给出什么，按包络估的。
- **页面** —— 从存档页面的播放器参数里读到的具体数字。

**为什么很多行是"包络"**：2002-2005 的中文门户走的是 MMS/RTSP 流，Wayback 从来不保存
这类流媒体，只存了页面；而 2006 年之后的播放页多数是 Flash + JS，参数在运行时由
`swf` 里拼出来，存档下来的 HTML 里只剩一个 `<embed>`。结果是"站点存在、年份可证、
具体码率不可证"。这种情况下本表给的是**当年编码器在这个尺寸下的典型值**，不是伪造的
实测数字。

## 原始素材取证与 2026-11-07 更正

用户从 archive.org 取回 20 份 2003–2013 的 cctv / 优酷系素材。**文件名的年份不等于
编码年份**：先看 ffprobe / MediaInfo 的元数据，再决定它能不能当证据。

### 取证方法

| 手段 | 用途 |
| --- | --- |
| `ffprobe -show_format -show_streams -of json` | 编码格式、尺寸、帧率、码率，**以及容器/流的 tags** |
| `MediaInfo --Output=JSON` | `Encoded_Date`、`Encoded_Library`、ASF 的 `WM/*`、RealMedia 的 `ASMRuleBook` |
| opencv 抽帧 | 画面里的台标、网站水印、播放器/浏览器界面 |
| 当年网页 / 博客 / 论坛 | 站方说明与技术社区对转码参数的记录 |

### 元数据里的站点线索

| 文件 | 关键字段 | 结论 |
| --- | --- | --- |
| `cctv-2004/2004年9月20日新闻.wmv` | `title = cctv.com`、`copyright = cctv.com`、`creation_time = 2004-09-20T11:42:05Z` | **A 级**：当年站端原始，wmv1 320x240@25 / 290 kbps / wmav2 11.025 kHz mono |
| `cctv-2004/[岩松看霓虹国02]....rmvb` | `ASMRuleBook = #($Bandwidth >= 0), Stream0Bandwidth = 64082, Stream1Bandwidth = 385918` | RealMedia 多码率规则书；rv40 + cook，320x240 / 388 kbps / 44.1 kHz stereo |
| `cctv-2007~2009/*.flv` | `Encoded_Library = LAME3.97 / LAME3.98 (alpha\|beta)`；画面有 `CCTV.com` 水印与栏目台标（opencv 抽帧确认） | **A 级**：flv1(Sorenson) 320x240@15 + MP3 11.025 kHz stereo，LAME 版本与年份相符 |
| `cctv-2003/《新闻调查》...wmv` ×2 | `creation_time = 2006-04-20`（两份同一天） | **B 级**：2006 年批量转制，不能代表 2003 |
| `cctv-2007/101575_...wmv` | `creation_time = 1970-01-01` | 时间戳被清零，B 级 |
| `cctv-ixiyou-2010later/...20130505.mp4` | `encoder = Lavf60.10.100` | FFmpeg 6.x（2023+）重封装，B 级 |
| `youku-2008/手把手教你...mp4` | `creation_time = 2010-05-24`、`GPAC ISO ... Handler`、`x264 core 54`；抽帧为 XP + IE 录屏 | 2010 年再分发，B 级 |
| `youku-early/*.flv`、`youku-ku6-2005-flv/*.flv` | `x264 core 54 / core 56 svn-680`，容器里是 H.264 + AAC | **B 级**：Flash 9 之后的重编码，文件名写 2005–2007 不成立 |

### 史料（不能只看视频的原因）

1. **央视网 2003《视频点播说明》**（`cctv.com/advertisement/20030521/`）：站点音、视频
   "目前包括 Real 和 Windows Media 两种格式"——**2003 年没有 FLV**。
2. **央视网 2006《走过 CCTV.com 的第一个十年》**：2004-03-01《视听在线》改版，直播码流
   由单一 56K 改为 **28K–56K–128K 自适应**（直播自适应，不是点播分辨率）。
3. **Cash Chou 博客《FLV 小檔案》**：第一代 FLV 是 Sorenson H.263（FLV1），Flash 8 起是
   On2 VP6（"中國土豆網在用"），Flash 9 才支持 H.264 + AAC。→ 直接证明那些 H.264/AAC
   的"优酷早期 FLV"不是当年站端产物。
4. **博客园《视频参数介绍》/ ChinaUnix 博客 / CSDN**：视频共享站 FLV 普遍 320×240、
   12–15 fps、H.263、码率不足 300 Kbps，播放界面常为 400×300。
5. **论坛实测帖**：土豆/优酷上传后"系统会自动转换成标准的 320×240"，一份 FLV 实测
   295 Kbps / 320×240 / 4:3 / 11.99 fps。
6. **百度知道 2009**：上传的视频"会被它自动变为 FLV 格式"。

### 更正清单（本版实际改了什么）

| 预设 | 原值 | 新值 | 主要依据 | 置信度 |
| --- | --- | --- | --- | --- |
| `cctv/2003` | 320x240@15 / 250k / 32k 22.05kHz | 320x240@15 / **290k** / 32k **11.025kHz** | A 级 2004 `cctv.com` 原始 WMV + 站方 2003 说明 | 高（码率/音频），帧率仍为包络 |
| `cctv/2006` | 384x288@25 / 350k / 48k 22.05kHz mono | **320x240@15 / 300k / 32k 11.025kHz stereo** | A 级 2007–09 cctv.com FLV + 博客/论坛 | 高 |
| `youku/2007` | 352x264@15 / 320k | **320x240@15 / 280k** | 当年博客/论坛的 320x240、<300 Kbps | 中（史料为主，素材仅弱旁证） |

**没有改的**：

- 交付编码器仍是 `flv`（Sorenson H.263）+ `libmp3lame`。史料 1、3、4 一致指向这一代；
  archive.org 里那些 H.264+AAC 的 FLV 是后期重编码，**不采信**。
- `tudou/2006` 的 352x264 / 295 Kbps 保持：它本就标注"实测"，且与论坛实测帖的 295 Kbps
  吻合，属于站点高清档，不与其他站点的 320x240 默认档混为一谈。
- `cctv/2003` 直播层 fps 保持 15：A 级素材是 PAL 25 fps，但直播层描述的是采集侧节流档位，
  不是站端交付帧率；无站方采集参数可证，保留包络值而不用文件帧率硬套。
- `ku6/2007` 等年份未动：`ku6-2005-flv` 那份的编码库是 x264 core 56（约 2008 后），
  不足以把预设年份前移。

## 直播画质与机器参数（v7.9.8.2）

上面那张表说的是"多大、多少帧、多少码率"。一个行还得出**画质**——而画质不是一个
数：`q:v` 和 `crf` 是两把不同的尺子，同一个 9 在 mpeg4 下是 375 kbps，在 libx264
下是 935 kbps（352x264@15 实测）。所以每一行都写两个数，按运行时的编码器取用：

| 预设 | `q:v` | CRF |
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

**怎么定的**：拿 `tests/media/test_src.mp4` 按每行的尺寸和帧率各编一遍，取"编出来的
码率落在该行上限以内、又尽量靠近上限"的那个值。CRF 那一列实测落在上限的
0.76–1.04 倍；`q:v` 的步长太粗（相邻两档画面几乎不变），所以它只保证"不超上限太多"。
参考素材是合成彩条，对 MPEG-4 来说比真实素材更难压，所以真实素材会更省——方向是安全
的，不会变成大幅超限。

- **依据等级**：上表是**实测**（本机 FFmpeg 快照 + 上面那份素材，2026-10-27）。
- 换编码器时两个数不会互相顶替：`--era youku/2007 --encoder libx264` 拿 CRF 22，
  不带 `--encoder` 时拿 `q:v 11`。
- `--h264 libx264` 把 2010 年后的行从 OpenH264 换过来时同理。OpenH264 本身没有画质
  旋钮，它拿到的是上限码率。

窗口、缓冲帧、反交错按年份给（同一次实测的一部分）：

| 年份 | `--window` | `--buffer-frames` | 反交错 | 为什么 |
| --- | --- | --- | --- | --- |
| ≤2003 | 3 s | 4 | `off` | RealServer/WMS 要持续几秒才降档；yadif 2006 年才出现；320x240 的帧不值得排八帧 |
| 2006–2009 | 2 s | 6 | `yadif` | FLV 门户在自己两秒的缓冲里就反应；yadif 同年随 MPlayer 进 FFmpeg |
| ≥2010 | 2 s | 8 | `yadif` | 640x360 以上的帧是 320x240 的五六倍，编码器成了慢的那一半 |

反交错对每一行都带"只处理标记为隔行的帧"（`--deint-marked-only`）：时代预设说的是
**当年的广播**怎么被处理，一份逐行素材不该被 yadif 白白磨软。

## 旧 id 仍然可用

v7.9.6.3 及以前的名字全部保留为别名，用旧写法时 CLI 会打一行"已更名"，功能不变：

| 旧 id | 现在的 id |
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

另外 `generic-1998` 这种连字符写法也认，等价于 `generic/1998`。

## H.264 用哪个实现

2010 年及以后的 H.264 行默认 **OpenH264**（`--h264 libopenh264`），之前的行是 libx264。

为什么：x264 的心理视觉优化会把 700 kbps 的 360p 做得比当年"干净"，一眼就是重制。
OpenH264 是那个年代的参考实现，固定码率下的软糊感才是当年的样子。它的参数只有
`-b:v`（没有 CRF），所以预设里的码率直接生效。

需要构建里带 `libopenh264`：本项目的 BtbN GPL 快照有，缺了就用
`--ffmpeg-download nonfree` 换成 `BnqDzj/FFmpeg-Builds-nonfree`。

## 自己加一份

自定义预设仍然写在设置文件的 `CustomEras` 里（菜单 14 的 `n`/`e`/`d`），id 可以和
内置的重名——重名时你的那份赢。v7.9.7 起自定义 id 也允许一个 `/`，所以可以写
`mysite/2012` 和内置行并排。
