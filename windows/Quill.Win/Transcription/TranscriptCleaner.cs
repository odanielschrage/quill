using System.Text.RegularExpressions;

namespace Quill.Transcription;

/// Removes the two artefacts Whisper reliably produces on marginal meeting audio.
///
/// Both were found in a real 26-minute call: ten non-speech markers (`[MÚSICA]`,
/// `[SOM DE CRIANÇA]`, `(speaking in foreign language)`) and a hallucination loop
/// that repeated one invented sentence four times in as many seconds. Neither is
/// a quill bug — they are known failure modes of the model on quiet or noisy
/// audio — but both are junk in a meeting transcript and both are unambiguous
/// enough to strip.
///
/// Runs per track, before the two are merged onto one clock. Repetition is only
/// visible as *consecutive* segments, and interleaving the tracks would separate
/// a run with the other speaker's words.
internal static partial class TranscriptCleaner
{
    /// A segment that is nothing but a bracketed marker. Whisper's convention for
    /// "this was not speech": [MÚSICA], (speaking in foreign language), *laughs*.
    [GeneratedRegex(@"^\s*(\[[^\]]*\]|\([^)]*\)|\*[^*]*\*)\s*$")]
    private static partial Regex MarkerOnly();

    /// A marker glued to the front of real text, which is how invented speaker
    /// labels arrive — "[VITOR] - Valeu, peri." The label is fabricated; the
    /// words after it usually are not, so the prefix is stripped rather than the
    /// segment dropped.
    [GeneratedRegex(@"^\s*(\[[^\]]*\]|\([^)]*\))\s*[-–—:]?\s*")]
    private static partial Regex MarkerPrefix();

    /// A run this long, repeating verbatim, is the model looping rather than
    /// someone repeating themselves.
    private const int LoopRunLength = 3;

    /// …or a phrase this long repeating back-to-back even once, which natural
    /// speech does not do. Below it, "não" twice in a row is left alone: short
    /// fillers genuinely do repeat, and deleting a real one is the worse error.
    private const int LoopPhraseWords = 4;

    public static IReadOnlyList<TranscriptSegment> Clean(
        IReadOnlyList<TranscriptSegment> segments, Action<string>? log = null)
    {
        var withoutMarkers = StripMarkers(segments, log);
        return CollapseLoops(withoutMarkers, log);
    }

    private static List<TranscriptSegment> StripMarkers(
        IReadOnlyList<TranscriptSegment> segments, Action<string>? log)
    {
        var kept = new List<TranscriptSegment>(segments.Count);
        var dropped = 0;

        foreach (var segment in segments)
        {
            if (MarkerOnly().IsMatch(segment.Text))
            {
                dropped++;
                log?.Invoke($"cleanup: dropped non-speech marker at {Seconds(segment)} "
                            + $"— {segment.Text.Trim()}");
                continue;
            }

            var stripped = MarkerPrefix().Replace(segment.Text, "").Trim();
            if (stripped.Length == 0)
            {
                dropped++;
                log?.Invoke($"cleanup: dropped empty segment at {Seconds(segment)} "
                            + $"— {segment.Text.Trim()}");
                continue;
            }

            if (stripped != segment.Text.Trim())
            {
                log?.Invoke($"cleanup: stripped label at {Seconds(segment)} "
                            + $"— {segment.Text.Trim()}");
            }
            kept.Add(segment with { Text = stripped });
        }

        if (dropped > 0) log?.Invoke($"cleanup: removed {dropped} non-speech marker(s)");
        return kept;
    }

    /// Collapse a run of identical consecutive segments to its first occurrence.
    ///
    /// The run's later timestamps are not folded into the survivor: the audio
    /// under a hallucinated repeat was most likely not speech at all, and
    /// stretching the first segment to cover it would claim someone spoke for
    /// seconds they didn't.
    private static List<TranscriptSegment> CollapseLoops(
        List<TranscriptSegment> segments, Action<string>? log)
    {
        var kept = new List<TranscriptSegment>(segments.Count);
        var removed = 0;

        for (var i = 0; i < segments.Count;)
        {
            var run = 1;
            while (i + run < segments.Count
                   && Same(segments[i + run].Text, segments[i].Text))
            {
                run++;
            }

            kept.Add(segments[i]);

            if (run > 1 && IsLoop(segments[i].Text, run))
            {
                removed += run - 1;
                log?.Invoke($"cleanup: collapsed {run} identical segments at "
                            + $"{Seconds(segments[i])} — {segments[i].Text.Trim()}");
                i += run;
            }
            else
            {
                // Not a loop: keep the rest of the run as ordinary speech.
                for (var extra = 1; extra < run; extra++) kept.Add(segments[i + extra]);
                i += run;
            }
        }

        if (removed > 0) log?.Invoke($"cleanup: removed {removed} looped segment(s)");
        return kept;
    }

    private static bool IsLoop(string text, int run) =>
        run >= LoopRunLength || WordCount(text) >= LoopPhraseWords;

    private static bool Same(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Seconds(TranscriptSegment segment) =>
        $"{segment.Start.TotalSeconds:F1}s";
}
