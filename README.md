<div align="center">

<img src="icon.ico" alt="BareRecord" width="96" height="96" />

# BareRecord

**A small, fast, native Windows screen recorder. No bloat.**

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Single file](https://img.shields.io/badge/binary-~16MB-success)](#build)
[![License](https://img.shields.io/badge/license-MIT-blue)](#license)

One `.exe`. No installer. No login. No cloud. No telemetry. No AI nonsense.

</div>

---

## What it does

Records your screen to MP4 (H.264 + AAC) and stays out of the way.

| | |
|---|---|
| 🎥 **Capture** | Primary monitor, or any window via the system picker |
| 🔊 **Audio** | System audio loopback, microphone, or both mixed |
| ⏯ **Controls** | Pause / resume, auto-stop timer, global hotkey (`Ctrl+Alt+R`) |
| ⏳ **Countdown** | Optional 3-2-1 overlay before recording starts |
| 📁 **Output** | Configurable folder + filename template with `{yyyy}{MM}{dd}{HH}{mm}{ss}{counter}{source}` tokens |
| 🪟 **Tray** | Minimizes to tray while recording — your window doesn't appear in the capture |
| 🔔 **Toasts** | Quiet balloon notifications on start / save (click to open the file) |
| ⚙️ **Settings** | Persisted to `%LOCALAPPDATA%\BareRecord\settings.json` — survives across launches |

## What it deliberately doesn't do

> The goal is a recorder that opens, records, and gets out of the way. So no:

❌ AI features &nbsp; ❌ Cloud uploads &nbsp; ❌ Accounts &nbsp; ❌ Built-in editor &nbsp; ❌ Effects marketplace &nbsp; ❌ Auto-update nag screens &nbsp; ❌ Telemetry

If you need a NLE, use a NLE. BareRecord just records.

---

## Install

### Option A — grab the binary

Head to the [Releases](../../releases) page and download `BareRecord.exe`. That's the entire app — drop it anywhere and run it. It writes its settings to `%LOCALAPPDATA%\BareRecord\` and nothing else.

> **Why is the SmartScreen warning showing?** Because the binary isn't code-signed (signing costs money for a side project). Click "More info" → "Run anyway".

### Option B — build from source

```powershell
git clone https://github.com/j0shua-SYSON/BareRecord.git
cd BareRecord
dotnet publish -c Release -r win-x64 -p:PublishAot=false
```

Output: `bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\BareRecord.exe` (~16 MB, fully self-contained).

**For the smallest possible binary** (~6 MB Native AOT), install Visual Studio's "Desktop development with C++" workload and drop the `-p:PublishAot=false` flag.

---

## Usage

| Action | How |
|---|---|
| Start / stop recording | Click the big red button, or press `Ctrl+Alt+R` from anywhere |
| Pause / resume | Click "Pause" while recording (frames + audio drop, timeline stays continuous — no double-speed segment) |
| Pick a window | Choose "Window" in Source, then pick from the system picker |
| Set auto-stop | "Auto-stop after N sec" in Options (0 = off) |
| Show recording in folder | Right-click the tray icon → Show, or click the "Open Folder" button after saving |

Settings persist automatically. Restart the app and your last source/audio/output choices are still there.

---

## Architecture

BareRecord is intentionally a single small binary — most of its size is the trimmed WinForms surface, not application code.

```
┌─────────────────────────────────────────────────────────────────┐
│  UI/MainWindow.cs                   WinForms shell, state machine│
│    └─ MainWindow.Tray.cs            NotifyIcon, balloon toasts   │
│    └─ CountdownOverlay.cs           Click-through 3-2-1 overlay  │
├─────────────────────────────────────────────────────────────────┤
│  Recording/RecordingSession.cs      Capture pump → encoder       │
│  ┌──────────────┬──────────────┬────────────────────────────────┐│
│  │ Capture/     │ Audio/       │ Encoding/                      ││
│  │ D3D11        │ WasapiLoop-  │ MediaSinkWriter (H.264 + AAC)  ││
│  │ + WGC item   │ back + mic   │ via Media Foundation           ││
│  │ factory      │ + ring mix   │                                ││
│  └──────────────┴──────────────┴────────────────────────────────┘│
├─────────────────────────────────────────────────────────────────┤
│  Settings.cs       JSON, source-gen for AOT, token-expanded names│
│  Hotkeys/          Global Ctrl+Alt+R via RegisterHotKey          │
└─────────────────────────────────────────────────────────────────┘
```

### Why the choices

- **WinForms over WPF** — same `~16 MB` trimmed footprint as raw Win32, but with built-in `NotifyIcon`, `ToolTip`, `NumericUpDown`, and `FolderBrowserDialog` so we don't reinvent them.
- **Hand-rolled COM via vtable function pointers** — declaring 5 D3D11 + 10 Media Foundation methods by vtable index is shorter than pulling in Vortice or NAudio, and keeps the dependency graph small.
- **Windows.Graphics.Capture for video** — DPI-aware, hardware-backed, handles fullscreen exclusive apps gracefully.
- **WASAPI loopback for system audio** — captures whatever's playing on the default render endpoint without a virtual cable. A second WASAPI client on the capture endpoint handles the mic; the two streams are mixed at write-time on the system audio's cadence (with a ring buffer to absorb mic jitter).
- **Media Foundation Sink Writer for muxing** — H.264 + AAC into MP4, hardware-accelerated encoder when one is available.
- **Source-gen JSON** — `System.Text.Json` reflection-free, so the app stays compatible with full trimming and Native AOT.

### The hard bits

- **Pause/resume timestamp stitching.** During a pause, frames and audio packets are dropped; on resume, video timestamps are shifted back by the cumulative paused duration so the MP4 has continuous timing with no double-speed segment. Audio stays in sync because the byte counter that drives audio timestamps doesn't tick while paused.
- **D3D11 multithread protection.** WGC fires frames on its own thread and the encoder MFT may touch the device context concurrently with our staging-copy. `ID3D11Multithread::SetMultithreadProtected(TRUE)` is non-negotiable here.
- **Countdown overlay.** Full-screen click-through (`WS_EX_TRANSPARENT`) with a magenta transparency key. The visible pill has to be drawn with fully opaque colors — semi-transparent fills blend with the magenta and render as dark purple instead of being keyed out.

---

## Requirements

- **Windows 10 1903 (build 18362)** or newer, x64 — required for Windows.Graphics.Capture
- **GPU with D3D11 feature level 10.0+** — almost any GPU from the last decade
- **No .NET runtime install needed** — the published binary is self-contained

---

## Project layout

```
BareRecord.csproj           # net9.0-windows, x64, single-file + trim
app.manifest                # PerMonitorV2 DPI awareness
icon.ico                    # Multi-res app icon

Program.cs                  # Application.Run(new MainWindow())
Settings.cs                 # JSON-persisted user settings + filename template

UI/
  MainWindow.cs             # State machine (Idle/Recording/Paused/Saved)
  MainWindow.Tray.cs        # NotifyIcon, show/hide, balloon toasts
  CountdownOverlay.cs       # Full-screen 3-2-1 overlay

Recording/
  RecordingSession.cs       # Owns capture → encoder pipeline + pause logic

Capture/
  CaptureItemFactory.cs     # IGraphicsCaptureItemInterop + picker
  D3D11.cs                  # Hand-rolled D3D11 via vtable function pointers
  MonitorEnumerator.cs      # GetPrimaryMonitor via MonitorFromPoint
  DisplayDiagnostics.cs     # Troubleshooting dump (EnumDisplayMonitors,
                            #   EnumDisplayDevices, DXGI EnumOutputs,
                            #   QueryDisplayConfig)

Audio/
  WasapiLoopback.cs         # System loopback OR mic capture, normalised
                            #   to 16-bit stereo PCM at device rate
  PcmRingBuffer.cs          # Lock-protected byte ring for mic mixing

Encoding/
  Mf.cs                     # Media Foundation P/Invoke + vtable calls
  MediaSinkWriter.cs        # H.264 + AAC → MP4 sink writer

Hotkeys/
  GlobalHotkey.cs           # RegisterHotKey/UnregisterHotKey wrapper

Win32/
  Native.cs                 # Minimal P/Invoke for WM_HOTKEY + MessageBox
```

---

## Settings schema

`%LOCALAPPDATA%\BareRecord\settings.json`:

```json
{
  "Source": "Primary",
  "AudioSystem": true,
  "AudioMic": false,
  "OutputFolder": "C:\\Users\\you\\Videos\\BareRecord",
  "Fps": 30,
  "ShowCursor": true,
  "Countdown": false,
  "AutoStopSeconds": 0,
  "FilenameTemplate": "BareRecord-{yyyy}{MM}{dd}-{HHmmss}",
  "Counter": 0,
  "MinimizeToTrayWhileRecording": true,
  "Notifications": true
}
```

Filename template tokens: `{yyyy} {MM} {dd} {HH} {mm} {ss} {HHmmss} {source} {counter}` — anything else passes through literally. Invalid filename characters are replaced with `_` after substitution.

---

## Troubleshooting

Click **Troubleshooting** in the bottom-right of the window to dump display state to `%TEMP%\BareRecord-displays.log`. Useful when "my monitor isn't showing up". (Common cause: duplicate/mirror mode means only one of the duplicates is capturable.)

If audio doesn't work, check that your default render device runs at one of the AAC encoder's supported rates: `8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000` Hz. Almost every modern device does (typically 48000).

If recording fails to start with "Hotkey unavailable", another app is already holding `Ctrl+Alt+R`.

---

## License

[MIT](LICENSE).

---

<div align="center">
<sub>Built with <a href="https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture">Windows.Graphics.Capture</a>, <a href="https://learn.microsoft.com/en-us/windows/win32/medfound/microsoft-media-foundation-sdk">Media Foundation</a>, and a healthy aversion to bloat.</sub>
</div>
