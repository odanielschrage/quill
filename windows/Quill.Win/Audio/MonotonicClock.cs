using System.Diagnostics;

namespace Quill.Audio;

/// Elapsed time since capture began.
///
/// Deliberately not wall-clock: the gap ledger converts elapsed time into a
/// sample position, so an NTP correction or a DST transition mid-meeting would
/// otherwise inject or swallow minutes of silence. Also the seam that lets the
/// gap-filling tests run deterministically instead of in real time.
internal interface IMonotonicClock
{
    void Restart();
    TimeSpan Elapsed { get; }
}

internal sealed class StopwatchClock : IMonotonicClock
{
    private readonly Stopwatch _stopwatch = new();

    public void Restart() => _stopwatch.Restart();
    public TimeSpan Elapsed => _stopwatch.Elapsed;
}
