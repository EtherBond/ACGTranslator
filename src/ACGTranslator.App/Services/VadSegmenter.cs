namespace ACGTranslator.Services;

public sealed class SpeechSegmentEventArgs(float[] samples, TimeSpan start, TimeSpan end) : EventArgs
{
    public float[] Samples { get; } = samples;
    public TimeSpan Start { get; } = start;
    public TimeSpan End { get; } = end;
    // Gate-estimated endpoint, before trailing silence. Not a manual speech annotation.
    public TimeSpan? SpeechEnd { get; init; }
    // Live capture maps its sample timeline to the monotonic clock. Offline clips leave these unset.
    public long? SpeechEndTimestamp { get; init; }
    public long? SegmentReadyTimestamp { get; init; }
}

// Energy gate only; the ASR worker re-checks each segment with Silero VAD.
public sealed class VadSegmenter
{
    public const int SampleRate = 16000;
    public const int DefaultMaxSegmentMs = 6000;
    private const int Frame = 320, PreRoll = 2880, EndSilence = 8320, MinVoiced = 3520;
    private readonly int _maximum;
    private readonly float[] _frame = new float[Frame];
    private int _fill, _silence, _voiced;
    private long _position, _start, _speechEnd;
    private readonly Queue<float> _pre = new();
    private readonly List<float> _current = [];
    public long ProcessedSamples => _position + _fill;
    public event EventHandler<SpeechSegmentEventArgs>? SegmentReady;
    public VadSegmenter(int maxSegmentMs = DefaultMaxSegmentMs) => _maximum = Math.Clamp(maxSegmentMs, 2000, 12000) * 16;
    public void Push(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _frame[_fill++] = sample;
            if (_fill == Frame) { ProcessFrame(_frame); _fill = 0; }
        }
    }
    private void ProcessFrame(ReadOnlySpan<float> frame)
    {
        double energy = 0;
        foreach (var sample in frame) energy += sample * sample;
        bool voice = Math.Sqrt(energy / frame.Length) >= (_current.Count == 0 ? .012 : .006);
        if (_current.Count == 0 && voice)
        {
            _start = Math.Max(0, _position - _pre.Count);
            _current.AddRange(_pre); _pre.Clear();
        }
        if (_current.Count != 0 || voice)
        {
            _current.AddRange(frame.ToArray());
            if (voice) { _voiced += frame.Length; _silence = 0; _speechEnd = _position + frame.Length; }
            else _silence += frame.Length;
            _position += frame.Length;
            if (_silence >= EndSilence || _current.Count >= _maximum) Finish();
        }
        else
        {
            foreach (var sample in frame) { _pre.Enqueue(sample); if (_pre.Count > PreRoll) _pre.Dequeue(); }
            _position += frame.Length;
        }
    }
    public void Flush()
    {
        if (_fill > 0) { ProcessFrame(_frame.AsSpan(0, _fill)); _fill = 0; }
        Finish(); _pre.Clear();
    }
    private void Finish()
    {
        SpeechSegmentEventArgs? ready = _voiced >= MinVoiced && _current.Count > 0
            ? new(_current.ToArray(), TimeSpan.FromSeconds(_start / 16000d), TimeSpan.FromSeconds((_start + _current.Count) / 16000d))
                { SpeechEnd = TimeSpan.FromSeconds(_speechEnd / 16000d) } : null;
        _current.Clear(); _silence = 0; _voiced = 0;
        if (ready is not null) SegmentReady?.Invoke(this, ready);
    }
}
