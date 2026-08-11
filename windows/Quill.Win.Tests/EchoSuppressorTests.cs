using Quill.Transcription;
using Xunit;

namespace Quill.Tests;

/// Echo suppression is a judgement call applied to someone's meeting notes, so
/// the cases that matter most are the ones where it must NOT fire: double-talk,
/// short replies, and the same words said at a different moment.
public sealed class EchoSuppressorTests
{
    private static Transcript.Segment Seg(string speaker, int startMs, int endMs, string text) =>
        new() { Speaker = speaker, StartMs = startMs, EndMs = endMs, Text = text };

    [Fact]
    public void EchoOfTheFarEndIsDropped()
    {
        var kept = EchoSuppressor.Apply([
            Seg("them", 0, 3000, "Bom dia a todos, obrigado por participarem."),
            Seg("me", 100, 3100, "Bom dia a todos obrigado por participarem"),
        ]);

        Assert.Single(kept);
        Assert.Equal("them", kept[0].Speaker);
    }

    /// The case the whole design turns on. Both people talking at once must
    /// survive, even though the mic picked up the far end as well.
    [Fact]
    public void DoubleTalkSurvives()
    {
        var kept = EchoSuppressor.Apply([
            Seg("them", 0, 4000, "então o relatório fica pronto na sexta-feira"),
            Seg("me", 500, 4000, "o relatório desculpa te interromper mas isso muda o cronograma inteiro"),
        ]);

        Assert.Equal(2, kept.Count);
    }

    /// Quoting someone back to them is not echo — it happens after they finish,
    /// so the time overlap check is what tells the two apart.
    [Fact]
    public void TheSameWordsAtADifferentMomentAreKept()
    {
        var kept = EchoSuppressor.Apply([
            Seg("them", 0, 3000, "precisamos adiar o lançamento"),
            Seg("me", 30_000, 33_000, "precisamos adiar o lançamento"),
        ]);

        Assert.Equal(2, kept.Count);
    }

    /// A short reply is as likely to be a real answer as an echo, and dropping a
    /// genuine one is the worse error.
    [Theory]
    [InlineData("sim")]
    [InlineData("certo")]
    [InlineData("ok")]
    public void ShortRepliesAreNeverDropped(string reply)
    {
        var kept = EchoSuppressor.Apply([
            Seg("them", 0, 3000, $"{reply} vamos seguir com o plano"),
            Seg("me", 100, 900, reply),
        ]);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void PartialEchoWithRealWordsIsKept()
    {
        // Half echo, half the speaker actually answering — containment lands
        // below the threshold, so it stays.
        var kept = EchoSuppressor.Apply([
            Seg("them", 0, 4000, "qual é o prazo para a entrega final"),
            Seg("me", 200, 4200, "qual é o prazo acho que conseguimos até quinta se ninguém adoecer"),
        ]);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void MicOnlySessionIsUntouched()
    {
        // Nothing played, so there is no far end to have echoed.
        var segments = new[]
        {
            Seg("me", 0, 3000, "gravando uma nota rápida para mim mesmo"),
            Seg("me", 3000, 6000, "lembrar de revisar o orçamento amanhã"),
        };

        Assert.Equal(2, EchoSuppressor.Apply(segments).Count);
    }

    [Fact]
    public void FarEndSegmentsAreNeverDropped()
    {
        // Two system segments repeating themselves must both survive: the rule
        // only ever removes mic segments.
        var kept = EchoSuppressor.Apply([
            Seg("them", 0, 3000, "vamos revisar o andamento do projeto"),
            Seg("them", 3000, 6000, "vamos revisar o andamento do projeto"),
        ]);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void EveryRemovalIsLogged()
    {
        var log = new List<string>();
        EchoSuppressor.Apply(
            [
                Seg("them", 0, 3000, "obrigado por participarem desta reunião"),
                Seg("me", 100, 3100, "obrigado por participarem desta reunião"),
            ],
            log.Add);

        // The dropped text has to be recoverable from transcribe.log — the
        // removal is a heuristic, not a certainty.
        Assert.Contains(log, line => line.Contains("obrigado por participarem", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("removed 1 mic segment", StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatedWordsMatchByCountNotPresence()
    {
        // "não não não" against a far end that said "não" once: only one token
        // matches, so containment is a third and the segment stays.
        var kept = EchoSuppressor.Apply([
            Seg("them", 0, 3000, "não"),
            Seg("me", 100, 3100, "não não não"),
        ]);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void PunctuationAndCaseDoNotDefeatTheMatch()
    {
        var kept = EchoSuppressor.Apply([
            Seg("them", 0, 3000, "Então, vamos fechar isso hoje?"),
            Seg("me", 100, 3100, "então vamos fechar isso hoje"),
        ]);

        Assert.Single(kept);
    }

    [Fact]
    public void OrderIsPreserved()
    {
        var kept = EchoSuppressor.Apply([
            Seg("me", 0, 1000, "primeira fala minha e bem distinta"),
            Seg("them", 2000, 3000, "resposta do outro lado"),
            Seg("me", 4000, 5000, "terceira fala minha também distinta"),
        ]);

        Assert.Equal(3, kept.Count);
        Assert.Equal([0, 2000, 4000], kept.Select(s => s.StartMs));
    }

    // The cases below are lifted from a 91-minute three-way call recorded with an
    // external condenser mic and speakers — the setup this feature exists for.

    /// Escaped in that call. The mic's copy of the far end came back degraded:
    /// "mentoria" as "notoria", so exact token matching scored it as the
    /// speaker's own words.
    [Fact]
    public void ADegradedCopyOfTheFarEndIsCaught()
    {
        var kept = EchoSuppressor.Apply([
            Seg("them", 671_000, 676_000,
                "Mas é só uma coisa. A questão da mentoria, da certificação, ela é separada."),
            Seg("me", 672_300, 677_000,
                "Mas é só uma coisa. A questão da notoria, da certificação, ela é separada."),
        ]);

        Assert.Single(kept);
        Assert.Equal("them", kept[0].Speaker);
    }

    /// Also escaped, for the opposite reason: an exact duplicate of two words,
    /// held back by the length floor that protects "sim" and "ok".
    [Fact]
    public void AShortVerbatimQuoteOfTheFarEndIsCaught()
    {
        var kept = EchoSuppressor.Apply([
            Seg("them", 761_000, 763_000, "Mentoria, certificação."),
            Seg("me", 761_900, 763_500, "Mentoria, certificação"),
        ]);

        Assert.Single(kept);
        Assert.Equal("them", kept[0].Speaker);
    }

    /// A single word is still never enough. The far end saying "sim" while you
    /// also say "sim" is a conversation, not an echo.
    [Fact]
    public void OneWordIsNeverEnoughEvenVerbatim()
    {
        var kept = EchoSuppressor.Apply([
            Seg("them", 644_000, 645_000, "Sim."),
            Seg("me", 644_300, 645_200, "Sim"),
        ]);

        Assert.Equal(2, kept.Count);
    }

    /// From the same call, and the reason precision matters more than recall:
    /// this is the speaker explaining something while the far end murmurs
    /// agreement. It must survive.
    [Fact]
    public void RealSpeechOverAnAcknowledgementSurvives()
    {
        var kept = EchoSuppressor.Apply([
            Seg("them", 644_000, 645_000, "Sim."),
            Seg("me", 644_300, 647_000,
                "Porque nem sempre as pessoas respondem certinho, né?"),
        ]);

        Assert.Equal(2, kept.Count);
    }

    /// The guard that keeps fuzzy matching honest, and where its line actually
    /// falls. Fuzzy matching is for spelling wobbles of one or two edits, not for
    /// rescuing a mishearing: "Ovidinho" heard as "OIBI" is four edits, and
    /// "mentoria" heard as "notoria" is three. Both stay unmatched here on
    /// purpose — widening the budget far enough to catch them would also start
    /// equating "campanha" with "campinas".
    ///
    /// The degraded-copy case is still caught, just by the other rule: one wrong
    /// word out of fourteen leaves containment well above the threshold. See
    /// ADegradedCopyOfTheFarEndIsCaught.
    [Fact]
    public void AGenuineMishearingIsNotTreatedAsTheSameWord()
    {
        Assert.False(EchoSuppressor.NearlySame("oibi", "ovidinho"));
        Assert.False(EchoSuppressor.NearlySame("notoria", "mentoria"));
        Assert.False(EchoSuppressor.NearlySame("campanha", "campinas"));
    }

    [Theory]
    [InlineData("não", "nós")]      // too short to risk
    [InlineData("mais", "mas")]     // one is below the length floor
    [InlineData("falar", "falou")]  // two edits in a five-letter word
    public void ShortOrDistinctWordsNeverMatchFuzzily(string a, string b) =>
        Assert.False(EchoSuppressor.NearlySame(a, b));

    [Theory]
    [InlineData("certo", "certa")]
    [InlineData("mentoria", "mentoría")]
    [InlineData("agendamento", "agendamentos")]
    public void SpellingWobblesOfTheSameWordMatch(string a, string b) =>
        Assert.True(EchoSuppressor.NearlySame(a, b));

    /// Fuzzy matching must not manufacture agreement out of unrelated speech.
    [Fact]
    public void FuzzyMatchingDoesNotCollapseDifferentSentences()
    {
        var kept = EchoSuppressor.Apply([
            Seg("them", 0, 5000, "então vamos revisar o andamento das campanhas hoje"),
            Seg("me", 500, 5500, "prefiro começar pelo relatório financeiro do trimestre"),
        ]);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void TokenizerKeepsAccentsAndDropsPunctuation() =>
        Assert.Equal(
            ["não", "sei", "se", "você", "já", "viu", "é", "ótima"],
            EchoSuppressor.Tokenize("Não sei se você já viu — é ótima!"));
}
