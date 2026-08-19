---
title: "Upstream proposal — transcript-level echo suppression for digimata/quill"
date: 2026-08-12
status: blocked
affects: "contribution to upstream; nothing in this repository"
---

## Status

Written and approved, **not yet posted**. `digimata/quill` has interaction limits
enabled — *"Interactions on this repository have been restricted to collaborators
only"* — so nobody outside the org can comment, open an issue or open a PR. GitHub
interaction limits expire on their own (24 hours to 6 months); the expiry is not
visible without admin rights on the repo. Retry periodically:

```sh
gh issue comment 19 --repo digimata/quill --body-file .issues/upstream-echo-proposal.md
```

(strip the frontmatter and everything above the divider first, or paste the body by
hand — the text below the divider is what goes in the comment.)

## Why this and not the Windows port

Reading the upstream issue list changed the plan. The repo has ~3,800 stars, 246
forks, an MIT licence, and an active backlog:

- **#19 (open)** and **#14 (closed)** both report the echo problem that quill's own
  `rca-001` sketches a fix for. #14 quantifies it at 51% of a 23-minute transcript.
  Still unsolved upstream.
- **#5** independently reached the same conclusions we did building the Windows
  engine — including that Whisper's language auto-detection reads only the opening
  window and can mislabel an entire meeting.
- **#22** is the multi-party speaker-naming problem; **#23** is per-app capture.

The echo suppressor is platform-independent logic that fixes an actively reported
bug, measured at 466 removals / 98% correct on a real 91-minute call. It is small,
reviewable, and it answers a question the maintainer's users are asking now — unlike
a 5,000-line C# sibling, which is a question about what the project *is*. Lead with
the fix; the Windows conversation goes better afterwards, if at all.

---

Same thing here, reproducibly enough that I ended up building the fix `rca-001` already sketches. Sharing the measurements in case they're useful, and offering the code. (#14 is the same problem with numbers attached — 51% of a 23-minute transcript.)

## Why it happens

Recording through speakers means the mic hears the far end too. That audio is already in `system`, so both tracks transcribe the same words — once correctly as `them`, once as if you'd said them. Nothing upstream of the transcript separates them: by the time the mic captures it, it genuinely *is* microphone audio.

## What rca-001 proposed, and the one place I'd diverge

> Mark a mic segment as echo only when it overlaps a system segment in time and has high fuzzy token similarity; preserve segments with substantial unique mic words so local interruptions and double-talk survive.

The overlap requirement and the preserve-unique-words instinct are both right. The change I'd argue for is the metric: **containment rather than similarity**.

Echo is a degraded, partial pickup of the far end, so a symmetric similarity score reads *low* exactly when confidence should be high — the mic's copy drops words and mangles others. Containment (what fraction of the mic segment's tokens were already playing at that moment) doesn't have that failure, and it fails safe where it matters: when both people talk at once the mic contributes words the system track doesn't have, containment drops, and the segment survives.

## Measurements

91-minute three-way call, external condenser mic through speakers — worst case for bleed — in pt-BR. I checked every removal against the audio rather than eyeballing the transcript: a removal counts as correct if the far end was audibly playing at that moment (per-second energy envelope of both tracks), wrong if only the mic was active.

| rule | removals | correct |
|---|---|---|
| containment >= 0.75, minimum 3 tokens | 425 | 99% |
| + the two refinements below | 466 | 98% |

Precision was never the problem. Recall was, and it leaked exactly two ways.

**1. A degraded copy.** The mic's transcription is the worse of the two — that call rendered *mentoria* as *notoria*, and exact token matching scored it as the speaker's own words. Fuzzy token matching closes it, but the guard matters more than the match: it has to refuse *OIBI* / *Ovidinho*, which is a real mishearing rather than a spelling wobble. Three edits in eight letters is roughly where the line falls; wider and you start equating *campanha* with *campinas*.

**2. The length floor.** *"Mentoria, certificação"* survived as the speaker's own words while the far end said exactly that, because two tokens is under the floor protecting *sim* and *ok*. A segment can drop below that floor when it appears **word for word** inside far-end speech that was playing across at least half of its duration.

That coverage requirement is load-bearing rather than decoration: without it the same rule also removed *"Ah, legal."* and *"E aí?"* said in gaps, and precision on the new removals fell to 93%.

One token is never enough, whatever the match.

## Caveats

- Thresholds are calibrated on one meeting, in Portuguese. Token containment is language-agnostic so it should transfer, but that is reasoning rather than measurement — the English example in #14 would make a useful second data point.
- Every removal is written to the session's log with its text and score. The rule is a heuristic, so what it discards stays recoverable instead of vanishing.
- Nothing is marked in `transcript.json`; dropping keeps the schema unchanged.

## Offer

My implementation is C# — I've been building a Windows sibling, which is where these measurements come from. The logic is platform-independent though: no AVFoundation, no Core ML, just the merged segment list after both tracks are on one clock. Porting it to Swift against the existing `TranscriptionCoordinator` is roughly 150 lines plus tests, and I'm glad to send it as a PR if you want it.

Equally glad to leave the analysis here if you'd rather write it yourself — the two rules above are the whole of it, and the measurement method is more useful than my code anyway.
