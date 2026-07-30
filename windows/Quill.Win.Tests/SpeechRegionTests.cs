using Quill.Transcription;
using Xunit;

namespace Quill.Tests;

/// The sample arithmetic behind skipping silence. Getting the offset wrong puts a
/// region's words at the wrong moment, which breaks the shared clock the two
/// tracks are merged on — the same damage the capture-side ledger prevents,
/// arriving from the transcription end. `quill vadtest` proves it end to end;
/// these pin the arithmetic.
public sealed class SpeechRegionTests
{
    private const int Rate = SpeechRegion.SampleRate;

    [Fact]
    public void OffsetIsWhereTheRegionStarts()
    {
        var region = new SpeechRegion(StartSample: 30 * Rate, SampleCount: 5 * Rate);

        Assert.Equal(30.0, region.Offset.TotalSeconds, precision: 6);
        Assert.Equal(5.0, region.Duration.TotalSeconds, precision: 6);
        Assert.Equal(35 * Rate, region.EndSample);
    }

    [Fact]
    public void SpanConvertsToSamples()
    {
        var region = SpeechRegion.Clamp(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), totalSamples: 10 * Rate);

        Assert.NotNull(region);
        Assert.Equal(2 * Rate, region!.Value.StartSample);
        Assert.Equal(2 * Rate, region.Value.SampleCount);
    }

    /// Silero is asked to pad its spans so word onsets aren't clipped, and that
    /// padding routinely runs past the last sample of the file.
    [Fact]
    public void PaddingPastTheEndIsClampedNotTruncatedToNothing()
    {
        var region = SpeechRegion.Clamp(
            TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(12), totalSamples: 10 * Rate);

        Assert.NotNull(region);
        Assert.Equal(9 * Rate, region!.Value.StartSample);
        Assert.Equal(10 * Rate, region.Value.EndSample);
    }

    [Theory]
    [InlineData(20, 25, 10)] // starts past the end of the track
    [InlineData(5, 5, 10)]   // zero length
    [InlineData(5, 4, 10)]   // inverted
    public void DegenerateSpansAreDropped(int start, int end, int totalSeconds) =>
        Assert.Null(SpeechRegion.Clamp(
            TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), totalSeconds * Rate));

    [Fact]
    public void EmptyTrackYieldsNoRegion() =>
        Assert.Null(SpeechRegion.Clamp(TimeSpan.Zero, TimeSpan.FromSeconds(1), totalSamples: 0));

    [Fact]
    public void NearbyRegionsMerge()
    {
        // 1s gap — cheaper to transcribe through than to seam.
        var regions = SpeechRegion.Coalesce(
            [new SpeechRegion(0, 5 * Rate), new SpeechRegion(6 * Rate, 3 * Rate)],
            TimeSpan.FromSeconds(2));

        Assert.Single(regions);
        Assert.Equal(0, regions[0].StartSample);
        Assert.Equal(9 * Rate, regions[0].EndSample);
    }

    [Fact]
    public void DistantRegionsStaySeparate()
    {
        // 30s gap — the whole point of the feature.
        var regions = SpeechRegion.Coalesce(
            [new SpeechRegion(0, 5 * Rate), new SpeechRegion(35 * Rate, 5 * Rate)],
            TimeSpan.FromSeconds(2));

        Assert.Equal(2, regions.Count);
        Assert.Equal(35 * Rate, regions[1].StartSample);
    }

    /// Padded spans from the detector can overlap; the merge must take the union
    /// rather than assume the later region ends later.
    [Fact]
    public void OverlappingRegionsMergeToTheirUnion()
    {
        var regions = SpeechRegion.Coalesce(
            [new SpeechRegion(0, 10 * Rate), new SpeechRegion(2 * Rate, 3 * Rate)],
            TimeSpan.FromSeconds(2));

        Assert.Single(regions);
        Assert.Equal(10 * Rate, regions[0].EndSample);
    }

    [Fact]
    public void CoalescingNothingYieldsNothing() =>
        Assert.Empty(SpeechRegion.Coalesce([], TimeSpan.FromSeconds(2)));

    /// A run of pauses shorter than the threshold collapses to one region, which
    /// is what continuous speech with sentence breaks looks like.
    [Fact]
    public void ARunOfSentencePausesCollapsesToOneRegion()
    {
        var regions = SpeechRegion.Coalesce(
            [
                new SpeechRegion(0, 3 * Rate),
                new SpeechRegion(4 * Rate, 3 * Rate),
                new SpeechRegion(8 * Rate, 3 * Rate),
                new SpeechRegion(12 * Rate, 3 * Rate),
            ],
            TimeSpan.FromSeconds(2));

        Assert.Single(regions);
        Assert.Equal(15 * Rate, regions[0].EndSample);
    }
}
