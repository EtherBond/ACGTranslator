using NAudio.CoreAudioApi;
using NAudio.Wave;
using ACGTranslator.Models;
namespace ACGTranslator.Services;

public sealed class AudioChunkEventArgs(byte[] buffer, WaveFormat format, double rms) : EventArgs
{
    public byte[] Buffer { get; } = buffer;
    public WaveFormat WaveFormat { get; } = format;
    public double Rms { get; } = rms;
}

public sealed class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private MMDevice? _device;
    private MMDeviceEnumerator? _enumerator;
    private Func<byte[], int, double> _decode = (_, _) => 0;
    public event EventHandler<AudioChunkEventArgs>? AudioChunk;
    public event EventHandler<StoppedEventArgs>? Stopped;
    public bool IsCapturing => _capture is not null;

    public static IReadOnlyList<AudioDeviceOption> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioDeviceOption> { new() { Id = "", Name = "跟随系统默认输出设备" } };
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device) devices.Add(new() { Id = device.ID, Name = device.FriendlyName });
        }
        return devices;
    }

    public void Start(string? id)
    {
        if (_capture is not null) throw new InvalidOperationException("采集已经启动");
        try
        {
            _enumerator = new();
            // A device saved in settings may have been unplugged; fall back to the default output.
            _device = string.IsNullOrEmpty(id) ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : TryGetDevice(id) ?? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _capture = new(_device);
            _decode = AudioPreprocessor.GetDecoder(_capture.WaveFormat);
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
            _capture.StartRecording();
        }
        catch { Dispose(); throw; }
    }

    public string? DeviceName => _device?.FriendlyName;

    private MMDevice? TryGetDevice(string id)
    {
        try { return _enumerator!.GetDevice(id) is { State: DeviceState.Active } device ? device : null; }
        catch { return null; }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture || e.BytesRecorded == 0) return;
        var bytes = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
        int size = capture.WaveFormat.BitsPerSample / 8;
        double energy = 0;
        for (int offset = 0; offset < bytes.Length; offset += size)
        { var value = _decode(bytes, offset); energy += value * value; }
        AudioChunk?.Invoke(this, new(bytes, capture.WaveFormat, Math.Sqrt(energy / (bytes.Length / size))));
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        Release();
        Stopped?.Invoke(this, e);
    }

    public void Stop() => _capture?.StopRecording();

    private void Release()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnData;
            _capture.RecordingStopped -= OnStopped;
            _capture.Dispose();
            _capture = null;
        }
        _device?.Dispose(); _device = null;
        _enumerator?.Dispose(); _enumerator = null;
    }

    public void Dispose() => Release();
}
