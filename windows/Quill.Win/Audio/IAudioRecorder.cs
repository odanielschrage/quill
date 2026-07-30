namespace Quill.Audio;

/// One capture source writing a single track to disk. Both the mic and the
/// system-loopback recorders sit behind this so RecordingSession — and its
/// tests — never depend on WASAPI.
internal interface IAudioRecorder : IDisposable
{
    bool IsRecording { get; }

    /// Wall-clock time of the first captured buffer — the track's true start,
    /// used to offset-align the two tracks' transcript timestamps. Null until
    /// audio actually arrives.
    ///
    /// Implementations MUST keep returning it after Stop(): RecordingSession
    /// stops both tracks first and reads the two skews afterwards, so a value
    /// that resets on stop silently zeroes start_offset_ms and mis-aligns the
    /// merged transcript.
    DateTimeOffset? FirstBufferAt { get; }

    /// Start capturing into `path`. Throws if capture can't be established;
    /// RecordingSession relies on that to avoid half-recording a meeting.
    void Start(string path);

    /// Stop capturing and finalize the file. Must be idempotent.
    void Stop();
}
