# Xiaomi SMB Viewer

A Windows desktop viewer that turns a folder of one-minute Xiaomi camera clips into a **single scrubbable timeline for the whole day**.

Xiaomi cameras write motion-triggered recordings to an SD card or SMB share as thousands of one-minute MP4 files, split across one folder per hour. Reviewing footage means opening each clip by hand. This app indexes a whole day, draws it as one timeline with the gaps visible, and plays across file boundaries as if it were a single recording.

## Why not a browser-based player

The recordings are **HEVC video with `pcm_alaw` (G.711) audio** in an MP4 container. Chromium — and therefore Electron, Tauri/WebView2, and any `<video>` element — cannot play `pcm_alaw` in MP4 at all, and needs a Store extension for HEVC. Playing these files in a web view requires transcoding on the fly, which defeats the point of hardware decoding. This app uses **LibVLC** instead, which decodes both natively with D3D11VA hardware acceleration.

## Features

- **One timeline for the entire day** — zoom from 24 hours down to 20 seconds with the scroll wheel, pan, and scrub. Gaps between motion-triggered clips are shown as gaps, not skipped silently.
- **Seamless playback across clips** — the player chains one-minute files automatically and jumps over recording gaps.
- **Hardware-accelerated decoding** (D3D11VA) for HEVC at 2304×1296 and above.
- **Speed control** — 0.5× to 16× for fast review of a full day.
- **Thumbnail preview on hover** — frames extracted with ffmpeg, cached on disk and in memory.
- **Clip export** — select a range on the timeline and save it as one MP4, either stream-copied (fast) or re-encoded to H.264/AAC (exact cuts).
- **Multi-camera comparison** — up to four cameras in a grid, driven by one shared clock with automatic drift correction.
- **12 languages** with automatic detection from the system locale, plus manual override.

## Requirements

- Windows 10/11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- **ffmpeg** and **ffprobe** on `PATH`, or placed next to the executable — optional, but without them thumbnails, export, and exact clip durations are disabled

LibVLC ships with the application through the `VideoLAN.LibVLC.Windows` NuGet package; no separate VLC installation is needed.

## Build

```bash
git clone https://github.com/tramix32/XiaomiSMBViewer.git
cd XiaomiSMBViewer
dotnet build -c Release
```

The executable lands in `bin/Release/net10.0-windows/`.

## Expected folder layout

Point the app at the `xiaomi_camera_videos` folder — a mapped drive, a UNC path, or a local copy:

```
Z:\xiaomi_camera_videos\
└── 94f827706de8\          ← camera ID (MAC address)
    ├── 2026081718\        ← YYYYMMDDHH, local time
    │   ├── 02M59S_1786982579.mp4
    │   ├── 05M31S_1786982731.mp4
    │   └── ...
    └── 2026081719\
        └── ...
```

The folder name plus the `MMmSSs` prefix give the **local** wall-clock time of each clip, and that is what the app uses to place clips on the timeline. The trailing number is a **UTC** Unix timestamp, kept only as a cross-check — mixing the two is the classic source of an hour-off timeline. The alternative `HHMMSS.mp4` naming used by some firmware builds is also recognised.

## Usage

| Action | Control |
|---|---|
| Seek | Click or drag on the timeline |
| Zoom | Scroll wheel over the timeline |
| Pan | Right-drag or middle-drag |
| Reset zoom | Double-click, or `0` |
| Select a range | `Shift` + drag, or the `[` and `]` buttons |
| Compare cameras | Ctrl-click several cameras in the sidebar |

### Keyboard shortcuts

| Key | Action |
|---|---|
| `Space` | Play / pause |
| `←` / `→` | ±5 seconds (`Shift`: ±60 seconds) |
| `↑` / `↓` | ±5 minutes |
| `,` / `.` | Previous / next clip |
| `Home` / `End` | First / last recording of the day |
| `1` … `5` | Speed 1× / 2× / 4× / 8× / 16× |
| `S` / `E` | Set selection start / end |
| `0` | Reset timeline zoom |
| `F11` | Fullscreen |
| `Esc` | Leave fullscreen |

## How the day index works

Scanning an SMB share is the slow part, so indexing happens in two passes:

1. **Immediate** — the file listing alone gives every clip's start time, with durations assumed to be 60 seconds and clamped so clips never overlap. The timeline is usable right away.
2. **Background** — `ffprobe` reads the real duration of every clip, six at a time, and the result is cached under `%LocalAppData%\XiaomiSMBViewer\index\{camera}\{yyyyMMdd}.json`. Reopening that day is then instant.

Thumbnails are cached in `%LocalAppData%\XiaomiSMBViewer\thumbs`. Deleting either folder is safe; it only costs a re-scan.

## Export

Export uses the ffmpeg `concat` demuxer with `inpoint`/`outpoint` directives.

- **Stream copy** (default) is near-instant and preserves the original HEVC, but cuts land on the nearest keyframe.
- **Re-encode** produces H.264/AAC with exact cut points and plays anywhere, at the cost of encoding time.

## Localization

Translations are plain JSON files in `Locales/`, copied next to the executable at build time:

```
Locales/en.json  ar  de  es  fr  hi  it  ja  pl  pt  ru  zh
```

On first run the app matches your Windows UI language; the sidebar dropdown overrides it and the choice is remembered. English fills in any key a translation is missing, so a partial file is safe.

**To add a language:** copy `en.json` to `<code>.json` (an ISO 639-1 code), translate the values, set `lang.name` to the language's own name, and restart. It appears in the dropdown automatically — no rebuild required. Right-to-left scripts are handled; the video pane and timeline stay left-to-right by design.

## Configuration

Settings live in `%AppData%\XiaomiSMBViewer\settings.json` — library root, language, hardware decoding, volume, and the last camera used.

## Known limitations

- Windows only. LibVLCSharp's `VideoView` is a native window hosted through WinForms interop, which is also why the thumbnail preview is a top-level popup rather than an overlay.
- At 8× and 16× the decoder handles 160–320 fps of 2304×1296 video; older GPUs will drop frames.
- Multi-camera panes re-sync only when drift exceeds 1.5 seconds, so they are frame-accurate to about a second, not exactly.
- Seeking while dragging is throttled to five real seeks per second — each one reopens a file over SMB.

## License

This project is released under the [MIT License](LICENSE).

### Third-party components

The application code is MIT, but it ships with and depends on components under other licenses:

| Component | License | How it is used |
|---|---|---|
| [LibVLCSharp](https://github.com/videolan/libvlcsharp) | LGPL-2.1-or-later | NuGet reference, dynamically linked |
| [libVLC](https://www.videolan.org/vlc/libvlc.html) (`VideoLAN.LibVLC.Windows`) | LGPL-2.1-or-later | Native DLLs copied next to the executable |
| [ffmpeg / ffprobe](https://ffmpeg.org/) | LGPL or GPL, depending on the build | **Not bundled.** Invoked as an external process for thumbnails, durations, and export |

libVLC is used through dynamic linking and its DLLs sit next to the executable, so they remain replaceable — which is what the LGPL asks for. ffmpeg is never linked or redistributed; the app only calls whatever `ffmpeg.exe` the user already has, so its license stays that user's concern.
