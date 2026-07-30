# quill for Windows

Native Windows implementation, sibling to the Swift/macOS build in `../Sources`.
Same idea, same on-disk contract, different platform layer — AppKit,
AVFoundation, Core Audio and Core ML have no Windows counterparts, so the
capture, transcription, tray and autostart layers are reimplemented rather than
ported.

Plan and rationale: [`../.issues/plan-001-windows-port.md`](../.issues/plan-001-windows-port.md).

## Status

| Phase | Scope | State |
|---|---|---|
| 0 | .NET 8 SDK | done |
| 1 | Pipeline: config, session, transcript contract, queue | **done** |
| 2 | WASAPI capture (mic + loopback) | **done** |
| 3 | Whisper transcription + model benchmark | **done** |
| 4 | Tray icon, notifications, session-end handling | next |
| 5 | `doctor`, `install --launch-at-login`, real CLI | pending |
| 6 | Single-file publish + docs | pending |

`Program.cs` is a placeholder until phase 5, but it carries the harnesses this
layer was verified with:

```sh
quill record 15              # capture both tracks for 15s, then transcribe
quill transcribe <dir>       # re-run the queue on a session folder
quill gaptest                # acceptance check — plays a tone, verifies the timeline
quill bench <wav> [ref.txt]  # time every model
```

## Build

```sh
cd windows
dotnet test
```

## The silence-gap ledger

WASAPI loopback stops delivering buffers entirely while nothing is playing,
unlike the macOS Core Audio process tap. Left alone, every silence in a meeting
would collapse: the system track would come out shorter than real time, its
timestamps would drift out of step with the mic track, and merging the two by
timestamp — how quill gets two-party diarization for free — would attribute
speech to the wrong side.

[`TrackWriter`](Quill.Win/Audio/TrackWriter.cs) therefore keeps a ledger of
elapsed monotonic time against samples written, and closes any shortfall with
digital silence. Monotonic rather than wall-clock on purpose: the ledger turns
elapsed time into a sample position, so an NTP correction or a DST change
mid-meeting would otherwise inject or swallow minutes of silence.

Verified on real hardware via `quill gaptest`: a 12s capture with audio only in
seconds 0–3 and 7–10 produced a 12.36s track with 6.09s inserted, rather than
collapsing to the ~6s that were audible. Confirmed empirically that loopback
really does deliver nothing during silence — a capture with nothing playing
produced a zero-byte track.

Silence *before* the first buffer needs no ledger — `start_offset_ms` in
`meta.json` carries that skew, and the merge shifts by it. Worth knowing: WASAPI
took ~2s to deliver its first buffer on a 2017 dual-core laptop, so that skew is
not hypothetical.

## Transcription

Whisper via [Whisper.net](https://github.com/sandrohanea/whisper.net) (whisper.cpp
/ GGML), on-device, Q5_0 quantized. Weights download once into
`%LOCALAPPDATA%\quill\models\` — a runtime artifact, never in the repo or the
binary, the same arrangement as FluidAudio's Parakeet cache on macOS.

The tracks are already mono 16 kHz PCM WAV, which is exactly whisper.cpp's input
format, so transcription reads them straight off disk with no decode or resample
step.

### Benchmark

**Do not carry the macOS README's "20 seconds per hour" over to Windows.** That
number is Apple Silicon's Neural Engine running Parakeet. On CPU it does not
apply — not by a little.

Measured with `quill bench` on 75s of synthesized pt-BR speech (138 words) on an
i7-7500U — a 2-core/4-thread 15W laptop from 2017, i.e. close to a worst case:

| model | load | run | xRT | WER | size |
|---|---|---|---|---|---|
| tiny | 0.6s | 56.8s | 1.32 | 10.1% | 28 MB |
| base | 0.6s | 56.2s | 1.34 | 5.1% | 53 MB |
| **small** | 1.4s | 180.8s | 0.42 | 2.2% | 167 MB |
| medium | 2.6s | 473.5s | 0.16 | 1.4% | 514 MB |
| large-v3-turbo | 2.8s | 661.4s | 0.11 | 1.4% | 547 MB |

`xRT` is seconds of audio per second of compute; higher is faster.

- **`base` strictly dominates `tiny`** — same speed, half the error rate.
- **`small` is the knee of the curve** and the default: 2.2% WER at 0.42 xRT.
- **`medium` and `large-v3-turbo` are not worth their cost here.** Both land at
  1.4%, a marginal gain over small for 3-8x the compute and 3x the download.
- Remember a session is *two* tracks: a 1-hour meeting is 2 hours of audio. Set
  `transcription.model` to `base` on a machine like this; the default targets a
  typical 8-core Windows machine.

Caveats: the audio is TTS, so unusually clean — real meeting audio will score
worse across the whole table, and the *ordering* is the durable result. The table
is also pessimistic for the lower rows, which inherit a hot chip from the models
above them; `small` transcribing the same clip in isolation managed 0.71-0.93 xRT
against the 0.42 measured mid-sequence. Measure your own machine cold.

**The downloader is ours, not Whisper.net's.** `WhisperGgmlDownloader` has no
timeout: a stalled connection hung for fourteen hours without transferring a byte
or reporting anything, which is unacceptable for a daemon that downloads in the
background after a meeting. `WhisperModels` does the HTTP itself with a 60s stall
timeout re-armed on every read, `Range` resume between attempts, and a size check
against `Content-Length` so a truncated transfer can't become a permanently
broken "cached" model.

## What differs from macOS, and why

- **Whisper instead of Parakeet.** Parakeet TDT v2 is English-only and Core ML.
  Whisper is multilingual, which the macOS README already lists as the planned
  fallback engine.
- **`transcription.model` and `transcription.language`** are Windows-only config
  keys (`auto`, `pt`, `en`, …). The macOS engine is English-only and comes in one
  size.
- **WAV mono 16 kHz** instead of AAC-in-CAF. It's what the ASR consumes anyway,
  it streams, and a truncated WAV is recoverable by rebuilding its
  header — which preserves the crash-tolerance property that motivated CAF.
- **`QUILL_CONFIG`** env var overrides the config path, so a second profile
  doesn't disturb the installed one.
- **No permission dance for system audio.** WASAPI loopback needs no consent
  prompt, so the embedded `Info.plist` disappears entirely.
- **Config lives at `%APPDATA%\quill\config.json`**, with
  `~/.config/quill/config.json` still read so one dotfiles repo serves both
  platforms.

## The on-disk contract

Identical to macOS, so `on_stop` hooks and downstream tooling work unchanged on
both. `ContractTests` is what enforces it — in particular the two details that
break it silently:

- Serialized types declare properties **alphabetically**, standing in for
  Swift's `.sortedKeys`. System.Text.Json writes properties in declaration
  order, so reordering them changes the output.
- The JSON encoder is set to `UnsafeRelaxedJsonEscaping`. The default escapes
  every non-ASCII character, which would turn a Portuguese transcript into
  `ã` soup where the Swift build writes plain UTF-8.

Only the track filenames differ (`mic.wav`/`system.wav` rather than `.caf`), and
those are read from `meta.json` rather than hardcoded on either platform.
