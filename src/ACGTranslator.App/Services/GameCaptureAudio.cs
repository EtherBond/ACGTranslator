using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;

namespace ACGTranslator.Services;

public sealed class GameCaptureAudio : IDisposable
{
    private readonly AudioCaptureService _capture = new();
    private readonly AudioPreprocessor _preprocessor = new();
    private readonly object _gate = new();
    private readonly Stopwatch _clock;
    private readonly AudioSignalMonitor _signal = new();
    private readonly string _folder;
    private double _maxInputRms;
    private WaveFileWriter? _writer;
    private StreamWriter? _timing;
    private long _samples;
    private bool _stopRequested;
    private TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception? Error { get; private set; }
    public AudioSignalState Signal => _signal.Snapshot();
    public string DeviceName => _capture.DeviceName ?? "";
    public double Duration => _samples / 16000d;
    public GameCaptureAudio(string folder, Stopwatch clock)
    {
        _clock = clock; _folder = folder;
        _writer = new(Path.Combine(folder, "capture.wav"), new WaveFormat(16000, 16, 1));
        _timing = new(Path.Combine(folder, "audio-timing.jsonl"));
        _capture.AudioChunk += (_, e) =>
        {
            double arrival = _clock.Elapsed.TotalSeconds;
            lock (_gate)
            {
                if (_writer is null || Error is not null) return;
                try
                {
                    var audio = _preprocessor.Convert(e.Buffer, e.WaveFormat);
                    _signal.Observe(audio, arrival); _maxInputRms = Math.Max(_maxInputRms, e.Rms);
                    long estimate = Math.Max(0, (long)Math.Round(arrival * 16000) - audio.Length);
                    long gap = estimate - _samples;
                    // WASAPI loopback may omit silent periods. Preserve them on the common timeline.
                    // Small callback jitter is not silence; log both sample and arrival clocks.
                    if (gap > 1280) WriteSilence(gap);
                    _timing!.WriteLine(JsonSerializer.Serialize(new { arrival, start_sample = _samples, samples = audio.Length, estimated_start = estimate, input_rms = e.Rms, converted_rms = Signal.RecentRms, format = e.WaveFormat.ToString() }));
                    _writer.WriteSamples(audio, 0, audio.Length); _samples += audio.Length;
                    _writer.Flush();
                }
                catch (Exception ex) { Error = ex; }
            }
        };
        _capture.Stopped += (_, e) => { if (e.Exception is not null) Error = e.Exception; else if (!_stopRequested) Error = new IOException("输出设备录音意外停止"); _stopped.TrySetResult(); };
    }
    public void Start(string device) => _capture.Start(device);
    public async Task StopAsync()
    {
        _stopRequested = true;
        if (_capture.IsCapturing)
        {
            _capture.Stop();
            try { await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException ex) { Error ??= ex; }
        }
        lock (_gate)
        {
            if (_writer is not null)
            {
                try { WriteSilence(Math.Max(0, (long)(_clock.Elapsed.TotalSeconds * 16000) - _samples)); }
                catch (Exception ex) { Error ??= ex; }
                _writer.Dispose(); _writer = null;
                _timing?.Dispose(); _timing = null;
            }
        }
        _capture.Dispose();
        File.WriteAllText(Path.Combine(_folder, "audio-health.json"), JsonSerializer.Serialize(new
        {
            signal = Signal, max_input_rms = _maxInputRms, recording_samples = _samples,
            status = Signal.HasSignal ? "signal_recorded" : "no_recorded_signal",
            note = "Nonzero audio establishes a signal, not proof of voiced dialogue."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void WriteSilence(long count)
    {
        var buffer = new float[16000];
        while (count > 0)
        {
            int n = (int)Math.Min(buffer.Length, count);
            _writer!.WriteSamples(buffer, 0, n); _samples += n; count -= n;
        }
    }
    public void Dispose()
    {
        _capture.Dispose();
        lock (_gate) { _writer?.Dispose(); _writer = null; _timing?.Dispose(); _timing = null; }
    }
}

public static class CaptureAudioExport
{
    public static void Cut(string source, string destination, double start, double end)
    {
        using var reader = new WaveFileReader(source);
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > reader.TotalTime.TotalSeconds + .001 || end - start > 120)
            throw new InvalidDataException("音频区间无效，须在录音范围内且不超过 120 秒");
        long begin = (long)Math.Round(start * reader.WaveFormat.SampleRate) * reader.WaveFormat.BlockAlign;
        long finish = Math.Min(reader.Length, (long)Math.Round(end * reader.WaveFormat.SampleRate) * reader.WaveFormat.BlockAlign);
        if (finish <= begin) throw new InvalidDataException("音频区间为空");
        reader.Position = begin;
        using var writer = new WaveFileWriter(destination, reader.WaveFormat);
        byte[] buffer = new byte[32768];
        long remaining = finish - begin;
        while (remaining > 0)
        {
            int count = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) throw new EndOfStreamException("录音提前结束");
            writer.Write(buffer, 0, count); remaining -= count;
        }
    }
    public static string Export(CaptureSession session)
    {
        var selected = session.Entries.Where(e => e.Review == "已确认").ToList();
        if (selected.Count == 0) throw new InvalidOperationException("没有已确认条目，请先听音并确认原文和边界");
        string folder = CaptureStore.Folder(session.Id);
        string export = Path.Combine(folder, "exports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(export);
        var rows = new List<object>();
        foreach (var entry in selected)
        {
            if (string.IsNullOrWhiteSpace(entry.Japanese)) throw new InvalidDataException("已确认条目缺少日文原文");
            // Use generated names, never imported or user-edited IDs as filenames.
            string filename = $"{rows.Count + 1:D6}.wav";
            Cut(Path.Combine(folder, "capture.wav"), Path.Combine(export, filename), entry.StartSeconds, entry.EndSeconds);
            rows.Add(new { id = $"{session.Id}-{entry.Id}", audio = filename, text = entry.Japanese,
                translation_zh = entry.Chinese, language = "ja", game = session.Game, scene = session.Id,
                speaker = entry.Speaker, source = $"{session.Id}/session.json#{entry.Id}", verified_original = true,
                capture_block = entry.Block, kind = "game-mix", source_start_s = entry.StartSeconds,
                source_end_s = entry.EndSeconds, screenshot = Path.GetFullPath(CaptureStore.SafeFile(session.Id, entry.Screenshot)) });
        }
        string manifest = Path.Combine(export, "verified.jsonl");
        File.WriteAllLines(manifest, rows.Select(r => JsonSerializer.Serialize(r)));
        return manifest;
    }
}
