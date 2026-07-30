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
| 3 | Whisper transcription + model benchmark | next |
| 4 | Tray icon, notifications, session-end handling | pending |
| 5 | `doctor`, `install --launch-at-login`, real CLI | pending |
| 6 | Single-file publish + docs | pending |

`Program.cs` is a placeholder until phase 5, but it carries the harnesses this
layer was verified with:

```sh
quill record 15   # capture both tracks for 15s, report duration/padding/peak
quill gaptest     # acceptance check — plays a tone, verifies the timeline
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

## What differs from macOS, and why

- **WAV mono 16 kHz** instead of AAC-in-CAF. It's what the ASR will consume
  anyway, it streams, and a truncated WAV is recoverable by rebuilding its
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
