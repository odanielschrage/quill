namespace Quill.Transcription;

/// A span of speech on the track's own timeline, expressed in samples.
///
/// This exists to keep one piece of arithmetic honest. Transcribing only the
/// speech means Whisper sees a shortened clip and numbers its segments from zero,
/// so every timestamp has to be shifted back by where the region started. Get
/// that wrong and the two tracks stop sharing a clock — the exact damage the
/// capture-side silence ledger exists to prevent, arriving from the other end.
internal readonly record struct SpeechRegion(long StartSample, long SampleCount)
{
    /// quill's tracks are always mono 16 kHz; the slicing below assumes it.
    public const int SampleRate = 16_000;

    /// How far to shift this region's transcript segments to put them back on the
    /// track's timeline.
    public TimeSpan Offset => TimeSpan.FromSeconds((double)StartSample / SampleRate);

    public TimeSpan Duration => TimeSpan.FromSeconds((double)SampleCount / SampleRate);

    public long EndSample => StartSample + SampleCount;

    /// Convert a detected span to sample indices, clamped to the track.
    ///
    /// Clamping is not defensive decoration: Silero is asked to pad its spans so
    /// word onsets aren't clipped, and that padding routinely runs past the last
    /// sample of the file.
    ///
    /// Returns null for a span that lands entirely outside the track or collapses
    /// to nothing.
    public static SpeechRegion? Clamp(TimeSpan start, TimeSpan end, long totalSamples)
    {
        if (totalSamples <= 0) return null;

        var first = Math.Max(0, (long)(start.TotalSeconds * SampleRate));
        var last = Math.Min(totalSamples, (long)Math.Ceiling(end.TotalSeconds * SampleRate));

        if (first >= totalSamples || last <= first) return null;
        return new SpeechRegion(first, last - first);
    }

    /// Merge regions separated by less than `maxGap`.
    ///
    /// Sentence pauses split a single stretch of talking into many regions, and
    /// each one costs a separate inference call plus its own warm-up. Skipping a
    /// one-second gap saves about a second of audio and can easily cost more than
    /// that in overhead — and splitting mid-thought gives the model less context
    /// to work with. Only gaps big enough to be worth the seam survive.
    public static List<SpeechRegion> Coalesce(
        IReadOnlyList<SpeechRegion> regions, TimeSpan maxGap)
    {
        var merged = new List<SpeechRegion>();
        if (regions.Count == 0) return merged;

        var gapSamples = (long)(maxGap.TotalSeconds * SampleRate);
        var current = regions[0];

        foreach (var next in regions.Skip(1))
        {
            if (next.StartSample - current.EndSample <= gapSamples)
            {
                // Take the union: `next` may end before `current` does if the
                // detector emitted overlapping padded spans.
                var end = Math.Max(current.EndSample, next.EndSample);
                current = new SpeechRegion(current.StartSample, end - current.StartSample);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);
        return merged;
    }
}
