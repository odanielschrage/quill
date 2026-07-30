using Quill.Audio;
using Quill.Transcription;
using Whisper.net.Ggml;
using Xunit;

namespace Quill.Tests;

/// Everything about the Whisper layer that can be checked without half a
/// gigabyte of weights on disk. Model download and accuracy are covered by
/// `quill bench`, whose numbers live in windows/README.md.
public sealed class WhisperModelTests
{
    [Theory]
    [InlineData("tiny", GgmlType.Tiny)]
    [InlineData("base", GgmlType.Base)]
    [InlineData("small", GgmlType.Small)]
    [InlineData("medium", GgmlType.Medium)]
    [InlineData("large-v3-turbo", GgmlType.LargeV3Turbo)]
    [InlineData("large-v3", GgmlType.LargeV3)]
    [InlineData("SMALL", GgmlType.Small)] // config values shouldn't be case-traps
    public void KnownModelNamesResolve(string configured, GgmlType expected) =>
        Assert.Equal(expected, WhisperModels.Resolve(configured));

    [Fact]
    public void UnknownModelWarnsAndFallsBackToSmall()
    {
        var warnings = new StringWriter();
        var previous = Console.Error;
        Console.SetError(warnings);
        try
        {
            // A recording that already happened must not be lost to a typo in
            // the config file.
            Assert.Equal(GgmlType.Small, WhisperModels.Resolve("gigantic"));
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.Contains("unknown transcription model", warnings.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void IdentifierIsTheProvenanceStringWrittenToTranscriptJson() =>
        Assert.Equal("ggml-small-q5_0",
            WhisperModels.Identifier(GgmlType.Small, QuantizationType.Q5_0));

    [Fact]
    public void UnquantizedModelsAreLabelledF16() =>
        Assert.Equal("ggml-medium-f16",
            WhisperModels.Identifier(GgmlType.Medium, QuantizationType.NoQuantization));

    /// Weights are a runtime artifact under LocalAppData, never in the repo or
    /// the binary — the same arrangement as FluidAudio's cache on macOS.
    [Fact]
    public void ModelsLiveUnderLocalAppDataNotTheProject()
    {
        var localAppData =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, WhisperModels.CacheDirectory, StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(WhisperModels.CacheDirectory, "ggml-tiny-q5_0.bin"),
            WhisperModels.PathFor(GgmlType.Tiny, QuantizationType.Q5_0));
    }
}

public sealed class WhisperEngineGuardTests : IDisposable
{
    private readonly string _root = Temp.Dir();

    public void Dispose() => Temp.Nuke(_root);

    /// The header-only WAV that a silent session actually produces: WASAPI
    /// loopback delivered nothing at all, so the track has zero frames.
    [Fact]
    public void HeaderOnlyTrackIsRejected()
    {
        var path = Path.Combine(_root, "system.wav");
        using (var _ = new TrackWriter(path, "system")) { }

        var error = Assert.Throws<InvalidOperationException>(
            () => WhisperEngine.EnsureHasAudio(path));
        Assert.Contains("nothing was captured", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackWithAudioPasses()
    {
        var path = Path.Combine(_root, "mic.wav");
        using (var writer = new TrackWriter(path, "mic"))
        {
            writer.Write(new float[TrackWriter.SampleRate]);
        }

        WhisperEngine.EnsureHasAudio(path);
    }

    /// Provenance travels into transcript.json, so it must not depend on the
    /// model being downloaded.
    [Fact]
    public void EngineReportsProvenanceBeforePrepare()
    {
        var engine = new WhisperEngine(GgmlType.Base, QuantizationType.Q5_0, "pt");

        Assert.Equal("whisper", engine.Name);
        Assert.Equal("ggml-base-q5_0", engine.Model);
    }

    [Fact]
    public async Task TranscribingBeforePrepareIsAProgrammingError()
    {
        var engine = new WhisperEngine(GgmlType.Base, QuantizationType.Q5_0, "pt");
        var path = Path.Combine(_root, "mic.wav");
        using (var writer = new TrackWriter(path, "mic"))
        {
            writer.Write(new float[TrackWriter.SampleRate]);
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.TranscribeAsync(path));
        Assert.Contains("before prepare", error.Message, StringComparison.Ordinal);
    }
}
