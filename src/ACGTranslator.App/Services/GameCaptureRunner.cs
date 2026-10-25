using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace ACGTranslator.Services;

public sealed class GameCaptureRunner
{
    private readonly Stopwatch _clock = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly GameOcrClient _ocr = new();
    private readonly StableCaptionTracker _tracker = new();
    private GameCaptureAudio? _audio;
    private StreamWriter? _observations;
    private StreamWriter? _frames;
    private double _lastEvidence;
    private Task? _loop;
    private bool _paused, _stopping;
    private int _block;
    private double _lastFrame;
    private string _lastText = "", _screenshot = "";
    private string _fingerprint = "";
    private GameOcrResult? _cachedText;
    public CaptureSession Session { get; }
    public event Action<string>? Status;
    public event Action? Updated;
    public event Action<string>? Faulted;
    public AudioSignalState? AudioSignal => _audio?.Signal;
    public GameCaptureRunner(string game, CaptureProfile profile)
    {
        Session = new CaptureSession { Game = game, Profile = profile };
        _tracker.Completed += entry => { Session.Entries.Add(entry); CaptureStore.Save(Session); Updated?.Invoke(); };
    }
    public async Task StartAsync(nint window, string device)
    {
        if (!Session.Profile.Valid) throw new InvalidOperationException("请先框选日文和中文区域");
        try
        {
            await _ocr.StartAsync(_cancel.Token);
            CaptureStore.Save(Session);
            string folder = CaptureStore.Folder(Session.Id);
            Directory.CreateDirectory(Path.Combine(folder, "frames"));
            Directory.CreateDirectory(Path.Combine(folder, "working"));
            _observations = new StreamWriter(Path.Combine(folder, "observations.jsonl")) { AutoFlush = true };
            _frames = new StreamWriter(Path.Combine(folder, "frame-index.jsonl")) { AutoFlush = true };
            _audio = new GameCaptureAudio(folder, _clock);
            _clock.Start(); _audio.Start(device); Session.AudioDevice = _audio.DeviceName;
            CaptureStore.Save(Session);
            _loop = LoopAsync(window);
        }
        catch
        {
            _clock.Stop(); _ocr.Dispose(); _audio?.Dispose(); _observations?.Dispose(); _frames?.Dispose();
            Session.State = "启动失败"; CaptureStore.Save(Session); throw;
        }
    }
    private async Task LoopAsync(nint window)
    {
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                if (_audio!.Error is not null) throw new IOException("录音已中断", _audio.Error);
                if (_audio.Signal.StartupSilent(_clock.Elapsed.TotalSeconds))
                    throw new IOException("连续 30 秒没有录到任何音频信号，已停止以避免空录。请检查所选输出设备与游戏实际播放设备是否一致，并使用“检查录音（5 秒）”重试。");
                if (_clock.Elapsed > TimeSpan.FromHours(2)) throw new IOException("已达到单次两小时上限，请停止并创建新会话");
                if (new DriveInfo(Path.GetPathRoot(CaptureStore.Root)!).AvailableFreeSpace < 512L * 1024 * 1024) throw new IOException("剩余空间不足 512 MB，采集停止");
                BitmapSource? image = null;
                try
                {
                    image = GameWindowCapture.Capture(window);
                    if (image.PixelWidth != Session.Profile.Width || image.PixelHeight != Session.Profile.Height)
                        throw new InvalidOperationException("游戏分辨率已改变，请恢复原尺寸或停止后重新框选");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    if (!_paused)
                    {
                        _tracker.Flush(_lastFrame); _block++; _lastText = ""; _fingerprint = "";
                        Session.Warnings.Add($"{_clock.Elapsed.TotalSeconds:F2}s：{ex.Message}");
                        CaptureStore.Save(Session); _paused = true;
                    }
                    Status?.Invoke(ex.Message + "；连续录音仍在进行");
                }
                if (image is not null && image.PixelWidth == Session.Profile.Width && image.PixelHeight == Session.Profile.Height)
                {
                    double at = _clock.Elapsed.TotalSeconds;
                    _paused = false;
                    string work = Path.Combine(CaptureStore.Folder(Session.Id), "working");
                    string ja = Path.Combine(work, "ja.png"), zh = Path.Combine(work, "zh.png"), speaker = Path.Combine(work, "speaker.png");
                    var regions = new[] { Session.Profile.Japanese!, Session.Profile.Chinese!, Session.Profile.Speaker }.Where(r => r is not null).Select(r => r!).ToArray();
                    string fingerprint = GameWindowCapture.Fingerprint(image, regions);
                    bool reused = fingerprint == _fingerprint && _cachedText is not null;
                    if (!reused)
                    {
                        GameWindowCapture.Save(image, Session.Profile.Japanese!, ja);
                        GameWindowCapture.Save(image, Session.Profile.Chinese!, zh);
                        if (Session.Profile.Speaker is not null) GameWindowCapture.Save(image, Session.Profile.Speaker, speaker);
                        _cachedText = await _ocr.ReadAsync(ja, zh, Session.Profile.Speaker is null ? null : speaker, _cancel.Token);
                        _fingerprint = fingerprint;
                    }
                    var text = _cachedText!;
                    string key = text.Japanese + "\u001f" + text.Chinese + "\u001f" + text.Speaker;
                    if (key != _lastText || _screenshot.Length == 0 || (!reused && at - _lastEvidence >= 2))
                    {
                        _lastText = key;
                        _screenshot = Path.Combine("frames", Guid.NewGuid().ToString("N") + ".png");
                        var union = Session.Profile.EvidenceRegion();
                        _lastEvidence = at;
                        GameWindowCapture.Save(image, union, CaptureStore.SafeFile(Session.Id, _screenshot));
                        _frames!.WriteLine(JsonSerializer.Serialize(new { frame_s = at, screenshot = _screenshot,
                            origin_x = union.X, origin_y = union.Y, width = union.Width, height = union.Height, block = _block }));
                    }
                    _tracker.Observe(at, text.Japanese, text.Chinese, text.Speaker, _screenshot, _block);
                    _observations!.WriteLine(JsonSerializer.Serialize(new { frame_s = at, ocr_finished_s = _clock.Elapsed.TotalSeconds,
                        japanese = text.Japanese, chinese = text.Chinese, speaker = text.Speaker, screenshot = _screenshot, block = _block, cached_ocr = reused }));
                    _lastFrame = at;
                    Status?.Invoke($"采集中 {_clock.Elapsed:hh\\:mm\\:ss} · {Session.Entries.Count} 条原始候选 · {text.Japanese.Replace('\n', ' ')}");
                }
                await Task.Delay(350, _cancel.Token);
            }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Session.State = "异常停止"; Session.Warnings.Add(ex.Message);
            Status?.Invoke("采集异常：" + ex.Message);
            _clock.Stop();
            if (_audio is not null) await _audio.StopAsync();
            CaptureStore.Save(Session);
            Faulted?.Invoke(ex.Message);
        }
    }
    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _cancel.Cancel();
        Exception? failure = null;
        try { if (_loop is not null) await _loop; }
        catch (Exception ex) { failure = ex; }
        _clock.Stop();
        try { _tracker.Flush(_paused ? _lastFrame : _clock.Elapsed.TotalSeconds); }
        catch (Exception ex) { failure ??= ex; }
        try
        {
            if (_audio is not null)
            {
                await _audio.StopAsync(); Session.DurationSeconds = _audio.Duration;
                if (_audio.Error is not null) { Session.Warnings.Add(_audio.Error.Message); Session.State = "录音异常"; }
            }
        }
        catch (Exception ex) { failure ??= ex; }
        finally { _audio?.Dispose(); _ocr.Dispose(); _cancel.Dispose(); _observations?.Dispose(); _observations = null; _frames?.Dispose(); _frames = null; }
        foreach (var entry in Session.Entries) entry.EndSeconds = Math.Min(entry.EndSeconds, Session.DurationSeconds);
        if (_audio is not null && !_audio.Signal.HasSignal)
        {
            Session.Warnings.Add("整段录音未记录到有效信号，不能用于语音训练；截图仍已保留。");
            Session.State = "录音无信号";
        }
        if (Session.State == "采集中") Session.State = "待整理";
        if (failure is not null) { Session.State = "保存异常"; Session.Warnings.Add(failure.Message); }
        CaptureStore.Save(Session);
        if (failure is not null) throw new IOException("采集已停止，但部分数据未能保存", failure);
    }
}
