using NAudio.Wave;
using ACGTranslator.Services;

namespace ACGTranslator.Tests;

public class VadSegmenterTests
{
    private static float[] Tone(double seconds, float amplitude = 0.2f) =>
        Enumerable.Range(0, (int)(seconds * VadSegmenter.SampleRate)).Select(i => amplitude * MathF.Sin(i * 0.2f)).ToArray();

    private static float[] Silence(double seconds) => new float[(int)(seconds * VadSegmenter.SampleRate)];

    [Fact]
    public void Speech_followed_by_silence_emits_one_segment_with_timing()
    {
        var vad = new VadSegmenter();
        var segments = new List<SpeechSegmentEventArgs>();
        vad.SegmentReady += (_, e) => segments.Add(e);

        vad.Push(Silence(1));
        vad.Push(Tone(1));
        vad.Push(Silence(1));

        var segment = Assert.Single(segments);
        // Pre-roll keeps up to 180 ms of audio before the onset.
        Assert.InRange(segment.Start.TotalSeconds, 0.8, 1.0);
        Assert.InRange(segment.End.TotalSeconds, 2.0, 2.6);
    }

    [Fact]
    public void Short_noise_is_ignored()
    {
        var vad = new VadSegmenter();
        var count = 0;
        vad.SegmentReady += (_, _) => count++;

        vad.Push(Tone(0.1));
        vad.Push(Silence(1));
        vad.Flush();

        Assert.Equal(0, count);
    }

    [Fact]
    public void Long_speech_is_split_at_the_maximum_length()
    {
        var vad = new VadSegmenter(maxSegmentMs: 2000);
        var segments = new List<SpeechSegmentEventArgs>();
        vad.SegmentReady += (_, e) => segments.Add(e);

        vad.Push(Tone(5));
        vad.Flush();

        Assert.True(segments.Count >= 2);
        Assert.All(segments, s => Assert.True(s.Samples.Length <= 2000 * 16));
    }
}

public class AudioPreprocessorTests
{
    private static byte[] FloatStereo(int rate, double seconds, double frequency)
    {
        var frames = (int)(rate * seconds);
        var bytes = new byte[frames * 2 * sizeof(float)];
        for (int i = 0; i < frames; i++)
        {
            var value = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / rate));
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8), value);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 4), value);
        }
        return bytes;
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return Math.Sqrt(sum / samples.Length);
    }

    [Fact]
    public void Resamples_48k_stereo_to_16k_mono()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var output = new AudioPreprocessor().Convert(FloatStereo(48000, 1, 440), format);

        Assert.InRange(output.Length, 15990, 16010);
        // A 440 Hz tone passes the filter: RMS of a 0.5 sine is about 0.354.
        Assert.InRange(Rms(output.AsSpan(1000)), 0.32, 0.38);
    }

    [Fact]
    public void Removes_content_above_the_16k_nyquist()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var output = new AudioPreprocessor().Convert(FloatStereo(48000, 1, 12000), format);

        Assert.True(Rms(output.AsSpan(1000)) < 0.02);
    }

    [Fact]
    public void Chunked_input_matches_single_buffer()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        var buffer = FloatStereo(44100, 0.5, 300);
        var whole = new AudioPreprocessor().Convert(buffer, format);

        var chunked = new AudioPreprocessor();
        var parts = new List<float>();
        for (int offset = 0; offset < buffer.Length; offset += 8 * 441)
            parts.AddRange(chunked.Convert(buffer[offset..Math.Min(buffer.Length, offset + 8 * 441)], format));

        Assert.Equal(whole, parts.ToArray());
    }

    [Theory]
    [InlineData(16, new byte[] { 0x00, 0x40 }, 0.5)]
    [InlineData(24, new byte[] { 0x00, 0x00, 0xC0 }, -0.5)]
    public void Decodes_pcm_bit_depths(int bits, byte[] data, double expected)
    {
        Assert.Equal(expected, AudioPreprocessor.Decode(data, 0, new WaveFormat(48000, bits, 1)), 6);
    }
}
