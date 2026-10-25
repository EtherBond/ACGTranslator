using ACGTranslator.Services;

namespace ACGTranslator.Tests;

public class StreamingSpeechTests
{
    [Fact]
    public void Weak_speech_is_kept_when_probability_is_high()
    {
        using var vad = new StreamingSpeechSegmenter(x => x.Any(v => v != 0) ? .9f : 0);
        var events = new List<SpeechSegmentEventArgs>();
        vad.SegmentReady += (_, e) => events.Add(e);
        vad.Push(new float[16000]);
        vad.Push(Enumerable.Repeat(.001f, 16000).ToArray());
        vad.Push(new float[16000]);
        vad.Flush();
        var result = Assert.Single(events);
        Assert.InRange(result.Start.TotalSeconds, .45, 1.0);
        Assert.InRange(result.End.TotalSeconds, 2.45, 2.6);
        Assert.Equal(48000, vad.ProcessedSamples);
    }

    [Fact]
    public void Loud_non_speech_does_not_open_gate()
    {
        using var vad = new StreamingSpeechSegmenter(_ => .1f);
        var count = 0;
        vad.SegmentReady += (_, _) => count++;
        vad.Push(Enumerable.Repeat(.6f, 48000).ToArray()); vad.Flush();
        Assert.Equal(0, count);
    }

    [Fact]
    public void Arbitrary_capture_chunk_sizes_preserve_audio_and_short_tail()
    {
        var source = Enumerable.Repeat(.2f, 33001).ToArray();
        using var vad = new StreamingSpeechSegmenter(_ => .9f, 2000);
        var events = new List<SpeechSegmentEventArgs>();
        vad.SegmentReady += (_, e) => events.Add(e);
        for (int i = 0; i < source.Length; i += 177) vad.Push(source.AsSpan(i, Math.Min(177, source.Length - i)));
        vad.Flush();
        Assert.Equal(source, events.SelectMany(e => e.Samples).ToArray());
        Assert.Equal(source.Length, vad.ProcessedSamples);
        Assert.Equal(2, events.Count);
        Assert.Equal(events[0].End, events[1].Start);
        Assert.Equal(source.Length / 16000d, events[^1].End.TotalSeconds, 6);
    }
}
