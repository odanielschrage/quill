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
internal static class EchoSuppressor
{
    /// Segment boundaries differ between the two transcriptions, and echo lags
    /// playback slightly, so overlap is judged generously.
    public static readonly TimeSpan OverlapTolerance = TimeSpan.FromSeconds(1);

    /// Three quarters of what the mic heard was already playing. Below this,
    /// enough of the segment is the speaker's own words to keep it.
    public const double ContainmentThreshold = 0.75;

    /// Short utterances are left alone. "sim", "certo", "ok" are as likely to be
    /// a real reply as an echo, and dropping a genuine answer is the worse error.
    public const int MinimumTokens = 3;

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
            if (tokens.Length < MinimumTokens)
            {
                kept.Add(segment);
                continue;
            }

            var containment = Containment(tokens, OverlappingText(segment, farEnd));
            if (containment < ContainmentThreshold)
            {
                kept.Add(segment);
                continue;
            }

            dropped++;
            log?.Invoke(
                $"echo: dropped mic segment at {segment.StartMs}ms "
                + $"({containment:P0} of it was playing) — \"{segment.Text}\"");
        }

        if (dropped > 0)
        {
            log?.Invoke($"echo: removed {dropped} mic segment(s) that echoed the system track");
        }
        return kept;
    }

    /// Every far-end word spoken while this mic segment was open.
    private static IEnumerable<string> OverlappingText(
        Transcript.Segment segment, IEnumerable<Transcript.Segment> farEnd)
    {
        var tolerance = (int)OverlapTolerance.TotalMilliseconds;
        foreach (var other in farEnd)
        {
            if (segment.StartMs - tolerance >= other.EndMs) continue;
            if (other.StartMs - tolerance >= segment.EndMs) continue;
            foreach (var token in Tokenize(other.Text)) yield return token;
        }
    }

    /// Fraction of `tokens` also present in `available`, counting multiplicity —
    /// so a mic segment repeating a word twice only matches an echo that also had
    /// it twice.
    private static double Containment(string[] tokens, IEnumerable<string> available)
    {
        var pool = new Dictionary<string, int>();
        foreach (var token in available)
        {
            pool[token] = pool.GetValueOrDefault(token) + 1;
        }

        var matched = 0;
        foreach (var token in tokens)
        {
            if (pool.GetValueOrDefault(token) <= 0) continue;
            pool[token]--;
            matched++;
        }
        return (double)matched / tokens.Length;
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
