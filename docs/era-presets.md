# 时代预设：站点 -> 年份

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
| `cctv/2003` | [2003](http://web.archive.org/web/20030528121336/http://www.cctv.com:80/) | 320x240@15 / 250k | 320x240@15 250k 32k/22.05kHz mono | wmv2 → wmav2 .asf | 包络 | MMS 流未被爬取保存，只有站点页面可引 |
| `cctv/2006` | [2006](http://web.archive.org/web/20060528214736/http://cctv.com:80/) | 384x288@25 / 350k | 384x288@25 350k 48k/22.05kHz mono | flv → libmp3lame .flv | 包络 | PAL 4:3 素材，25 fps |
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
| `youku/2007` | [2007](http://web.archive.org/web/20070602010215/http://www.youku.com/v_show/id_1001059.html) | 352x264@15 / 320k | 352x264@15 320k 32k/22.05kHz mono | flv → libmp3lame .flv | 包络 |  |
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
