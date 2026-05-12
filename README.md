# BareRecord

A native Windows screen recorder. One self-contained executable. No installer, no runtime dependencies, no telemetry.

[![CI](https://github.com/j0shua-SYSON/BareRecord/actions/workflows/ci.yml/badge.svg)](https://github.com/j0shua-SYSON/BareRecord/actions/workflows/ci.yml)
[![Release](https://github.com/j0shua-SYSON/BareRecord/actions/workflows/release.yml/badge.svg)](https://github.com/j0shua-SYSON/BareRecord/actions/workflows/release.yml)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

## Overview

BareRecord captures the screen to an MP4 (H.264 video, AAC audio) using only the Windows APIs that ship with the operating system. The published binary is a single executable of approximately 16 MB; no .NET runtime needs to be installed on the target machine.

The project's design constraint is minimalism. The application starts, records, and stays out of the way.

## Features

- Primary monitor and window capture, the latter via the system Graphics Capture picker
- System audio loopback and microphone capture, individually or mixed
- Pause and resume with continuous-timestamp output (no double-speed segment on resume)
- Configurable auto-stop timer
- Optional three-second countdown overlay before recording starts
- Global hotkey (`Ctrl+Alt+R`) usable while the application is minimized to the system tray
- Filename templates with date, source, and counter tokens
- System-tray integration with click-to-restore and balloon notifications
- Settings persisted to `%LOCALAPPDATA%\BareRecord\settings.json`

## Non-features

The following are intentionally omitted:

- Artificial intelligence features
- Cloud upload, sync, or account requirements
- A built-in video editor
- Effect or template marketplaces
- Auto-update prompts
- Telemetry or analytics

If a full non-linear editor or a hosted service is required, BareRecord is not the right tool.

## Installation

### Download a release

Prebuilt binaries are available on the [Releases](../../releases) page. Download `BareRecord-vX.Y.Z-win-x64.exe` and run it. The application writes its configuration to `%LOCALAPPDATA%\BareRecord\` and does not modify the registry or install services.

The binary is not currently code-signed; Windows SmartScreen may display a warning on first launch.

### Build from source

```powershell
git clone https://github.com/j0shua-SYSON/BareRecord.git
cd BareRecord
dotnet publish -c Release -r win-x64 -p:PublishAot=false
```

The resulting binary is at:

```
bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\BareRecord.exe
```

For a smaller binary (approximately 6 MB) using Native AOT, install Visual Studio 2022 with the "Desktop development with C++" workload, then omit the `-p:PublishAot=false` flag.

## Usage

| Action | Method |
|---|---|
| Start or stop recording | Click the record button, or press `Ctrl+Alt+R` from any application |
| Pause or resume | Click the Pause button while recording |
| Capture a specific window | Set Source to *Window*, then choose the target window in the system picker |
| Configure auto-stop | Set the *Auto-stop after* value in the Options section (0 disables) |
| Open the saved recording | Click *Open File* or *Open Folder* after saving, or click the toast notification |

Settings are persisted automatically on change.

## Requirements

- Windows 10 version 1903 (build 18362) or newer, 64-bit
- A GPU supporting Direct3D 11 feature level 10.0 or higher

The published binary is self-contained; no additional runtime installation is required.

## Architecture

The application is organized around a single `RecordingSession` that coordinates three independent capture sources and one Media Foundation sink writer.

```
+--------------------------------------------------------------------+
|  UI/MainWindow.cs                  WinForms shell, state machine    |
|    +- MainWindow.Tray.cs           NotifyIcon, balloon notifications|
|    +- CountdownOverlay.cs          Click-through 3-2-1 overlay      |
+--------------------------------------------------------------------+
|  Recording/RecordingSession.cs     Capture pump and encoder driver  |
|  +--------------+----------------+----------------------------------+
|  | Capture/     | Audio/         | Encoding/                        |
|  | D3D11 and    | WasapiLoopback | MediaSinkWriter (H.264 + AAC)    |
|  | WGC item     | (system + mic) | via Media Foundation             |
|  | factory      | + ring mix     |                                  |
|  +--------------+----------------+----------------------------------+
+--------------------------------------------------------------------+
|  Settings.cs       JSON, source-generated, token-expanded filenames |
|  Hotkeys/          Global Ctrl+Alt+R via RegisterHotKey             |
+--------------------------------------------------------------------+
```

### Components

- **Capture.** Windows.Graphics.Capture API. Frames are delivered via Direct3D 11 surfaces; a staging texture is used to bring pixel data back to system memory for the encoder.
- **Audio.** WASAPI loopback on the default render endpoint for system audio, and shared-mode capture on the default capture endpoint for microphone. Both streams are normalized to 16-bit stereo PCM at the device's native sample rate.
- **Encoding.** Media Foundation Sink Writer mux H.264 video and AAC audio into an MP4 container. Color conversion (BGRA to NV12) is delegated to the sink writer's built-in color-converter MFT.
- **User interface.** WinForms, with a `NotifyIcon` for tray integration and a topmost layered window for the countdown overlay.
- **Persistence.** Settings are serialized via `System.Text.Json` source generators, keeping the application compatible with full IL trimming and Native AOT.

### Design decisions

- **WinForms instead of WPF.** WPF accounts for approximately 120 MB of additional weight in a self-contained build. WinForms produces an equivalent ~16 MB binary while keeping built-in `NotifyIcon`, `ToolTip`, `NumericUpDown`, and `FolderBrowserDialog` available without reimplementation.
- **Hand-written COM interop.** Declaring five Direct3D 11 methods and ten Media Foundation methods via vtable function pointers is shorter than introducing Vortice or NAudio as dependencies, and avoids pulling in unused API surface.
- **Source-generated JSON.** Eliminates reflection from the settings code path so the application can be fully trimmed.

### Implementation notes

- **Pause and resume.** During a pause, both video frames and audio packets are discarded. On resume, video timestamps are offset by the cumulative paused duration so the resulting MP4 has continuous timing with no gap and no double-speed segment. Audio remains in sync because the byte counter that drives audio timestamps does not advance during pause.
- **Concurrent device access.** Windows.Graphics.Capture delivers frames on its own thread, and the Media Foundation encoder MFT may access the Direct3D 11 device context concurrently with the staging copy. `ID3D11Multithread::SetMultithreadProtected` is enabled on the device to serialize concurrent access.
- **Microphone mixing.** When both system audio and microphone are captured, the system loopback drives the encoder's audio timeline. Microphone packets are buffered into a one-second ring and dequeued by the system-audio handler at write time, then mixed sample-by-sample (saturating addition) before the buffer is handed to the sink writer.
- **Countdown overlay.** A full-screen click-through window (`WS_EX_TRANSPARENT`) uses magenta as both the background color and `Form.TransparencyKey`. The visible elements must be drawn with fully opaque colors; semi-transparent fills would alpha-blend with the magenta background and fail to be keyed out correctly.

## Project layout

```
BareRecord.csproj           Project file (net9.0-windows, x64)
app.manifest                PerMonitorV2 DPI awareness
icon.ico                    Multi-resolution application icon

Program.cs                  Application entry point
Settings.cs                 JSON-persisted settings and filename templates

UI/
  MainWindow.cs             State machine (Idle / Recording / Paused / Saved)
  MainWindow.Tray.cs        NotifyIcon, balloon notifications, show/hide
  CountdownOverlay.cs       Full-screen 3-2-1 overlay

Recording/
  RecordingSession.cs       Owns the capture-to-encoder pipeline

Capture/
  CaptureItemFactory.cs     IGraphicsCaptureItemInterop, system picker
  D3D11.cs                  Hand-rolled Direct3D 11 vtable interop
  MonitorEnumerator.cs      Primary monitor lookup
  DisplayDiagnostics.cs     Multi-API display dump for troubleshooting

Audio/
  WasapiLoopback.cs         WASAPI loopback or capture, normalized PCM
  PcmRingBuffer.cs          Lock-protected ring used during mic mixing

Encoding/
  Mf.cs                     Media Foundation P/Invoke and vtable calls
  MediaSinkWriter.cs        H.264 + AAC sink writer

Hotkeys/
  GlobalHotkey.cs           RegisterHotKey wrapper

Win32/
  Native.cs                 Minimal P/Invoke surface (WM_HOTKEY, MessageBox)
```

## Settings reference

`%LOCALAPPDATA%\BareRecord\settings.json`:

```json
{
  "Source": "Primary",
  "AudioSystem": true,
  "AudioMic": false,
  "OutputFolder": "C:\\Users\\<user>\\Videos\\BareRecord",
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

### Filename template tokens

| Token | Replacement |
|---|---|
| `{yyyy}` | Four-digit year |
| `{MM}` | Two-digit month |
| `{dd}` | Two-digit day |
| `{HH}` | Two-digit hour, 24-hour |
| `{mm}` | Two-digit minute |
| `{ss}` | Two-digit second |
| `{HHmmss}` | Concatenation of hour, minute, second |
| `{source}` | Capture source (`Primary`, `Window`, `Region`) |
| `{counter}` | Zero-padded sequence number, monotonically increasing per recording |

Unrecognized tokens pass through literally. Characters that are invalid in Windows filenames are replaced with `_` after substitution.

## Troubleshooting

The *Troubleshooting* button in the application window writes a diagnostic dump to `%TEMP%\BareRecord-displays.log`. The dump includes the output of `EnumDisplayMonitors`, `EnumDisplayDevices`, the DXGI adapter and output enumeration, and `QueryDisplayConfig`. This is the first thing to consult when a display is not detected.

A common cause of a missing display is duplicate or mirror display mode, in which only one of the duplicated outputs is independently capturable by Windows.Graphics.Capture.

If audio is not recorded, verify that the default render device runs at one of the AAC encoder's supported sample rates: 8000, 11025, 16000, 22050, 24000, 32000, 44100, or 48000 Hz. Most modern audio devices default to 48000 Hz.

If the application reports *Hotkey unavailable*, another application is already registered for `Ctrl+Alt+R`. The hotkey is the only one currently used by BareRecord.

## Continuous integration

Every push to `main` and every pull request triggers a Windows build via the [CI workflow](.github/workflows/ci.yml), which restores, builds with `/warnaserror`, publishes a single-file binary, and uploads it as a workflow artifact.

Pushing a tag of the form `vMAJOR.MINOR.PATCH` triggers the [release workflow](.github/workflows/release.yml), which builds the same binary, attaches it to a GitHub release, and auto-generates release notes from commits since the previous tag.

To cut a release from the command line:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The release workflow can also be invoked manually from the Actions tab.

## License

Released under the [GNU General Public License v3.0](LICENSE).

```
BareRecord -- a native Windows screen recorder.
Copyright (C) 2026 j0shua-SYSON

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
```
