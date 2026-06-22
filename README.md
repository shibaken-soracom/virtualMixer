# virtualMixer

A CLI virtual audio mixer for Windows that reproduces the **loopback** feature of audio
interfaces: it captures **PC playback (WASAPI loopback)** and one or more **microphones /
line inputs**, mixes them with per-source volume balance, and lets you **record the mix to
M4A (AAC)** and/or **monitor** it live through another output device.

Uses only Windows-standard APIs (WASAPI + Media Foundation) via
[NAudio](https://github.com/naudio/NAudio) — **no virtual audio driver required**.

> **Scope note:** this does *not* expose the mix as a virtual microphone that other apps
> (OBS / Zoom / Discord) can select — that needs a kernel audio driver (e.g. VB-CABLE).
> Here the mix goes to a **recording** and/or **live monitor** only.

## Requirements

- Windows 10 / 11
- .NET 8 SDK — `winget install Microsoft.DotNet.SDK.8`
- AAC encoding and >2-channel resampling use the **Media Foundation** runtime, present on
  all desktop SKUs. (Windows "N" editions need the *Media Feature Pack*.)

## Build & run

```powershell
dotnet build -c Release
dotnet run   -c Release
# …or run the built exe directly:
.\bin\Release\net8.0-windows\vmixer.exe
```

On launch the app prints the device list and a REPL prompt (`>`). Type `help` for the
command list. `<Tab>` auto-completes commands, sub-commands, device ids and input ids.

## Quick start

```text
add-input loopback        # capture PC audio (system default output)
add-input mic C3          # add your microphone (see 'devices' for the C-index)
vol i0 80                 # PC audio down to 80%
vol i1 120                # mic boosted to 120% (+~1.6 dB)
levels                    # watch the live meters, balance by ear/eye (Esc to return)
monitor R0                # hear the mix on headphones — a DIFFERENT device than captured!
rec start podcast.m4a     # start recording the mix
rec stop                  # stop → encodes to podcast.m4a
save podcast              # remember this setup as a preset
quit
```

## Commands

| Command | Description |
|---|---|
| `devices` | list render (`R0..`) and capture (`C0..`) devices; `*` = system default |
| `refresh` | re-enumerate devices (after plugging / unplugging hardware) |
| `add-input loopback [Rn]` | add a PC-playback loopback input (default = default render device) |
| `add-input mic Cn` | add a microphone / line capture input |
| `inputs` | list current mixer inputs (id / state / volume / device) |
| `enable <id> on\|off` | route an input into the mix, or mute it out |
| `vol <id> <0-200>` | set input volume (`100` = unity, up to `200` = +6 dB boost) |
| `monitor Rn` | play the mix out to render device `Rn` (to hear the balance) |
| `monitor off` | stop monitoring |
| `rec start [path]` | start recording (default `recordings/mix_yyyyMMdd_HHmmss.m4a`) |
| `rec stop` | stop recording → encodes to M4A (AAC, 192 kbps) |
| `status` | show engine status (format, monitor, recording, inputs) |
| `levels` | live per-input peak meters; `Esc` / `Enter` / `q` to return |
| `complete <text>` | show the Tab-completion candidates for a partial command |
| `save <name>` | save the current inputs + monitor selection as a preset |
| `load <name>` | load a preset (replaces the current setup) |
| `presets` | list saved presets |
| `help` | show the in-app help |
| `quit` / `exit` | shut down cleanly |

### Device ids

`devices` assigns stable indices per session: render endpoints are `R0`, `R1`, … and
capture endpoints are `C0`, `C1`, …. Render devices are used as **loopback sources** and
as the **monitor output**; capture devices are used as **mic / line inputs**. A bare number
is also accepted where a device id is expected (e.g. `add-input mic 3` ≡ `add-input mic C3`).

### Input ids

Each `add-input` creates an input with an id `i0`, `i1`, … (numbering resets when inputs
are cleared, e.g. by `load`). Use these ids with `enable` and `vol`.

## Live levels

`levels` opens a modal, in-place meter view: one bar per input on a **dBFS** scale
(`-60 dB` floor … `0 dB`), coloured **green** (ok) → **yellow** (hot) → **red** (clipping),
with the numeric peak and device name. A `!` after the id marks a muted/disabled input.
Press `Esc`, `Enter`, or `q` to return to the prompt. When stdin is redirected (piped /
scripted), it prints a single snapshot instead.

## Presets

`save <name>` writes the current inputs (kind, device, volume, enabled) and the monitor
selection to `presets/<name>.json`. `load <name>` clears the current setup and rebuilds it
from the file. Devices are matched by **stable endpoint id**, falling back to friendly name,
so presets survive re-enumeration; inputs whose device is missing are skipped with a notice.
Preset files are machine-specific (they store endpoint ids) and are git-ignored by default.

## Important: avoid feedback

Do **not** monitor through the same device you are loopback-capturing — the monitored mix
would be re-captured and blow up into a feedback loop. The app refuses this automatically
(guarded by endpoint id, both when adding a loopback input and when setting the monitor):
capture your speakers and monitor on headphones, or vice-versa.

## How it works

```text
WasapiLoopbackCapture ┐
                      ├─ BufferedWaveProvider ─ (mono→stereo, resample→48k) ─ Volume ─ Meter ─┐
WasapiCapture (mic) ──┘                                                                       │
                                                                                              ▼
                                                                                  MixingSampleProvider
                                                                                              │
                                                                                          RecordTap
                                                                                          ┌───┴────────────────────────────┐
                                                                                          ▼                                ▼
                                                                                  WasapiOut (monitor)      WaveFileWriter (temp WAV) ─ on stop → AAC / M4A
```

- **Master mix format:** 48 kHz / stereo / 32-bit float. Every input is normalised to this
  format on the pull side (mono→stereo + resample; `>2`-channel sources, e.g. 5.1 loopback,
  go through a Media Foundation resampler).
- **Capture path:** each WASAPI capture pushes into a 500 ms ring buffer
  (`DiscardOnBufferOverflow`), decoupling the device callback from the mixer pull.
- **Recording:** a tap between the mixer and its puller writes a temp `*.tmp.wav` in real
  time, guaranteeing the file is bit-identical to what the monitor hears; on `rec stop` the
  WAV is transcoded to M4A (AAC) via the built-in Media Foundation encoder and the temp WAV
  is deleted. If the app is torn down mid-recording, the raw WAV is kept as a salvage copy.
- **One puller invariant:** exactly one component drives the mixer at any time — the monitor
  `WasapiOut`, or (when there is no monitor) a wall-clock-paced background pump: the record
  pump while recording, or the meter-drive pump while the `levels` view is open.

## Project layout

| File | Responsibility |
|---|---|
| `Program.cs` | REPL, command parsing, Tab-completion wiring, live-levels rendering |
| `MixerEngine.cs` | the mixer, record tap, monitor, pull pumps, presets apply/export |
| `InputSource.cs` | one capture → ring buffer → format-normalise → volume → meter chain |
| `DeviceCatalog.cs` | WASAPI endpoint enumeration and stable `R`/`C` indexing |
| `Recorder.cs` | temp-WAV lifecycle and WAV→AAC/M4A transcode |
| `Preset.cs` | preset records and JSON load/save under `presets/` |
| `Meter.cs` | dBFS level-meter math (shared by live view and snapshot) |
| `LineEditor.cs` | minimal interactive line reader with context-aware Tab completion |

## Notes & limitations

- Output is **M4A (AAC) only**, fixed at 192 kbps. The intermediate WAV is bounded by the
  ~4 GB RIFF limit (≈6 h at 48 kHz/stereo/float).
- The mix is **not** routed to a virtual microphone — recording / monitoring only
  (see the scope note above).
- Tab completion and the live `levels` view are interactive-console features; under
  redirected stdin the editor falls back to plain `ReadLine` and `levels` prints a snapshot.

## License

[MIT](LICENSE) — free to use, modify, and distribute for any purpose, commercial or
not. The bundled dependency [NAudio](https://github.com/naudio/NAudio) is also MIT-licensed.
