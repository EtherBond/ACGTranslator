using System.Diagnostics;
namespace ACGTranslator.Services;

// Converts captured audio to 16 kHz mono and segments it. Segment times are relative to Start().
public sealed class AudioPipeline : IDisposable
{
    // Loopback supplies no frames while nothing is playing; silence is synthesized after this gap.
    private const long IdleGapMs = 180;
    private readonly object _sync = new();
    private readonly Stopwatch _clock = new();
    private readonly Timer _timer;
    private AudioPreprocessor _convert = new();
    private VadSegmenter _vad = new();
    private StreamingSpeechSegmenter? _speechVad;
    private long _lastAudio, _originTimestamp;
    private bool _running, _closed;
    public event EventHandler<SpeechSegmentEventArgs>? SegmentReady;
    public event EventHandler<ProcessingErrorEventArgs>? Failed;

    public AudioPipeline() => _timer = new Timer(_ => PadIdle(), null, 100, 100);

    public void Start(int maxSegmentMs = VadSegmenter.DefaultMaxSegmentMs, bool speechSegmentation = false)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _speechVad?.Dispose();
            _speechVad = null;
            if (speechSegmentation)
            {
                var gate = new SileroSpeechGate(SileroSpeechGate.ModelPath());
                _speechVad = new StreamingSpeechSegmenter(gate.Probability, maxSegmentMs, gate);
                _speechVad.SegmentReady += ForwardSegment;
            }
            _convert = new AudioPreprocessor();
            _vad = new VadSegmenter(maxSegmentMs);
            _vad.SegmentReady += ForwardSegment;
            _lastAudio = 0;
            _originTimestamp = Stopwatch.GetTimestamp();
            _clock.Restart();
            _running = true;
        }
    }

    private void ForwardSegment(object? sender, SpeechSegmentEventArgs segment)
    {
        var ready = Stopwatch.GetTimestamp();
        long? endpoint = segment.SpeechEnd is { } end
            ? _originTimestamp + (long)(end.TotalSeconds * Stopwatch.Frequency) : null;
        // Capture buffering / clock drift can put a sample endpoint ahead of wall time.
        // Keep it missing instead of reporting an artificial zero latency.
        if (endpoint > ready) endpoint = null;
        SegmentReady?.Invoke(this, new SpeechSegmentEventArgs(segment.Samples, segment.Start, segment.End)
        {
            SpeechEnd = segment.SpeechEnd,
            SpeechEndTimestamp = endpoint,
            SegmentReadyTimestamp = ready
        });
    }

    public void Push(AudioChunkEventArgs chunk)
    {
        lock (_sync)
        {
            if (!_running) return;
            var samples = _convert.Convert(chunk.Buffer, chunk.WaveFormat);
            if (_clock.ElapsedMilliseconds - _lastAudio > IdleGapMs) PadTo(Math.Max(0, _clock.Elapsed.TotalSeconds - samples.Length / (double)VadSegmenter.SampleRate));
            PushSamples(samples);
            _lastAudio = _clock.ElapsedMilliseconds;
        }
    }

    private void PushSamples(float[] samples)
    {
        try
        {
            if (_speechVad is not null) _speechVad.Push(samples);
            else _vad.Push(samples);
        }
        catch (Exception ex)
        {
            _running = false; _clock.Stop();
            Failed?.Invoke(this, new ProcessingErrorEventArgs($"语音分段失败，已停止采集：{ex.Message}"));
        }
    }

    private void PadIdle()
    {
        lock (_sync)
            if (_running && _clock.ElapsedMilliseconds - _lastAudio > IdleGapMs) PadTo(_clock.Elapsed.TotalSeconds);
    }

    private void PadTo(double seconds)
    {
        long missing = (long)(seconds * VadSegmenter.SampleRate) - (_speechVad?.ProcessedSamples ?? _vad.ProcessedSamples);
        while (missing > 0 && _running)
        {
            int count = (int)Math.Min(3200, missing);
            PushSamples(new float[count]); missing -= count;
        }
    }

    // Emits any pending speech and stops accepting audio.
    public void Stop()
    {
        lock (_sync)
        {
            if (!_running) return;
            _running = false;
            _clock.Stop();
            if (_speechVad is not null) _speechVad.Flush();
            else _vad.Flush();
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_sync) { _closed = true; _speechVad?.Dispose(); _speechVad = null; }
        _timer.Dispose();
    }
}
