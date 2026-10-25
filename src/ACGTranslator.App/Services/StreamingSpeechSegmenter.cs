namespace ACGTranslator.Services;

/// <summary>Stateful 32 ms speech detection; emitted audio retains its original timeline.</summary>
public sealed class StreamingSpeechSegmenter : IDisposable
{
    private const int Frame = 512, PreRoll = 8192, EndSilence = 8192, MinSpeech = 3072;
    private readonly Func<float[], float> _probability;
    private readonly IDisposable? _owner;
    private readonly int _maximum;
    private readonly float[] _frame = new float[Frame];
    private readonly Queue<float> _pre = new();
    private readonly List<float> _current = [];
    private int _fill, _silence, _voiced;
    private long _position, _start, _speechEnd;
    private bool _speech, _continuation;
    public long ProcessedSamples => _position + _fill;
    public event EventHandler<SpeechSegmentEventArgs>? SegmentReady;

    public StreamingSpeechSegmenter(Func<float[], float> probability, int maxSegmentMs = 6000, IDisposable? owner = null)
    {
        _probability = probability;
        _maximum = Math.Clamp(maxSegmentMs, 2000, 12000) * 16;
        _owner = owner;
    }

    public void Push(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _frame[_fill++] = sample;
            if (_fill == Frame) { Process(Frame); _fill = 0; }
        }
    }

    private void Process(int count)
    {
        if (count < Frame) Array.Clear(_frame, count, Frame - count);
        var p = _probability(_frame);
        if (!float.IsFinite(p)) throw new InvalidOperationException("语音检测返回无效概率。");
        _speech = p >= (_speech ? .20f : .35f);
        if (_current.Count == 0 && _speech)
        {
            _start = _position - _pre.Count;
            _current.AddRange(_pre); _pre.Clear();
        }
        if (_current.Count > 0 || _speech)
        {
            for (var i = 0; i < count; i++) _current.Add(_frame[i]);
            if (_speech) { _voiced += count; _silence = 0; _speechEnd = _position + count; }
            else _silence += count;
            _position += count;
            if (_silence >= EndSilence) Finish(false);
            else if (_current.Count >= _maximum) Finish(true);
        }
        else
        {
            for (var i = 0; i < count; i++) { _pre.Enqueue(_frame[i]); if (_pre.Count > PreRoll) _pre.Dequeue(); }
            _position += count;
        }
    }

    private void Finish(bool limit)
    {
        // Do not discard a short tail after a forced maximum-length boundary.
        var emit = _voiced >= MinSpeech || (_continuation && _voiced > 0);
        var ready = emit ? new SpeechSegmentEventArgs(_current.ToArray(),
            TimeSpan.FromSeconds(_start / 16000d), TimeSpan.FromSeconds(_position / 16000d))
            { SpeechEnd = TimeSpan.FromSeconds(_speechEnd / 16000d) } : null;
        _current.Clear(); _silence = _voiced = 0;
        _continuation = limit && emit;
        if (!limit) _speech = false;
        if (ready is not null) SegmentReady?.Invoke(this, ready);
    }

    public void Flush()
    {
        if (_fill > 0) { Process(_fill); _fill = 0; }
        Finish(false); _pre.Clear();
    }

    public void Dispose() => _owner?.Dispose();
}
