using Quill.Transcription;
using Xunit;

namespace Quill.Tests;

/// Every case here is taken from a real 26-minute meeting transcript, including
/// the ones the filter must leave alone.
public sealed class TranscriptCleanerTests
{
    private static TranscriptSegment Seg(double startS, double endS, string text) =>
        new(TimeSpan.FromSeconds(startS), TimeSpan.FromSeconds(endS), text);

    private static string[] Texts(IReadOnlyList<TranscriptSegment> segments) =>
        [.. segments.Select(s => s.Text)];

    [Theory]
    [InlineData("[MÚSICA]")]
    [InlineData("[MÚSICA DE FUNDO]")]
    [InlineData("[SOM DE CRIANÇA]")]
    [InlineData("[intérprete]")]
    [InlineData("(speaking in foreign language)")]
    [InlineData("*laughs*")]
    public void NonSpeechMarkersAreDropped(string marker)
    {
        var cleaned = TranscriptCleaner.Clean([
            Seg(0, 2, "vamos começar então"),
            Seg(2, 4, marker),
        ]);

        Assert.Equal(["vamos começar então"], Texts(cleaned));
    }

    /// The label is fabricated; the words after it usually aren't, so the prefix
    /// goes and the speech stays.
    [Fact]
    public void AnInventedSpeakerLabelIsStrippedNotDropped()
    {
        var cleaned = TranscriptCleaner.Clean([Seg(826, 827, "[VITOR] - Valeu, peri.")]);

        Assert.Equal(["Valeu, peri."], Texts(cleaned));
    }

    /// The loop that prompted this: one invented sentence, four times in four
    /// seconds.
    [Fact]
    public void AHallucinationLoopCollapsesToOneSegment()
    {
        var cleaned = TranscriptCleaner.Clean([
            Seg(37, 38, "E eu me chamou de \"Mais\""),
            Seg(38, 39, "E eu me chamou de \"Mais\""),
            Seg(39, 40, "E eu me chamou de \"Mais\""),
            Seg(40, 41, "E eu me chamou de \"Mais\""),
            Seg(48, 57, "e 32 reais, 100 reais, acho."),
        ]);

        Assert.Equal(2, cleaned.Count);
        Assert.Equal(37, cleaned[0].Start.TotalSeconds);
        Assert.Equal(48, cleaned[1].Start.TotalSeconds);
    }

    /// Short fillers genuinely repeat in speech. Deleting a real "não" is the
    /// worse error, so a pair of them survives.
    [Theory]
    [InlineData("não")]
    [InlineData("é...")]
    [InlineData("sim")]
    public void AShortWordSaidTwiceIsLeftAlone(string filler)
    {
        var cleaned = TranscriptCleaner.Clean([Seg(0, 1, filler), Seg(1, 2, filler)]);

        Assert.Equal(2, cleaned.Count);
    }

    /// …but three of them is the model looping, not someone stammering.
    [Fact]
    public void AShortWordThreeTimesIsALoop()
    {
        var cleaned = TranscriptCleaner.Clean([
            Seg(0, 1, "não"), Seg(1, 2, "não"), Seg(2, 3, "não"),
        ]);

        Assert.Single(cleaned);
    }

    /// A long phrase repeating verbatim back-to-back even once is not something
    /// natural speech does.
    [Fact]
    public void ALongPhraseRepeatedOnceIsALoop()
    {
        var cleaned = TranscriptCleaner.Clean([
            Seg(0, 5, "então vamos pensar nisso com calma"),
            Seg(5, 10, "então vamos pensar nisso com calma"),
        ]);

        Assert.Single(cleaned);
    }

    /// Someone circling back to the same point later is not a loop.
    [Fact]
    public void TheSamePhraseFarApartIsKept()
    {
        var cleaned = TranscriptCleaner.Clean([
            Seg(0, 5, "então vamos pensar nisso com calma"),
            Seg(5, 9, "o CPC está muito alto ainda"),
            Seg(9, 14, "então vamos pensar nisso com calma"),
        ]);

        Assert.Equal(3, cleaned.Count);
    }

    [Fact]
    public void TimestampsOfSurvivorsAreUntouched()
    {
        var cleaned = TranscriptCleaner.Clean([
            Seg(10, 12, "[MÚSICA]"),
            Seg(12.5, 18.25, "o pixel não está retornando o evento"),
        ]);

        Assert.Single(cleaned);
        Assert.Equal(12.5, cleaned[0].Start.TotalSeconds);
        Assert.Equal(18.25, cleaned[0].End.TotalSeconds);
    }

    /// A collapsed run does not inherit the rest of its timestamps: the audio
    /// under a hallucinated repeat probably wasn't speech, and stretching the
    /// survivor over it would claim someone spoke for seconds they didn't.
    [Fact]
    public void ACollapsedRunDoesNotStretchOverTheSilenceItReplaced()
    {
        var cleaned = TranscriptCleaner.Clean([
            Seg(37, 38, "uma frase inventada pelo modelo"),
            Seg(38, 39, "uma frase inventada pelo modelo"),
            Seg(39, 44, "uma frase inventada pelo modelo"),
        ]);

        Assert.Single(cleaned);
        Assert.Equal(38, cleaned[0].End.TotalSeconds);
    }

    [Fact]
    public void EverythingRemovedIsLogged()
    {
        var log = new List<string>();
        TranscriptCleaner.Clean(
            [
                Seg(0, 2, "[MÚSICA]"),
                Seg(2, 6, "essa frase se repete aqui"),
                Seg(6, 10, "essa frase se repete aqui"),
            ],
            log.Add);

        Assert.Contains(log, l => l.Contains("[MÚSICA]", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("collapsed 2", StringComparison.Ordinal));
    }

    [Fact]
    public void OrdinarySpeechPassesThroughUnchanged()
    {
        TranscriptSegment[] input =
        [
            Seg(0, 5, "mais ou menos assim do jeito que eu faço aqui"),
            Seg(5, 11, "eu duplico pra caramba o nível de campanha"),
            Seg(11, 18, "aí eu vou ver no CPC todos os que são baratinhos"),
        ];

        Assert.Equal(Texts(input), Texts(TranscriptCleaner.Clean(input)));
    }

    [Fact]
    public void NothingInNothingOut() => Assert.Empty(TranscriptCleaner.Clean([]));
}
