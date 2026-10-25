using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ACGTranslator.Models;
using ACGTranslator.Services;
using NAudio.Wave;

namespace ACGTranslator;

public partial class CaptureWindow : Window
{
    private CaptureProfile _profile = new();
    private GameCaptureRunner? _runner;
    private CaptureSession? _session;
    private WaveOutEvent? _player;
    private WaveFileReader? _playReader;
    private bool _busy, _closing;
    private bool _loadingEntry = true, _dirty;
    private double? _ownerOpacity;
    private bool _ownerTopmost;
    private string _profileKey = "";
    private string? _handoff;
    private static string ProfilePath => Path.Combine(CaptureStore.Root, "regions.json");
    public CaptureWindow()
    {
        InitializeComponent();
        RefreshWindows();
        try { DeviceBox.ItemsSource = AudioCaptureService.GetOutputDevices(); DeviceBox.SelectedIndex = 0; }
        catch (Exception ex) { CaptureStatus.Text = "无法枚举声音设备：" + ex.Message; }
        ReloadSessions();
        _loadingEntry = false;
    }
    private void Error(Exception ex) { CaptureStatus.Text = ex.Message; MessageBox.Show(this, ex.Message, "游戏采集", MessageBoxButton.OK, MessageBoxImage.Warning); }
    private void RefreshWindows()
    {
        var windows = GameWindowCapture.List().Where(w => !w.Title.StartsWith("游戏双语测试采集") && !w.Title.StartsWith("游戏训练素材采集") && w.Title != "ACGTranslator").ToList();
        WindowBox.ItemsSource = windows;
        WindowBox.SelectedItem = windows.FirstOrDefault(w => w.Title.Contains("天使") || w.Title.Contains("天使騒々") || w.Title.Contains("TenShi", StringComparison.OrdinalIgnoreCase));
    }
    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindows();
    private void WindowBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RegionStatus is null) return;
        _profile = new(); _profileKey = (WindowBox.SelectedItem as GameWindow)?.Title ?? "";
        try
        {
            if (File.Exists(ProfilePath))
            {
                var profiles = JsonSerializer.Deserialize<Dictionary<string, CaptureProfile>>(File.ReadAllText(ProfilePath));
                if (profiles?.TryGetValue(_profileKey, out var saved) == true) _profile = saved;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { CaptureStatus.Text = "区域配置读取失败，请重新框选"; }
        ShowRegions();
    }
    private void ShowRegions() => RegionStatus.Text = $"画面 {_profile.Width}×{_profile.Height} · 日文 {Describe(_profile.Japanese)} · 中文 {Describe(_profile.Chinese)} · 角色 {Describe(_profile.Speaker)}";
    private static string Describe(CaptureRegion? r) => r is null ? "未设置" : $"({r.X},{r.Y}) {r.Width}×{r.Height}";
    private void SaveProfile()
    {
        Directory.CreateDirectory(CaptureStore.Root);
        Dictionary<string, CaptureProfile> profiles = [];
        if (File.Exists(ProfilePath))
        {
            try { profiles = JsonSerializer.Deserialize<Dictionary<string, CaptureProfile>>(File.ReadAllText(ProfilePath)) ?? []; }
            catch (JsonException) { }
        }
        profiles[_profileKey] = _profile;
        File.WriteAllText(ProfilePath + ".tmp", JsonSerializer.Serialize(profiles));
        File.Move(ProfilePath + ".tmp", ProfilePath, true);
    }
    private void HideOverlay()
    {
        if (Owner is not null && _ownerOpacity is null)
        {
            _ownerOpacity = Owner.Opacity; _ownerTopmost = Owner.Topmost;
            Owner.Topmost = false; Owner.Opacity = 0;
        }
    }
    private void RestoreOverlay()
    {
        if (Owner is not null && _ownerOpacity is { } value) { Owner.Opacity = value; Owner.Topmost = _ownerTopmost; }
        _ownerOpacity = null;
    }
    private async Task<BitmapSource> SnapshotAsync()
    {
        if (WindowBox.SelectedItem is not GameWindow game) throw new InvalidOperationException("请先选择游戏窗口");
        HideOverlay(); Hide();
        try
        {
            GameWindowCapture.SetForegroundWindow(game.Handle);
            await Task.Delay(450);
            return GameWindowCapture.Capture(game.Handle);
        }
        finally { Show(); Activate(); if (_runner is null) RestoreOverlay(); }
    }
    private async void SelectRegion_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _runner is not null) return;
        _busy = true;
        try
        {
            var image = await SnapshotAsync();
            string tag = ((Button)sender).Tag.ToString()!;
            var dialog = new RegionSelectionWindow(image, tag == "ja" ? "日文" : tag == "zh" ? "中文" : "角色名") { Owner = this };
            if (dialog.ShowDialog() != true) return;
            if (_profile.Width != image.PixelWidth || _profile.Height != image.PixelHeight) _profile = new() { Width = image.PixelWidth, Height = image.PixelHeight };
            if (tag == "ja") _profile.Japanese = dialog.Region;
            else if (tag == "zh") _profile.Chinese = dialog.Region;
            else _profile.Speaker = dialog.Region;
            SaveProfile(); ShowRegions();
        }
        catch (Exception ex) { Error(ex); }
        finally { _busy = false; }
    }
    private void ClearSpeaker_Click(object sender, RoutedEventArgs e)
    {
        try { _profile.Speaker = null; SaveProfile(); ShowRegions(); } catch (Exception ex) { Error(ex); }
    }
    private async void PreviewOcr_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!_profile.Valid) throw new InvalidOperationException("请先框选日文和中文区域");
            var image = await SnapshotAsync();
            if (image.PixelWidth != _profile.Width || image.PixelHeight != _profile.Height) throw new InvalidOperationException("画面尺寸已变，请重新框选");
            var folder = Path.Combine(CaptureStore.Root, "calibration"); Directory.CreateDirectory(folder);
            var ja = Path.Combine(folder, "ja.png"); var zh = Path.Combine(folder, "zh.png");
            GameWindowCapture.Save(image, _profile.Japanese!, ja); GameWindowCapture.Save(image, _profile.Chinese!, zh);
            using var ocr = new GameOcrClient();
            CaptureStatus.Text = "正在检查本地日中 OCR…";
            var result = await ocr.ReadAsync(ja, zh, null, CancellationToken.None);
            MessageBox.Show(this, $"日文：\n{result.Japanese}\n\n中文：\n{result.Chinese}\n\n请与游戏画面核对。空白或错字较多时请调整框选区域。", "OCR 预览");
            CaptureStatus.Text = "OCR 预览完成";
        }
        catch (Exception ex) { Error(ex); }
        finally { _busy = false; }
    }
    private async void CheckAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _runner is not null) return;
        _busy = true; SetupPanel.IsEnabled = false; StartCapture.IsEnabled = false;
        try
        {
            string folder = Path.Combine(CaptureStore.Root, "audio-checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            var clock = Stopwatch.StartNew();
            using var audio = new GameCaptureAudio(folder, clock);
            audio.Start((DeviceBox.SelectedItem as AudioDeviceOption)?.Id ?? "");
            string deviceName = audio.DeviceName;
            if (WindowBox.SelectedItem is GameWindow game) { WindowState = WindowState.Minimized; GameWindowCapture.SetForegroundWindow(game.Handle); }
            CaptureStatus.Text = "正在检查所选输出设备的声音，5 秒后显示结果…";
            try { await Task.Delay(5000); }
            finally { clock.Stop(); await audio.StopAsync(); WindowState = WindowState.Normal; Show(); Activate(); }
            if (audio.Error is not null) throw new IOException("录音检查失败", audio.Error);
            bool ok = audio.Signal.HasSignal;
            AudioSignalText.Text = ok ? $"录音检查通过：{deviceName}，峰值 {audio.Signal.Peak:P2}" : $"没有录到声音：{deviceName}，请检查游戏输出设备。";
            CaptureStatus.Text = AudioSignalText.Text;
            MessageBox.Show(this, AudioSignalText.Text + (ok ? "\n已检测到音频信号；开始采集后仍会持续监测。" : "\n请让游戏播放有声内容，并选择实际发声的输出设备后重试。"), "录音检查");
        }
        catch (Exception ex) { Error(ex); }
        finally { _busy = false; SetupPanel.IsEnabled = true; StartCapture.IsEnabled = true; }
    }
    private void SetRecording(bool active)
    {
        SetupPanel.IsEnabled = !active; SessionPanel.IsEnabled = !active; ReviewPanel.IsEnabled = !active;
        StartCapture.IsEnabled = !active; StopCapture.IsEnabled = active;
    }
    private async void StartCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _runner is not null) return;
        _busy = true;
        try
        {
            if (WindowBox.SelectedItem is not GameWindow game) throw new InvalidOperationException("请选择游戏窗口");
            if (!_profile.Valid) throw new InvalidOperationException("请先框选日文和中文，并检查 OCR 预览");
            if (string.IsNullOrWhiteSpace(GameName.Text)) throw new InvalidOperationException("请填写游戏名");
            StopPlayback(); SetRecording(true); StopCapture.IsEnabled = false;
            _runner = new(GameName.Text.Trim(), _profile);
            _runner.Status += text =>
            {
                CaptureStatus.Text = text;
                if (_runner?.AudioSignal is { } signal)
                    AudioSignalText.Text = signal.HasSignal ? $"录音信号已收到 · 当前音量 {signal.RecentRms:P2}" : "尚未录到声音；连续 30 秒无信号会停止，请检查播放设备。";
            };
            _runner.Faulted += reason => Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (_busy || _runner is null) return;
                _busy = true;
                try { await StopAsync(); WindowState = WindowState.Normal; Show(); Activate(); Error(new IOException(reason)); }
                catch (Exception ex) { Error(ex); }
                finally { _busy = false; }
            }));
            _runner.Updated += () => { EntriesGrid.ItemsSource = _runner?.Session.Entries.ToList(); };
            CaptureStatus.Text = "正在启动离线 OCR 和录音…";
            await _runner.StartAsync(game.Handle, (DeviceBox.SelectedItem as AudioDeviceOption)?.Id ?? "");
            _session = _runner.Session; EntriesGrid.ItemsSource = null;
            HideOverlay(); StopCapture.IsEnabled = true;
            WindowState = WindowState.Minimized; GameWindowCapture.SetForegroundWindow(game.Handle);
        }
        catch (Exception ex) { _runner = null; SetRecording(false); RestoreOverlay(); Error(ex); }
        finally { _busy = false; }
    }
    private async Task StopAsync()
    {
        if (_runner is null) return;
        var runner = _runner;
        StopCapture.IsEnabled = false;
        try
        {
            await runner.StopAsync();
            _session = runner.Session;
            CaptureStatus.Text = $"已保存 {_session.Entries.Count} 条原始候选，正在生成整理包…";
            try
            {
                _handoff = await Task.Run(() => CaptureHandoff.Export(runner.Session));
                CaptureStatus.Text = $"已保存并生成整理包 · {_session.Entries.Count} 条，无需逐条确认。回到对话说“整理最新采集”即可。";
            }
            catch (Exception ex) { CaptureStatus.Text = "原始采集已保存，但整理包生成失败，可稍后重试：" + ex.Message; }
        }
        finally { _runner = null; RestoreOverlay(); SetRecording(false); ReloadSessions(_session?.Id); }
    }
    private async void StopCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try { await StopAsync(); } catch (Exception ex) { Error(ex); } finally { _busy = false; }
    }
    private void ReloadSessions(string? selected = null)
    {
        var sessions = CaptureStore.LoadAll();
        SessionsBox.ItemsSource = sessions;
        SessionsBox.SelectedItem = sessions.FirstOrDefault(s => s.Id == selected) ?? sessions.FirstOrDefault();
    }
    private void RefreshSessions_Click(object sender, RoutedEventArgs e) { try { ReloadSessions(_session?.Id); } catch (Exception ex) { Error(ex); } }
    private void SessionsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopPlayback(); _session = SessionsBox.SelectedItem as CaptureSession;
        EntriesGrid.ItemsSource = _session?.Entries;
        _handoff = _session is null ? null : CaptureHandoff.Latest(_session.Id);
        HandoffStatus.Text = _handoff is null ? "未生成整理包；旧会话可点击“生成整理包”，不需要重新录制。" : "整理包：" + _handoff;
    }
    private void EntriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _loadingEntry = true; _dirty = false;
        StopPlayback();
        var entry = EntriesGrid.SelectedItem as CaptureEntry;
        JapaneseText.Text = entry?.Japanese ?? ""; ChineseText.Text = entry?.Chinese ?? ""; SpeakerText.Text = entry?.Speaker ?? "";
        BeginText.Text = entry?.StartSeconds.ToString("F3", CultureInfo.CurrentCulture) ?? "";
        EndText.Text = entry?.EndSeconds.ToString("F3", CultureInfo.CurrentCulture) ?? "";
        EntryNote.Text = entry is null ? "选择条目查看截图和音频" : $"{entry.Note} · 画面区间 {entry.VisualStart:F2}–{entry.VisualEnd:F2}s";
        EvidenceImage.Source = null;
        if (entry is not null && _session is not null)
        {
            try
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(CaptureStore.SafeFile(_session.Id, entry.Screenshot)); image.EndInit(); image.Freeze(); EvidenceImage.Source = image;
            }
            catch (Exception ex) { EntryNote.Text += " · 截图读取失败：" + ex.Message; }
        }
        _loadingEntry = false;
    }
    private void ReviewTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingEntry || _session is null || EntriesGrid.SelectedItem is not CaptureEntry entry) return;
        _dirty = true;
        CaptureStatus.Text = "修改尚未保存；请保存后再切换条目。已修改条目需重新确认。";
        if (entry.Review == "已确认")
        {
            entry.Review = "待复核";
            try { CaptureStore.Save(_session); }
            catch (Exception ex) { CaptureStatus.Text = "撤销确认状态失败：" + ex.Message; }
        }
    }
    private (double Start, double End) Range()
    {
        if (!double.TryParse(BeginText.Text, out double start) || !double.TryParse(EndText.Text, out double end) ||
            !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > (_session?.DurationSeconds ?? 0) + .001 || end - start > 120)
            throw new InvalidOperationException("请填写有效的起止秒数，结束不能超过录音长度，单条不超过 120 秒");
        return (start, end);
    }
    private void SaveEntry(string state)
    {
        if (_session is null || EntriesGrid.SelectedItem is not CaptureEntry entry) return;
        var range = Range();
        if (state == "已确认" && string.IsNullOrWhiteSpace(JapaneseText.Text)) throw new InvalidOperationException("确认前需填写实际说出的日文原文");
        entry.Japanese = JapaneseText.Text.Trim(); entry.Chinese = ChineseText.Text.Trim(); entry.Speaker = SpeakerText.Text.Trim();
        entry.StartSeconds = range.Start; entry.EndSeconds = range.End; entry.Review = state;
        CaptureStore.Save(_session); _dirty = false; EntriesGrid.Items.Refresh(); CaptureStatus.Text = "已保存 · " + state;
    }
    private void SaveEntry_Click(object sender, RoutedEventArgs e) { try { SaveEntry("待复核"); } catch (Exception ex) { Error(ex); } }
    private void Confirm_Click(object sender, RoutedEventArgs e) { try { SaveEntry("已确认"); } catch (Exception ex) { Error(ex); } }
    private void Exclude_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_session is null || EntriesGrid.SelectedItem is not CaptureEntry entry) return;
            entry.Review = "已排除"; CaptureStore.Save(_session); EntriesGrid.Items.Refresh();
        }
        catch (Exception ex) { Error(ex); }
    }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_session is null || EntriesGrid.SelectedItem is not CaptureEntry) return;
            StopPlayback(); var range = Range();
            string file = Path.Combine(CaptureStore.Folder(_session.Id), "preview.wav");
            CaptureAudioExport.Cut(Path.Combine(CaptureStore.Folder(_session.Id), "capture.wav"), file, range.Start, range.End);
            _playReader = new WaveFileReader(file); _player = new WaveOutEvent(); _player.Init(_playReader); _player.Play();
        }
        catch (Exception ex) { StopPlayback(); Error(ex); }
    }
    private void StopPlayback() { _player?.Stop(); _player?.Dispose(); _player = null; _playReader?.Dispose(); _playReader = null; }
    private void StopPlay_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(CaptureStore.Root); Process.Start(new ProcessStartInfo(_session is null ? CaptureStore.Root : CaptureStore.Folder(_session.Id)) { UseShellExecute = true }); }
        catch (Exception ex) { Error(ex); }
    }
    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        if (MessageBox.Show(this, "删除这个采集会话的录音、截图、复核记录和导出文件？不会删除游戏文件。", "删除采集会话", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { StopPlayback(); CaptureStore.Delete(_session.Id); _session = null; ReloadSessions(); }
        catch (Exception ex) { Error(ex); }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _busy) return;
        if (_dirty) { MessageBox.Show(this, "当前修改尚未保存，请先保存或重新确认，再导出。", "导出评测清单"); return; }
        _busy = true; ReviewPanel.IsEnabled = false; SessionPanel.IsEnabled = false; StartCapture.IsEnabled = false;
        try
        {
            StopPlayback(); var session = _session;
            string result = await Task.Run(() => CaptureHandoff.Export(session));
            _handoff = result; HandoffStatus.Text = "整理包：" + result;
            CaptureStatus.Text = $"全部 {session.Entries.Count} 条候选已打包，等待助手整理：" + result;
            MessageBox.Show(this, $"整理包已生成：\n{result}\n\n已保留全部原始候选，不要求逐条确认。回到对话说“整理最新采集”，或复制整理请求发给助手。", "素材已保存");
        }
        catch (Exception ex) { Error(ex); }
        finally { _busy = false; ReviewPanel.IsEnabled = true; SessionPanel.IsEnabled = true; StartCapture.IsEnabled = true; }
    }
    private void CopyHandoff_Click(object sender, RoutedEventArgs e)
    {
        if (_handoff is null) { CaptureStatus.Text = "请先停止保存，或为旧会话生成整理包。"; return; }
        try
        {
            Clipboard.SetText($"请整理这个游戏训练素材包：{_handoff}。结合截图修正原文，核对录音与边界，导出训练数据和修改记录；无法确认的条目单列。不要自动校准设置。" );
            CaptureStatus.Text = "已复制整理请求，粘贴到助手对话即可。";
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) return;
        if (_busy) { e.Cancel = true; return; }
        if (_runner is not null)
        {
            e.Cancel = true; _busy = true;
            try { await StopAsync(); }
            catch (Exception ex) { Error(ex); }
            finally { _busy = false; _closing = true; Close(); }
        }
        StopPlayback(); RestoreOverlay();
    }
}
