using ACGTranslator.Services;

namespace ACGTranslator.Tests;

public class SpeechEndpointTests
{
    [Fact]
    public void Energy_gate_endpoint_excludes_tail_but_keeps_original_audio()
    {
        var vad = new VadSegmenter();
        var events = new List<SpeechSegmentEventArgs>();
        vad.SegmentReady += (_, e) => events.Add(e);
        vad.Push(Enumerable.Repeat(.2f, 16000).ToArray());
        vad.Push(new float[16000]);
        vad.Flush();
        var segment = Assert.Single(events);
        Assert.Equal(TimeSpan.FromSeconds(1), segment.SpeechEnd);
        Assert.Equal(TimeSpan.FromSeconds(1.52), segment.End);
        Assert.Equal(24320, segment.Samples.Length);
        Assert.Null(segment.SpeechEndTimestamp); // Offline sample position is not wall time.
        Assert.Null(segment.SegmentReadyTimestamp);
    }

    [Fact]
    public void Neural_gate_endpoint_precedes_the_retained_silence()
    {
        using var vad = new StreamingSpeechSegmenter(frame => frame.Any(v => v != 0) ? .9f : 0);
        var events = new List<SpeechSegmentEventArgs>();
        vad.SegmentReady += (_, e) => events.Add(e);
        vad.Push(Enumerable.Repeat(.2f, 16384).ToArray());
        vad.Push(new float[16384]);
        vad.Flush();
        var segment = Assert.Single(events);
        Assert.Equal(TimeSpan.FromSeconds(1.024), segment.SpeechEnd);
        Assert.Equal(TimeSpan.FromSeconds(1.536), segment.End);
        Assert.Equal(24576, segment.Samples.Length);
        Assert.Null(segment.SpeechEndTimestamp);
    }

    [Fact]
    public void Forced_boundary_and_partial_flush_keep_their_own_endpoints()
    {
        using var vad = new StreamingSpeechSegmenter(_ => .9f, 2000);
        var events = new List<SpeechSegmentEventArgs>();
        vad.SegmentReady += (_, e) => events.Add(e);
        var audio = Enumerable.Repeat(.2f, 33001).ToArray();
        vad.Push(audio);
        vad.Flush();
        Assert.Equal(2, events.Count);
        Assert.All(events, segment => Assert.Equal(segment.End, segment.SpeechEnd));
        Assert.Equal(audio, events.SelectMany(segment => segment.Samples).ToArray());
    }
}
