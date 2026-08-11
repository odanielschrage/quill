using System.Text;

namespace Quill.Transcription;

/// Drops mic segments that are just the far end coming back through the speakers.
///
/// Recording a meeting on speakers rather than headphones means the mic hears the
/// other person too. That audio is already in the system track, so the same words
/// get transcribed twice — once correctly as "them", and once as if *you* had said
/// them. A transcript that has you saying the other person's lines is worse than
/// no echo handling at all.
///
/// This is the approach rca-001 landed on for macOS after Apple's VoiceProcessingIO
/// turned out to deliver digital silence on some routes: quill already has a clean
/// far-end track and both tracks on one clock, so the cheapest reliable place to
/// remove echo is the transcript, not the audio. Nothing here is Windows-specific
/// — the same rule would port to the Swift build unchanged.
///
/// The test is *containment*, not similarity: what fraction of the mic segment's
/// words were already coming out of the speakers at that moment. Echo is usually a
/// degraded, partial pickup of the far end, so a symmetric similarity score reads
/// low exactly when confidence should be high. Containment doesn't have that
/// problem, and it fails safe in the case that matters — when both people talk at
/// once, the mic contributes words the system track doesn't have, containment
/// drops, and the segment survives.
///
/// Measured on a 91-minute three-way call with an external condenser mic and
/// speakers: 425 removals, 419 of them while the far end was audibly playing.
/// Precision was never the problem. Recall was, and the two ways it leaked are
/// handled below.
internal static class EchoSuppressor
{
    /// Segment boundaries differ between the two transcriptions, and echo lags
    /// playback slightly, so overlap is judged generously.
    public static readonly TimeSpan OverlapTolerance = TimeSpan.FromSeconds(1);

    /// Three quarters of what the mic heard was already playing. Below this,
    /// enough of the segment is the speaker's own words to keep it.
    public const double ContainmentThreshold = 0.75;

    /// Short utterances are left alone by the containment rule. "sim", "certo",
    /// "ok" are as likely to be a real reply as an echo, and dropping a genuine
    /// answer is the worse error.
    public const int MinimumTokens = 3;

    /// …except when the mic reproduces the far end *verbatim*. A two-word segment
    /// that appears word for word inside something playing at that exact moment
    /// is echo, and the length floor was protecting it: a real call leaked
    /// "Mentoria, certificação" as the speaker's own words while the far end said
    /// exactly that. One token is still never enough — a bare "sim" is too common
    /// to attribute.
    public const int VerbatimMinimumTokens = 2;

    /// How much of a short segment's airtime the matching far-end speech must
    /// actually cover. The verbatim rule reaches below the length floor, and the
    /// words it reaches for — "ah legal", "e aí" — are exactly the ones two people
    /// say independently. Requiring the far end to have been playing across most
    /// of the segment is evidence rather than another word-count guess: it
    /// separates an echo from the same phrase said in a gap.
    private const double VerbatimCoverage = 0.5;

    /// Fuzzy matching only applies to words long enough for a near-miss to be
    /// meaningful. Below this, "não" and "nós" are one edit apart.
    private const int FuzzyMinimumLength = 4;

    /// Two transcriptions of the same speech disagree, and the mic's copy is the
    /// degraded one. The same call turned "mentoria" into "notoria" — same word,
    /// two edits, and exact matching scored it as the speaker's own. Tokens count
    /// as the same when they are close in length and within a proportional edit
    /// distance; the length guard is what stops "OIBI" being read as "Ovidinho",
    /// which is a genuine mishearing rather than a spelling wobble.
    private const int MaxLengthDifference = 2;

    /// Returns the segments to keep. Anything dropped is reported through `log`
    /// rather than vanishing — the removal is a judgement call, so it stays
    /// auditable in the session's transcribe.log.
    public static List<Transcript.Segment> Apply(
        IReadOnlyList<Transcript.Segment> segments, Action<string>? log = null)
    {
        var farEnd = segments.Where(s => s.Speaker == "them").ToList();
        if (farEnd.Count == 0) return [.. segments];

        var kept = new List<Transcript.Segment>(segments.Count);
        var dropped = 0;

        foreach (var segment in segments)
        {
            if (segment.Speaker != "me")
            {
                kept.Add(segment);
                continue;
            }

            var tokens = Tokenize(segment.Text);
            if (tokens.Length == 0)
            {
                kept.Add(segment);
                continue;
            }

            var overlapping = Overlapping(segment, farEnd);
            if (overlapping.Count == 0)
            {
                kept.Add(segment);
                continue;
            }

            string? reason = null;

            if (tokens.Length >= VerbatimMinimumTokens && AppearsVerbatim(tokens, overlapping))
            {
                reason = "word for word in what was playing";
            }
            else if (tokens.Length >= MinimumTokens)
            {
                var containment = Containment(tokens, overlapping.SelectMany(o => o.Tokens));
                if (containment >= ContainmentThreshold)
                {
                    reason = $"{containment:P0} of it was playing";
                }
            }

            if (reason is null)
            {
                kept.Add(segment);
                continue;
            }

            dropped++;
            log?.Invoke($"echo: dropped mic segment at {segment.StartMs}ms "
                        + $"({reason}) — \"{segment.Text}\"");
        }

        if (dropped > 0)
        {
            log?.Invoke($"echo: removed {dropped} mic segment(s) that echoed the system track");
        }
        return kept;
    }

    /// Far-end segments open while this mic segment was, each kept with its own
    /// token run and how much of the mic segment it actually covers — the
    /// verbatim test needs both, rather than one flattened bag.
    private static List<(string[] Tokens, double Coverage)> Overlapping(
        Transcript.Segment segment, IEnumerable<Transcript.Segment> farEnd)
    {
        var tolerance = (int)OverlapTolerance.TotalMilliseconds;
        var span = Math.Max(1, segment.EndMs - segment.StartMs);
        var runs = new List<(string[], double)>();

        foreach (var other in farEnd)
        {
            if (segment.StartMs - tolerance >= other.EndMs) continue;
            if (other.StartMs - tolerance >= segment.EndMs) continue;

            var overlap = Math.Min(segment.EndMs, other.EndMs)
                          - Math.Max(segment.StartMs, other.StartMs);
            runs.Add((Tokenize(other.Text), Math.Max(0, overlap) / (double)span));
        }
        return runs;
    }

    /// True when the mic's words appear as an unbroken run inside a single far-end
    /// segment that was playing across most of the mic segment. Stronger evidence
    /// than containment — a contiguous quote of what was audibly playing at the
    /// time is not something two people produce simultaneously by chance.
    private static bool AppearsVerbatim(
        string[] tokens, List<(string[] Tokens, double Coverage)> overlapping)
    {
        foreach (var (run, coverage) in overlapping)
        {
            if (coverage < VerbatimCoverage) continue;
            for (var start = 0; start + tokens.Length <= run.Length; start++)
            {
                var match = true;
                for (var i = 0; i < tokens.Length && match; i++)
                {
                    match = run[start + i] == tokens[i];
                }
                if (match) return true;
            }
        }
        return false;
    }

    /// Fraction of `tokens` also present in `available`, counting multiplicity —
    /// so a mic segment repeating a word twice only matches an echo that also had
    /// it twice.
    ///
    /// Exact matches are claimed first so a near-miss can never consume the token
    /// an exact match needed.
    private static double Containment(string[] tokens, IEnumerable<string> available)
    {
        var pool = available.ToList();
        var claimed = new bool[pool.Count];
        var matched = 0;
        var pending = new List<string>();

        foreach (var token in tokens)
        {
            var found = false;
            for (var i = 0; i < pool.Count; i++)
            {
                if (claimed[i] || pool[i] != token) continue;
                claimed[i] = true;
                matched++;
                found = true;
                break;
            }
            if (!found) pending.Add(token);
        }

        foreach (var token in pending)
        {
            for (var i = 0; i < pool.Count; i++)
            {
                if (claimed[i] || !NearlySame(token, pool[i])) continue;
                claimed[i] = true;
                matched++;
                break;
            }
        }

        return (double)matched / tokens.Length;
    }

    /// Whether two tokens are the same word transcribed differently.
    internal static bool NearlySame(string a, string b)
    {
        if (a == b) return true;
        if (a.Length < FuzzyMinimumLength || b.Length < FuzzyMinimumLength) return false;
        if (Math.Abs(a.Length - b.Length) > MaxLengthDifference) return false;

        var budget = Math.Max(1, Math.Max(a.Length, b.Length) / 4);
        return EditDistance(a, b, budget) <= budget;
    }

    /// Levenshtein, abandoned as soon as it cannot come in under `budget`.
    private static int EditDistance(string a, string b, int budget)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
                if (current[j] < best) best = current[j];
            }
            if (best > budget) return budget + 1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// Lowercase, punctuation stripped, split on whitespace. Accents are kept:
    /// they are part of the word in Portuguese, and the two tracks transcribe the
    /// same speech consistently enough for that to help rather than hurt.
    internal static string[] Tokenize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
            else builder.Append(' ');
        }
        return builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
