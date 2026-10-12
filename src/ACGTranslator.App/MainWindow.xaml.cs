using NAudio.Wave;
using System.Windows;
using System.Windows.Input;
using ACGTranslator.Models;
using ACGTranslator.Services;

namespace ACGTranslator;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AudioCaptureService _audioCapture = new();
    private readonly AudioPipeline _audioPipeline = new();
    private LocalAsrClient _asrClient;
    private LocalTranslationClient _translationClient;
    private readonly ModelStore _modelStore;
    private readonly GpuInfo? _gpu;
    private string? _engineError;
    private SubtitleProcessor? _processor;
    private SessionInfo _session = new(Guid.Empty, "");
    private int _segmentsDetected, _subtitlesCompleted;
    private string _lastError = "";
    private DateTime _lastLevelUpdate;

    public MainWindow()
    {
        InitializeComponent();
        _settings = App.Settings.Load();
        try
        {
            _modelStore = new ModelStore(ModelCatalog.LoadDefault(), AppPaths.Models);
            _gpu = HardwareInfo.DetectNvidia();
            (_asrClient, _translationClient) = CreateClients();
        }
        catch (Exception ex)
        {
            _engineError = ex.Message;
            _modelStore = new ModelStore(new ModelCatalog(), AppPaths.Models);
            _gpu = null;
            (_asrClient, _translationClient) = CreateFallbackClients();
        }
        _audioCapture.AudioChunk += AudioCapture_AudioChunk;
        _audioCapture.Stopped += AudioCapture_Stopped;
        _audioPipeline.SegmentReady += AudioPipeline_SegmentReady;
        RestoreSavedBounds();
        ApplySettings();
    }

    private void RestoreSavedBounds()
    {
        if (_settings.Width is > 200) Width = _settings.Width.Value;
        if (_settings.Height is > 120) Height = _settings.Height.Value;
        if (_settings.Left is not { } left || _settings.Top is not { } top) return;
        // Ignore a saved position that is no longer on any monitor.
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.IntersectsWith(new Rect(left, top, Width, Height))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    private void ApplySettings()
    {
        Topmost = _settings.IsAlwaysOnTop;
        Opacity = _settings.OverlayOpacity;
        SourceTextBlock.Visibility = _settings.ShowSource ? Visibility.Visible : Visibility.Collapsed;
        ChineseTextBlock.FontSize = _settings.FontSize;
        SourceTextBlock.FontSize = Math.Max(12, _settings.FontSize * 0.65);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await WarmUpAsync();

    private async Task WarmUpAsync()
    {
        // Loading Whisper and the translation packages takes seconds; do it before the user presses start.
        StatusText.Text = "正在加载本地模型…";
        var results = await Task.WhenAll(TryWarmUpAsync(_asrClient), TryWarmUpAsync(_translationClient));
        if (_engineError is not null) results = [.. results, _engineError];
        var errors = results.Where(error => error is not null).ToList();
        if (_audioCapture.IsCapturing) return;
        StatusText.Text = errors.Count == 0 ? "本地模型已就绪" : "模型未就绪";
        if (errors.Count > 0) ShowError(string.Join("\n", errors));
    }

    private static async Task<string?> TryWarmUpAsync(WorkerProcess worker)
    {
        try { await worker.WarmUpAsync(); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_audioCapture.IsCapturing)
        {
            StartButton.IsEnabled = false;
            _audioCapture.Stop();
            return;
        }
        try
        {
            _segmentsDetected = 0;
            _subtitlesCompleted = 0;
            _session = new(Guid.NewGuid(), $"实时字幕 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _processor = new SubtitleProcessor(_asrClient, _translationClient);
            _processor.Recognized += Processor_Recognized;
            _processor.Completed += Processor_Completed;
            _processor.Failed += Processor_Failed;
            _audioPipeline.Start(_settings.MaxSegmentMs);
            _audioCapture.Start(_settings.OutputDeviceId);
            StartButton.Content = "停止识别";
            SettingsButton.IsEnabled = false;
            StatusText.Text = $"采集中 · {_audioCapture.DeviceName}";
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _audioPipeline.Stop();
            _ = StopProcessorAsync();
            StatusText.Text = "采集失败";
            MessageBox.Show(this, $"无法启动系统声音采集：{ex.Message}", "音频采集错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AudioCapture_AudioChunk(object? sender, AudioChunkEventArgs e)
    {
        _audioPipeline.Push(e);
        // Capture delivers ~100 chunks per second; refreshing the label at 5 Hz is enough.
        var now = DateTime.UtcNow;
        if (now - _lastLevelUpdate < TimeSpan.FromMilliseconds(200)) return;
        _lastLevelUpdate = now;
        var level = Math.Min(100, (int)Math.Round(e.Rms * 240));
        Dispatcher.BeginInvoke(() => LevelBar.Value = level);
    }

    private void AudioPipeline_SegmentReady(object? sender, SpeechSegmentEventArgs e)
    {
        Interlocked.Increment(ref _segmentsDetected);
        _processor?.Enqueue(e, _session);
        Dispatcher.BeginInvoke(UpdateProgress);
    }

    private void Processor_Recognized(object? sender, SubtitleEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        SourceTextBlock.Text = $"[{LanguageName(e.Entry.SourceLanguage)}] {e.Entry.SourceText}";
        ChineseTextBlock.Text = SubtitleProcessor.IsTranslatable(e.Entry.SourceLanguage) ? "正在翻译…" : "";
    });

    private void Processor_Completed(object? sender, SubtitleEventArgs e)
    {
        if (_settings.SaveHistory)
        {
            try { App.History.Add(e.Entry); }
            catch (Exception ex) { Processor_Failed(this, new($"保存历史失败：{ex.Message}")); }
        }
        Interlocked.Increment(ref _subtitlesCompleted);
        Dispatcher.BeginInvoke(() =>
        {
            SourceTextBlock.Text = $"[{LanguageName(e.Entry.SourceLanguage)}] {e.Entry.SourceText}";
            ChineseTextBlock.Text = string.IsNullOrWhiteSpace(e.Entry.ChineseText) ? $"（{e.Entry.Status}）" : e.Entry.ChineseText;
            UpdateProgress();
        });
    }

    private void Processor_Failed(object? sender, ProcessingErrorEventArgs e) => Dispatcher.BeginInvoke(() => ShowError(e.Message));

    private void ShowError(string message)
    {
        _lastError = message;
        ErrorText.Text = message;
        ErrorText.ToolTip = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void UpdateProgress()
    {
        if (!_audioCapture.IsCapturing) return;
        var dropped = _processor?.Dropped ?? 0;
        StatusText.Text = $"采集中 · 片段 {_segmentsDetected} · 字幕 {_subtitlesCompleted}" + (dropped > 0 ? $" · 跳过 {dropped}" : "");
    }

    private async void AudioCapture_Stopped(object? sender, StoppedEventArgs e)
    {
        _audioPipeline.Stop();
        await Dispatcher.InvokeAsync(() =>
        {
            StatusText.Text = "正在处理剩余片段…";
            LevelBar.Value = 0;
        });
        await StopProcessorAsync();
        await Dispatcher.InvokeAsync(() =>
        {
            StartButton.Content = "开始识别";
            StartButton.IsEnabled = true;
            SettingsButton.IsEnabled = true;
            StatusText.Text = e.Exception is null ? $"已停止 · 共 {_subtitlesCompleted} 条字幕" : "音频设备已断开";
            if (e.Exception is not null) ShowError($"音频设备已断开：{e.Exception.Message}");
        });
    }

    private async Task StopProcessorAsync()
    {
        var processor = _processor;
        _processor = null;
        if (processor is null) return;
        await processor.DisposeAsync();
        processor.Recognized -= Processor_Recognized;
        processor.Completed -= Processor_Completed;
        processor.Failed -= Processor_Failed;
    }

    private static string LanguageName(string code) => code switch
    {
        "ja" => "日语",
        "en" => "英语",
        "zh" => "中文",
        _ => code
    };

    private void TestButton_Click(object sender, RoutedEventArgs e)
    {
        // Preview only; nothing is written to history.
        SourceTextBlock.Text = "[日语] これはテスト字幕です。";
        ChineseTextBlock.Text = "这是一条测试字幕。";
    }

    private void ErrorText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_lastError.Length > 0) MessageBox.Show(this, _lastError, "错误详情", MessageBoxButton.OK, MessageBoxImage.Warning);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e) => new HistoryWindow { Owner = this }.ShowDialog();

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var previous = (_settings.PythonExecutable, _settings.AsrModelPath, _settings.AsrDevice, _settings.Language, _settings.AsrModelId, _settings.TranslatorId, _settings.TranslatorDevice);
        var window = new SettingsWindow(_settings, _modelStore) { Owner = this };
        if (window.ShowDialog() != true) return;
        ApplySettings();
        if (previous == (_settings.PythonExecutable, _settings.AsrModelPath, _settings.AsrDevice, _settings.Language, _settings.AsrModelId, _settings.TranslatorId, _settings.TranslatorDevice)) return;
        // Worker options are fixed at process start, so restart them with the new settings.
        _asrClient.Dispose();
        _translationClient.Dispose();
        try
        {
            (_asrClient, _translationClient) = CreateClients();
            _engineError = null;
        }
        catch (Exception ex)
        {
            _engineError = ex.Message;
            (_asrClient, _translationClient) = CreateFallbackClients();
        }
        ErrorText.Visibility = Visibility.Collapsed;
        await WarmUpAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Width = Width;
            _settings.Height = Height;
            try { App.Settings.Save(_settings); } catch { }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _audioCapture.Dispose();
        _audioPipeline.Dispose();
        _processor?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _asrClient.Dispose();
        _translationClient.Dispose();
        base.OnClosed(e);
    }

    private (LocalAsrClient Asr, LocalTranslationClient Translation) CreateClients()
    {
        var asr = EngineResolver.ResolveAsr(_settings, _modelStore, _gpu);
        var translator = EngineResolver.ResolveTranslator(_settings, _modelStore, _gpu);
        return (new LocalAsrClient(AppPaths.Python(_settings.PythonExecutable), asr, _settings.Language),
            new LocalTranslationClient(AppPaths.Python(_settings.PythonExecutable), translator));
    }

    private (LocalAsrClient Asr, LocalTranslationClient Translation) CreateFallbackClients()
    {
        var asr = new AsrPlan("未安装识别模型", Path.Combine(AppPaths.Models, "asr", "whisper-base"), "cpu", []);
        var translator = new TranslatorPlan(new CatalogItem { Id = "argos", Name = "未安装翻译引擎", Engine = "argos", Languages = ["ja", "en"] },
            ["--engine", "argos", "--model", Path.Combine(AppPaths.Models, "translators", "argos")], "cpu");
        return (new LocalAsrClient(AppPaths.Python(_settings.PythonExecutable), asr, _settings.Language),
            new LocalTranslationClient(AppPaths.Python(_settings.PythonExecutable), translator));
    }
}
