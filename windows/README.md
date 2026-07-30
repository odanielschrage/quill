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
| 1 | Pipeline: config, session, transcript contract, queue | done |
| 2 | WASAPI capture (mic + loopback) | **done** |
| 3 | Whisper transcription + model benchmark | **done** |
| 4 | Tray icon, notifications, session-end handling | **done** |
| 5 | `doctor`, `install --launch-at-login`, real CLI | **done** |
| 6 | Single-file publish + docs | **done** |
| 7 | VAD, device-change resilience, echo cancellation | pending |

## CLI

```sh
quill                            # run the tray daemon (default)
quill run --out <dir>            # same, with a custom recordings root
quill doctor                     # check devices, permissions, models
quill install --launch-at-login
quill install --uninstall
```

Plus the dev harnesses each phase was verified with:

```sh
quill status                 # resolved config, cached models
quill record 15              # capture both tracks for 15s, then transcribe
quill transcribe <dir>       # re-run the queue on a session folder
quill gaptest                # R1 acceptance check — plays a tone, checks timeline
quill bench <wav> [ref.txt]  # time every model
quill icons <dir>            # write the generated tray icons out to look at
```

Parsed by hand rather than with System.CommandLine, which is still
`3.0.0-preview`. Three commands and three flags don't justify a preview
dependency in a project whose whole shape is "one binary, few dependencies" —
unlike macOS, where ArgumentParser is stable and first-party.

`install` writes one value under `HKCU\...\CurrentVersion\Run`. Same reasoning
that makes the macOS build write a plain LaunchAgent instead of using
`SMAppService.mainApp`: no elevation, no XML, and the same per-user scope. It
warns if you register a path inside `bin\Debug` or `bin\Release`, which would
silently stop working the next time the project is cleaned.

## doctor

The checks barely overlap with the macOS ones.

**What disappears:** "system audio — state unknowable until first use". On macOS
the TCC state can't be queried without side effects, so `doctor` can only
describe the flow. WASAPI loopback needs no consent at all. What it does need is
a **render endpoint to listen to** — with no output device there is no system
track — so that became the check instead.

**What's new:** Windows gates the microphone in three separate places, and any
one of them denies. The one people miss is *"Let desktop apps access your
microphone"*, stored apart from the global toggle under `ConsentStore\microphone\
NonPackaged` — quill is not a packaged app. There is also a per-executable entry,
keyed on the full path with backslashes replaced by `#`. All three are checked.

Sample output:

```
✓ microphone: allowed
✓ input device: Microfone (HyperX Quadcast)
✓ system audio: Fones de ouvido / Alto falantes (Realtek Audio)
✓ recordings folder: C:\Users\danie\Recordings
✓ transcription: ggml-small-q5_0 cached · language auto
```

Warnings never block; hard failures stop the daemon from starting, because there
is no point putting an icon in the tray that cannot record. A missing model is
deliberately only a warning — it must never stop quill from capturing a meeting
that is starting right now.

(Rendering those check marks needs `Console.OutputEncoding = UTF8`; the console
defaults to the OEM code page, which would turn them — and any accented path —
into mojibake.)

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
digital silence. Verified on real hardware via `quill gaptest`: a 12s capture
with audio only in seconds 0–3 and 7–10 produced a 12.36s track with 6.09s
inserted, rather than collapsing to the ~6s that were audible.

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

- **`base` strictly dominates `tiny`** — same speed, half the error rate. There
  is no reason to run tiny.
- **`small` is the knee of the curve** and the default: 2.2% WER at 0.42 xRT.
- **`medium` and `large-v3-turbo` are not worth their cost here.** Both land at
  1.4%, a marginal gain over small for 3–8× the compute and 3× the download.
- Remember a session is *two* tracks: a 1-hour meeting is 2 hours of audio. Even
  at the isolated ~0.8 xRT this laptop managed, that is a couple of hours of
  background compute — fine for an overnight queue, slow if you want the
  transcript right after the call. Set `transcription.model` to `base` on hardware
  like this.

Caveats worth keeping in mind:

- The audio is TTS, so it is unusually clean. Real meeting audio — noise, accents,
  crosstalk — will score worse across the whole table. The *ordering* is the
  durable result, not the absolute WER.
- **The table is pessimistic for anything below the top rows.** It runs every
  model back to back, so the later ones inherit a hot chip. Between two runs
  `medium` moved 416s → 473s and `large-v3-turbo` 375s → 661s. More tellingly,
  `small` transcribing the same 75s clip *in isolation* took 105s and 81s on the
  two tracks of a real session — 0.71–0.93 xRT, against the 0.42 measured mid-
  sequence. Treat the table as a floor and measure your own machine cold.
- Every model except tiny got "loopback" wrong (`Lopbach`, `lopeback`, `lowback`)
  — English jargon in Portuguese audio, the case `WithPrompt()` exists for.

Re-run it yourself with `quill bench <audio.wav> [reference.txt]`.

### Skipping silence

Silero VAD runs before inference and Whisper only sees the speech.
`transcription.vad`, on by default; the model is ~1 MB and downloads on first use.

The motivation is the system track: most of it is silence the capture ledger
*inserted*, and Whisper was being paid to transcribe it. But the bigger payoff
turned out not to be speed.

Fed 30 seconds of digital silence, Whisper hallucinates. The whole-track run in
`quill vadtest` opens with a segment reading `[Música]` at 0:00 — invented
content, which in a real session would be attributed to a speaker and land in the
transcript as something someone said. With VAD that segment doesn't exist.

Measured on a 120s track (30s silence, 75s pt-BR speech, 15s silence, `tiny`):

| | elapsed | first segment |
|---|---|---|
| whole track | 23.7s | 0.0s — `[Música]` |
| speech only | 18.3s | 29.9s — `Bom dia a todos…` |

1.30× faster there, and the win scales with how much silence a track actually
has — a system track from a meeting where you did most of the talking is far
emptier than this test. On a track that's nearly all speech, VAD costs its own
detection time (a second or so) for no saving.

Two things keep it safe:

- **Timestamps are shifted back.** Transcribing only the speech hands Whisper a
  shortened clip numbered from zero, so each region's segments are offset by where
  that region started. Without it every region would report 0:00 and the two
  tracks would stop sharing a clock — the same damage the capture ledger prevents,
  arriving from the other end. `quill vadtest` asserts the first segment lands at
  29.9s against an injected 30s lead.
- **It never returns an empty transcript.** If the detector finds no speech in a
  track that has audio — a wrong threshold, an unusually quiet recording — the
  whole track is transcribed instead. Same for a VAD model that fails to load or
  a track that isn't quill's own mono 16-bit 16 kHz format. Skipping silence is an
  optimization, and an optimization must not be able to lose a meeting.

Detection is tuned to lose nothing rather than to skip the most: a low threshold,
250 ms of padding around each span, and only silences longer than 500 ms split a
region. Spans closer than 2 seconds are then merged back together — sentence
pauses fragmented one stretch of talking into 11 regions in testing, and each
region costs a separate inference call, so stitching them back saves more than the
skipped second was worth.

## Packaging

```sh
dotnet publish Quill.Win/Quill.Win.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

One `quill.exe`, ~70 MB, carrying the .NET runtime, WinForms, NAudio and
whisper.cpp. Nothing to install to run it.

**`IncludeAllContentForSelfExtract=true` is required, and the obvious flag is the
wrong one.** `IncludeNativeLibrariesForSelfExtract` bundles the native libraries
and extracts them, but leaves `AppContext.BaseDirectory` pointing at the directory
containing the `.exe`. Whisper.net probes for `whisper.dll` relative to that, so
it looks in the wrong place and every transcription dies with:

```
System.IO.FileNotFoundException: Native Library not found in default paths.
```

`IncludeAllContentForSelfExtract` extracts everything to a temp directory *and*
points `BaseDirectory` at it, so the probe succeeds. The cost is a slower first
launch while the bundle unpacks; subsequent runs reuse the extraction.

This fails at transcription time, not at startup — `doctor` passes, recording
works, and the failure only lands in `transcribe.log` after a meeting. Worth
running one real transcription from the published binary after any change to the
publish settings.

Idle footprint is 47 MB working set / 13 MB private. It climbs to a few hundred MB
while a model is loaded, and drops back when the queue drains — the coordinator
releases the engine rather than idling on gigabytes of weights.

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
