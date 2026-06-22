# virtualMixer

A CLI virtual audio mixer for Windows that reproduces the **loopback** feature of audio
interfaces: it captures **PC playback (WASAPI loopback)** + one or more **microphones**,
mixes them with per-source volume balance, and lets you **record the mix to M4A (AAC)**
and/or **monitor** it through another output device.

Uses only Windows-standard APIs (WASAPI + Media Foundation) via
[NAudio](https://github.com/naudio/NAudio) — **no virtual audio driver required**.

> Scope note: this does *not* expose the mix as a virtual microphone that other apps
> (OBS/Zoom/Discord) can select — that needs a kernel audio driver (e.g. VB-CABLE).
> Here the mix goes to a **recording** and/or **live monitor** only.

## Requirements

- Windows 10/11
- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)

## Build & run

```powershell
dotnet build -c Release
dotnet run -c Release
# or run the built exe:
.\bin\Release\net8.0-windows\vmixer.exe
```

## Commands

| Command | Description |
|---|---|
| `devices` | list render (`R0..`) and capture (`C0..`) devices; `*` = system default |
| `refresh` | re-enumerate devices |
| `add-input loopback [Rn]` | add PC-playback loopback input (default = default render) |
| `add-input mic Cn` | add a microphone / line capture input |
| `inputs` | list current mixer inputs |
| `enable <id> on\|off` | route an input into the mix or mute it out |
| `vol <id> <0-200>` | input volume (100 = unity, up to 200 = +6 dB) |
| `monitor Rn` | play the mix out to render device `Rn` |
| `monitor off` | stop monitoring |
| `rec start [path]` | start recording (default `recordings/mix_yyyyMMdd_HHmmss.m4a`) |
| `rec stop` | stop and encode to M4A (AAC, 192 kbps) |
| `status` / `levels` | engine status / per-input peak meters |
| `quit` / `exit` | shut down cleanly |

## Example session

```
add-input loopback        # capture PC audio (default output)
add-input mic C3          # add your microphone
vol i0 80                 # PC audio at 80%
vol i1 120                # mic boosted to 120%
monitor R0                # hear the mix on headphones (a DIFFERENT device!)
rec start podcast.m4a     # start recording the mix
rec stop                  # -> podcast.m4a
quit
```

## Important: avoid feedback

Do **not** monitor through the same device you are loopback-capturing — the monitored mix
would be re-captured and blow up into a feedback loop. The app refuses this automatically
(guarded by endpoint ID). Capture your speakers, monitor on headphones (or vice-versa).

## How it works

```
WasapiLoopbackCapture ┐
WasapiCapture (mic) ───┼─ BufferedWaveProvider ─ (mono→stereo, resample to 48k) ─ Volume ─┐
                       │                                                                   ├─ MixingSampleProvider ─ RecordTap ─┬─ WasapiOut (monitor)
                       └───────────────────────────────────────────────────────────────────┘                                   └─ WaveFileWriter (temp WAV) ─ on stop → AAC/M4A
```

- Master mix format: 48 kHz / stereo / 32-bit float.
- Recording writes a temp WAV in real time, then transcodes to M4A (AAC) on `rec stop`
  and deletes the temp WAV.
- Exactly one puller drives the mixer at a time: the monitor `WasapiOut`, or — when
  recording without a monitor — a wall-clock-paced pump thread.
