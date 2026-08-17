A Windows desktop viewer that turns a folder of one-minute Xiaomi camera clips into a single scrubbable timeline for the whole day.

## Which download

| Asset | Size | Requires |
|---|---|---|
| `XiaomiSMBViewer-{{VERSION}}-win-x64.zip` | {{SIZE_FDD}} MB | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| `XiaomiSMBViewer-{{VERSION}}-win-x64-selfcontained.zip` | {{SIZE_SCD}} MB | nothing, runtime included |

Unpack anywhere and run `XiaomiSMBViewer.exe`. Keep the `libvlc` and `Locales` folders next to it.

Install [ffmpeg](https://ffmpeg.org/) and put `ffmpeg.exe` / `ffprobe.exe` on `PATH` (or beside the executable) to enable hover thumbnails, clip export, and exact segment durations. The app runs without them, with those three features disabled.

## Highlights

- One timeline for a whole day, zoomable from 24 hours down to 20 seconds, with recording gaps shown as gaps
- Seamless playback across per-minute files, skipping gaps automatically
- D3D11VA hardware decoding of HEVC at 2304x1296, including the `pcm_alaw` audio that browser-based players reject
- Playback speed 0.5x to 16x
- Thumbnail preview on timeline hover, cached on disk
- Range export to a single MP4, stream-copied or re-encoded to H.264/AAC
- Up to four cameras side by side on one shared clock
- 12 languages, auto-detected from the system locale, switchable at runtime

Windows x64 only.
