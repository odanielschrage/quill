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

    /// Load the model, downloading it first if needed. `progress` carries
    /// human-readable status for the tray — the first run pulls hundreds of
    /// megabytes, and a daemon with no console has nowhere else to say so.
    Task PrepareAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    /// `log` receives per-track diagnostics — what the VAD kept, what it skipped.
    /// These have to reach the session's transcribe.log rather than stderr: the
    /// tray daemon is a WinExe with no console, so anything written there is lost
    /// exactly when someone is trying to work out why a transcript looks wrong.
    Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        string audioPath, Action<string>? log = null, CancellationToken ct = default);
    Task ReleaseAsync();
}
