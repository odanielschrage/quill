namespace Quill.Transcription;

/// One timed span of recognized speech from a single track, relative to that
/// track's own start.
internal readonly record struct TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// A speech-to-text engine quill can run locally. Engines are prepared lazily
/// (model download + load) when the transcription queue has work and released
/// when it drains, so quill never idles holding gigabytes of model weights.
internal interface ITranscriptionEngine
{
    /// Short engine identifier recorded as transcript.json provenance.
    string Name { get; }

    /// Concrete model identifier recorded as transcript.json provenance.
    string Model { get; }

    Task PrepareAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string audioPath, CancellationToken ct = default);
    Task ReleaseAsync();
}
