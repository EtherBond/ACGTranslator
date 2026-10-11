using System.Diagnostics;
namespace OfflineSubtitleTranslator.Services;

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
    private long _lastAudio;
    private bool _running, _closed;
    public event EventHandler<SpeechSegmentEventArgs>? SegmentReady;

    public AudioPipeline() => _timer = new Timer(_ => PadIdle(), null, 100, 100);

    public void Start(int maxSegmentMs = VadSegmenter.DefaultMaxSegmentMs)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _convert = new AudioPreprocessor();
            _vad = new VadSegmenter(maxSegmentMs);
            _vad.SegmentReady += (_, e) => SegmentReady?.Invoke(this, e);
            _lastAudio = 0;
            _clock.Restart();
            _running = true;
        }
    }

    public void Push(AudioChunkEventArgs chunk)
    {
        lock (_sync)
        {
            if (!_running) return;
            var samples = _convert.Convert(chunk.Buffer, chunk.WaveFormat);
            if (_clock.ElapsedMilliseconds - _lastAudio > IdleGapMs) PadTo(Math.Max(0, _clock.Elapsed.TotalSeconds - samples.Length / (double)VadSegmenter.SampleRate));
            _vad.Push(samples);
            _lastAudio = _clock.ElapsedMilliseconds;
        }
    }

    private void PadIdle()
    {
        lock (_sync)
            if (_running && _clock.ElapsedMilliseconds - _lastAudio > IdleGapMs) PadTo(_clock.Elapsed.TotalSeconds);
    }

    private void PadTo(double seconds)
    {
        long missing = (long)(seconds * VadSegmenter.SampleRate) - _vad.ProcessedSamples;
        while (missing > 0)
        {
            int count = (int)Math.Min(3200, missing);
            _vad.Push(new float[count]); missing -= count;
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
            _vad.Flush();
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_sync) _closed = true;
        _timer.Dispose();
    }
}
