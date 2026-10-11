using NAudio.Wave;
namespace OfflineSubtitleTranslator.Services;

// Stateful FIR anti-alias filter and fractional-rate conversion. One instance per capture.
public sealed class AudioPreprocessor
{
    private const int Taps = 63;
    private int _rate, _cursor;
    private long _phase;
    private double _previous;
    private double[] _filter = [], _history = [];
    private WaveFormat? _format;
    private Func<byte[], int, double> _decode = (_, _) => 0;

    public float[] Convert(byte[] buffer, WaveFormat format)
    {
        if (!ReferenceEquals(_format, format))
        {
            _decode = GetDecoder(format);
            _format = format;
        }
        if (_rate != format.SampleRate) Initialize(format.SampleRate);
        int channels = format.Channels, size = format.BitsPerSample / 8, frame = channels * size;
        if (channels <= 0 || buffer.Length % frame != 0) throw new InvalidDataException("不完整的音频帧");
        var output = new List<float>(buffer.Length / frame * VadSegmenter.SampleRate / _rate + 2);
        for (int offset = 0; offset < buffer.Length; offset += frame)
        {
            double value = 0;
            for (int c = 0; c < channels; c++) value += _decode(buffer, offset + c * size);
            value /= channels;
            if (_rate == VadSegmenter.SampleRate) { output.Add((float)value); continue; }
            _history[_cursor] = value;
            double filtered = 0;
            for (int t = 0, i = _cursor; t < Taps; t++, i = i == 0 ? Taps - 1 : i - 1)
                filtered += _filter[t] * _history[i];
            _cursor = (_cursor + 1) % Taps;
            _phase += VadSegmenter.SampleRate;
            while (_phase >= _rate)
            {
                _phase -= _rate;
                double fraction = 1 - _phase / (double)VadSegmenter.SampleRate;
                output.Add((float)(_previous + (filtered - _previous) * fraction));
            }
            _previous = filtered;
        }
        return output.ToArray();
    }

    private void Initialize(int rate)
    {
        if (rate < 8000 || rate > 192000) throw new NotSupportedException("不支持的音频采样率");
        _rate = rate; _phase = 0; _cursor = 0; _previous = 0;
        _filter = new double[Taps]; _history = new double[Taps];
        double cutoff = Math.Min(0.45, 7200d / rate), sum = 0;
        for (int n = 0; n < Taps; n++)
        {
            int x = n - Taps / 2;
            double sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            _filter[n] = sinc * (0.54 - 0.46 * Math.Cos(2 * Math.PI * n / (Taps - 1)));
            sum += _filter[n];
        }
        for (int n = 0; n < Taps; n++) _filter[n] /= sum;
    }

    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    // Resolved once per format so the per-sample path does no format checks.
    public static Func<byte[], int, double> GetDecoder(WaveFormat format)
    {
        bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat;
        if (format is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == FloatSubFormat) floating = true;
            else if (ext.SubFormat != PcmSubFormat) throw new NotSupportedException("不支持的扩展音频格式");
        }
        else if (!floating && format.Encoding != WaveFormatEncoding.Pcm) throw new NotSupportedException("仅支持 PCM 和浮点音频");
        Func<byte[], int, double> raw = floating ? format.BitsPerSample switch
        {
            32 => (data, offset) => BitConverter.ToSingle(data, offset),
            64 => (data, offset) => BitConverter.ToDouble(data, offset),
            _ => throw new NotSupportedException("不支持的浮点音频位深")
        } : format.BitsPerSample switch
        {
            8 => (data, offset) => (data[offset] - 128) / 128d,
            16 => (data, offset) => BitConverter.ToInt16(data, offset) / 32768d,
            24 => (data, offset) => ((data[offset] << 8 | data[offset + 1] << 16 | data[offset + 2] << 24) >> 8) / 8388608d,
            32 => (data, offset) => BitConverter.ToInt32(data, offset) / 2147483648d,
            _ => throw new NotSupportedException("不支持的 PCM 音频位深")
        };
        return (data, offset) => raw(data, offset) is var value && double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
    }

    public static double Decode(byte[] data, int offset, WaveFormat format) => GetDecoder(format)(data, offset);
}
