using NAudio.Wave;

namespace Quill.Audio;

/// Averages any channel count down to mono.
///
/// NAudio ships StereoToMonoSampleProvider, which handles exactly two channels.
/// That isn't enough here: a WASAPI mix format follows the device, so a surround
/// endpoint hands over six or eight channels — and rca-001 on the macOS side hit
/// a nine-channel input device. Speech models want one channel anyway.
internal sealed class DownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _interleaved = [];

    public WaveFormat WaveFormat { get; }

    public DownmixSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var needed = count * _channels;
        if (_interleaved.Length < needed) _interleaved = new float[needed];

        var read = _source.Read(_interleaved, 0, needed);

        // Providers hand back whole frames; a partial one would be a source bug,
        // and dropping its remainder is better than emitting a skewed frame.
        var frames = read / _channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            var start = frame * _channels;
            for (var channel = 0; channel < _channels; channel++)
            {
                sum += _interleaved[start + channel];
            }
            buffer[offset + frame] = sum / _channels;
        }
        return frames;
    }
}
