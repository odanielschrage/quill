# quill

A minimal, fully local meeting recorder + transcriber. One tray click records
your mic and all system audio as two separate tracks; when you stop, quill
transcribes both on-device and writes a speaker-tagged transcript. Nothing ever
leaves the machine.

Named for the feather. Sibling of [parrot](https://github.com/digimata/parrot), same skeleton: single
binary, tray icon, no app bundle.

## Platforms

Two native implementations, one on-disk format. Nothing is shared at the code
level — AppKit, AVFoundation, Core Audio and Core ML have no Windows
counterparts — so the platform layers are reimplemented rather than ported.

| | macOS | Windows |
|---|---|---|
| source | [`Sources/quill`](Sources/quill) — Swift | [`windows/`](windows) — C#/.NET 8 |
| requires | macOS 15+ | Windows 10 1607+ |
| mic | AVAudioEngine | WASAPI capture |
| system audio | Core Audio process tap | WASAPI loopback |
| tracks | AAC in CAF | 16 kHz mono WAV |
| transcription | Parakeet TDT 0.6B v2 (Core ML) | Whisper (whisper.cpp / GGML) |
| UI | NSStatusItem | NotifyIcon |
| launch at login | LaunchAgent | `HKCU\…\Run` |

`meta.json`, `transcript.json` and `transcript.md` are identical in shape on both,
so `on_stop` hooks and anything downstream are portable. Only the track filenames
differ, and those are read from `meta.json` rather than hardcoded.

Windows specifics — benchmark numbers, the loopback silence problem, `doctor`
checks — are in [`windows/README.md`](windows/README.md).

## Install

**macOS**

```sh
cd quill
swift build -c release
sudo cp .build/release/quill /usr/local/bin/quill
quill install --launch-at-login   # optional — runs in the background on login
```

Core Audio process taps mean no virtual device and no kernel extension. Apple
Silicon recommended for transcription speed.

**Windows**

```sh
cd quill/windows
dotnet publish Quill.Win/Quill.Win.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

Produces one self-contained `quill.exe` (~70 MB — it carries the .NET runtime and
whisper.cpp). Copy it somewhere stable, then optionally
`quill install --launch-at-login`. Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download)
to build; nothing to install to run it.

`IncludeAllContentForSelfExtract` is load-bearing, not decoration — see
[`windows/README.md`](windows/README.md#packaging).

## How to use

1. **Run it** (`quill` in a terminal, or at login).
2. **Click the feather in the menu bar / notification area → Start recording.**
   First use prompts for microphone access — and on macOS, System Audio Recording
   as well. While recording, the icon turns red with a running elapsed counter.
3. **Click → Stop recording** when the meeting ends. Transcription starts
   automatically (the menu shows progress); a notification fires when the
   transcript is ready.

Each session lands in `~/Recordings/<yyyy.MM.dd-HHmm>/`:

| File | Contents |
|---|---|
| `mic.caf` / `mic.wav` | your side (default input device) |
| `system.caf` / `system.wav` | everything the machine played — the other side of the call |
| `meta.json` | start/end timestamps, duration, per-track start offsets |
| `transcript.json` | canonical transcript — engine provenance + timed, speaker-tagged segments |
| `transcript.md` | the same transcript rendered for reading |
| `transcribe.log` | transcription progress/errors for this session |

Two tracks on purpose: speech models do better on clean single-source audio,
and mic-vs-system is free two-party diarization — `me` vs `them` with no
speaker-identification model.

Crash-tolerance on purpose too, by different means. CAF needs no finalization
pass, so if the process dies mid-meeting everything already written is still
readable. WAV can't do that, but a truncated one is recoverable by rebuilding its
header, and the samples are flushed every few seconds.

## Transcription

Built in, on-device, automatic.

**macOS** uses **Parakeet TDT 0.6B v2** (English) via
[FluidAudio](https://github.com/FluidInference/FluidAudio)'s Core ML port —
roughly 20 seconds per hour of audio on Apple Silicon.

**Windows** uses **Whisper** via [Whisper.net](https://github.com/sandrohanea/whisper.net),
because Parakeet v2 is English-only and Core ML. Whisper is multilingual, and on
CPU it is far slower than the Apple Silicon figure above — see the measured
numbers in [`windows/README.md`](windows/README.md) before setting expectations.

Models (hundreds of MB) download once on first transcription and live outside the
repo; `quill doctor` tells you whether they're already cached so you're never
downloading after an important meeting.

Each track is transcribed separately, shifted by its start offset so both share
one clock, and merged by timestamp. Jobs run in a serial queue — you can start a
new recording while the last one transcribes. Unfinished jobs resume on next
launch (the filesystem is the queue: a session with `meta.json` but no
`transcript.json` is pending). Failures append to the session's `transcribe.log`
and never block later jobs.

The engine sits behind a small protocol, so a second engine per platform is an
implementation rather than a rewrite.

## Config

Optional, at `~/.config/quill/config.json` — on Windows,
`%APPDATA%\quill\config.json`, with the Unix path still read so one dotfiles repo
serves both.

```json
{
  "recordings_dir": "~/Recordings",
  "transcription": { "enabled": true, "engine": "parakeet" },
  "on_stop": "my-hook"
}
```

- `recordings_dir` — where sessions land. Resolution order: `--out` flag >
  config > `~/Recordings`.
- `transcription.enabled` — set `false` to just record.
- `transcription.engine` — `parakeet` on macOS, `whisper` on Windows.
- `transcription.model`, `transcription.language` — Windows only. Whisper is
  multilingual and comes in several sizes; Parakeet is neither.
- `mic_voice_processing` — macOS only, and off by default. Apple's echo
  cancellation on the mic; set `true` when recording meetings through the
  speakers, so playback doesn't bleed into the mic track and get transcribed
  twice as "me". The trade: while the voice unit is live, macOS ducks other
  playback slightly (`.min` ducking is configured, but it can't be zeroed). On
  headphones there's no echo to cancel, so raw capture is the better default.
- `on_stop` — command spawned with the session directory as its argument,
  **after the transcript is written** (or right after recording if transcription
  is disabled). Wire it to whatever comes next: summarization, filing, indexing.

`QUILL_CONFIG` (Windows) points at a different config file, for a second profile.

## CLI

```sh
quill                        # run the tray daemon (^C to quit, or quit from the icon)
quill run --out <dir>        # custom recordings root (default ~/Recordings)
quill doctor                 # check permissions, devices, recordings folder, models
quill install --launch-at-login
quill install --uninstall
```

The Windows build carries a few extra dev harnesses — `bench`, `gaptest`,
`transcribe` — documented in [`windows/README.md`](windows/README.md).

## Stack

**macOS** — Swift, single SPM executable target

- **Core Audio process tap** (`AudioHardwareCreateProcessTap`, macOS 14.2+) —
  system audio capture via a private aggregate device
- **AVAudioEngine** — mic capture
- **AVAudioFile** — streaming AAC encode into CAF
- **FluidAudio / Parakeet** — on-device Core ML transcription
- **NSStatusItem** — the whole UI

**Windows** — C#, single self-contained executable

- **WASAPI loopback** ([NAudio](https://github.com/naudio/NAudio)) — system audio
  capture, no consent prompt and no virtual device
- **WASAPI capture** — mic
- **Whisper.net** — on-device whisper.cpp transcription
- **NotifyIcon** — the whole UI

## Gotchas

- A global tap records *everything* the machine plays — notification dings,
  music, all of it. Don't play Spotify during meetings (or ask for a
  per-process picker if it bothers you).
- Parakeet v2 is English-only, so the macOS build is too. Use the Windows build,
  or wait for a Whisper engine on macOS, for other languages.
- **macOS:** if recordings come out silent, check System Settings → Privacy &
  Security → Screen & System Audio Recording. The binary embeds its Info.plist
  (`__TEXT,__info_plist`) so TCC can attribute permissions to quill itself when
  running as a LaunchAgent.
- **Windows:** if the mic track is silent, check Settings → Privacy & security →
  Microphone — including *"Let desktop apps access your microphone"*, which is a
  separate switch from the global one and the one people miss. `quill doctor`
  checks both.
- **Windows:** transcription on CPU is nothing like Apple Silicon's Neural
  Engine. Measure your machine with `quill bench` before assuming a model size.
