using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ACGTranslator.Models;
using ACGTranslator.Services;
using Microsoft.Win32;

namespace ACGTranslator;

public partial class CalibrationWindow : Window
{
    private readonly AppSettings _active;
    private string[] _inputs = [];
    private CancellationTokenSource? _cancel;
    private bool _running;
    private readonly List<object> _candidates = [];
    private AppSettings _original;
    private string? _turnModel;
    public CalibrationWindow(AppSettings settings)
    {
        _active = settings; _original = settings;
        InitializeComponent(); LoadCandidates(); ReloadRuns();
    }
    private void LoadCandidates()
    {
        _candidates.Clear(); _turnModel = null;
        try
        {
            _original = App.Settings.Load();
            if (_original.PreserveAsrContext || _original.SpeechSegmentation || _original.AdaptiveAsr || _original.AsrComputeType != "default" || !string.IsNullOrWhiteSpace(_original.AsrHotwords))
                throw new InvalidOperationException("当前校准器仅支持默认识别流程。请先在设置中关闭完整上下文、实验性分段、按需复核，选择设备默认精度并暂时清空术语提示。");
            var store = new ModelStore(ModelCatalog.LoadDefault(), AppPaths.Models);
            var gpu = HardwareInfo.DetectNvidia();
            var plan = EngineResolver.ResolveAsr(_original, store, gpu);
            var runtime = store.InstalledRuntime("whisper-cuda");
            var translator = EngineResolver.SelectInstalled(store, CatalogKind.Translator, _original.TranslatorId, gpu is not null);
            int translationVram = _original.TranslatorDevice == "cpu" || gpu is null ? 0 : translator?.VramMb ?? 0;
            int beam = _original.AsrBeamSize is 1 or 2 or 5 ? _original.AsrBeamSize : plan.Device == "cuda" ? 5 : 2;
            var seen = new HashSet<string>();
            void Add(string name, string path, string device, int width, IEnumerable<string> dlls)
            {
                path = Path.GetFullPath(path);
                if (!seen.Add($"{path}|{device}|{width}")) return;
                _candidates.Add(new { name = $"{name} / {device} / beam {width}", model = path, device, beam = width, language = _original.Language, dll_dirs = dlls.ToArray() });
            }
            Add("当前配置", plan.ModelPath, plan.Device, beam, plan.DllDirectories);
            var paths = new[] { plan.ModelPath }.Concat(store.Installed(CatalogKind.Asr).Select(store.GetDirectory)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3);
            string[] devices = gpu is not null && runtime is not null ? [plan.Device, plan.Device == "cuda" ? "cpu" : "cuda"] : ["cpu"];
            foreach (var path in paths)
                foreach (var device in devices)
                    foreach (int width in new[] { 1, 2 })
                    {
                        var known = store.Installed(CatalogKind.Asr).FirstOrDefault(i => string.Equals(store.GetDirectory(i), path, StringComparison.OrdinalIgnoreCase));
                        if (device == "cuda" && known is not null && known.VramMb + translationVram + 1000 > gpu!.MemoryMb) continue;
                        Add(Path.GetFileName(path), path, device, width, device == "cuda" ? [store.GetDirectory(runtime!)] : []);
                    }
            if (_original.TurnEnhancement)
            {
                var speaker = store.Installed(CatalogKind.Speaker).FirstOrDefault();
                if (speaker is not null) _turnModel = Path.Combine(store.GetDirectory(speaker), "speaker.onnx");
            }
            CandidateText.Text = $"共 {_candidates.Count} 个配置；当前：{plan.Summary}，beam {beam}。至少 12 条已核验语音、3 个独立场景/时间块；未安装模型不会下载。";
            RunButton.IsEnabled = true;
        }
        catch (Exception ex) { CandidateText.Text = "无法校准：" + ex.Message; RunButton.IsEnabled = false; }
    }
    private void PickInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "原文字幕或已核验清单|*.srt;*.jsonl;*.csv", Multiselect = true };
        if (dialog.ShowDialog(this) == true) { _inputs = dialog.FileNames; InputText.Text = string.Join("；", _inputs); VerifiedBox.IsChecked = false; }
    }
    private void PickAudio_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "录音|*.wav;*.flac;*.ogg;*.mp3;*.opus" };
        if (dialog.ShowDialog(this) == true) { AudioText.Text = dialog.FileName; VerifiedBox.IsChecked = false; }
    }
    private void ClearAudio_Click(object sender, RoutedEventArgs e) => AudioText.Text = "";
    private void SetBusy(bool value)
    {
        _running = value; Configuration.IsEnabled = !value; RunButton.IsEnabled = !value;
        CancelButton.IsEnabled = value; RestoreButton.IsEnabled = !value; RunsBox.IsEnabled = !value;
    }
    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (_inputs.Length == 0 || VerifiedBox.IsChecked != true) { MessageBox.Show(this, "请选择素材，并确认原文、配音和时间对应关系已核验。"); return; }
        bool srt = _inputs.Any(p => Path.GetExtension(p).Equals(".srt", StringComparison.OrdinalIgnoreCase));
        if ((srt && (_inputs.Length != 1 || !File.Exists(AudioText.Text))) || (!srt && AudioText.Text.Length > 0))
        { MessageBox.Show(this, "SRT 模式需一份原文字幕和一段对应录音；清单模式请清除额外录音。"); return; }
        LoadCandidates();
        if (_candidates.Count == 0 || !RunButton.IsEnabled) return;
        string folder = Path.Combine(AsrCalibrationSettings.Root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var cancel = new CancellationTokenSource(); _cancel = cancel; SetBusy(true); ResultsText.Clear();
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "settings-before.json"), JsonSerializer.Serialize(_original));
            var config = new { inputs = _inputs, audio = srt ? AudioText.Text : null, verified = true,
                language = ((ComboBoxItem)LanguageBox.SelectedItem).Tag.ToString(), mode = ((ComboBoxItem)ModeBox.SelectedItem).Tag.ToString(),
                budget_seconds = int.Parse(((ComboBoxItem)BudgetBox.SelectedItem).Content.ToString()!) * 60,
                max_rtf = 1.0, candidates = _candidates, turn_model = _turnModel,
                worker_path = Path.Combine(AppContext.BaseDirectory, "Resources", "asr_worker.py") };
            string configPath = Path.Combine(folder, "config.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(config));
            string script = Path.Combine(AppContext.BaseDirectory, "Resources", "autotune.py");
            if (!File.Exists(script)) throw new FileNotFoundException("校准脚本未随程序打包", script);
            var info = new ProcessStartInfo(AppPaths.Python(_original.PythonExecutable)) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var arg in new[] { "-u", script, "--config", configPath, "--out", folder }) info.ArgumentList.Add(arg);
            info.Environment["HF_HUB_OFFLINE"] = "1"; info.Environment["PYTHONIOENCODING"] = "utf-8";
            using var process = new Process { StartInfo = info };
            process.Start();
            using var cleanup = new ProcessCleanup(process);
            using var registration = cancel.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            var errors = process.StandardError.ReadToEndAsync();
            while (await process.StandardOutput.ReadLineAsync(cancel.Token) is { } line)
            {
                try
                {
                    using var message = JsonDocument.Parse(line); var r = message.RootElement;
                    if (r.GetProperty("event").GetString() == "progress")
                        ProgressText.Text = $"{r.GetProperty("candidate").GetString()} · {r.GetProperty("phase").GetString()} · {r.GetProperty("sample")}/{r.GetProperty("total")}";
                    else ResultsText.AppendText(line + "\n");
                }
                catch (JsonException) { ResultsText.AppendText(line + "\n"); }
            }
            await process.WaitForExitAsync(cancel.Token);
            string stderr = await errors;
            cancel.Token.ThrowIfCancellationRequested();
            string reportPath = Path.Combine(folder, "report.json");
            if (!File.Exists(reportPath)) throw new IOException("校准未生成报告：" + stderr);
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            var root = report.RootElement;
            ResultsText.Text = Describe(root) + "\n\n报告目录：" + folder;
            if (process.ExitCode != 0 || !root.GetProperty("eligible").GetBoolean())
            { ProgressText.Text = "未应用：验证未通过、素材不足或运行未完成"; return; }
            if (AutoApplyBox.IsChecked == true)
            {
                cancel.Token.ThrowIfCancellationRequested();
                AsrCalibrationSettings.Apply(_active, _original, root.GetProperty("recommendation"), folder);
                ProgressText.Text = "设置已保存并备份；返回主界面生效，独立校准需重启翻译器";
            }
            else ProgressText.Text = "已完成报告；按选项保留原设置";
        }
        catch (OperationCanceledException) { ProgressText.Text = "已取消，原设置保持不变"; }
        catch (Exception ex) { ProgressText.Text = "未应用设置"; ResultsText.AppendText("\n" + ex.Message); }
        finally { _cancel = null; cancel.Dispose(); SetBusy(false); ReloadRuns(folder); }
    }
    internal static string Describe(JsonElement report)
    {
        var text = new StringBuilder();
        text.AppendLine(report.GetProperty("eligible").GetBoolean() ? "独立样本验证通过" : "保留原设置");
        string? reason = report.GetProperty("reason").GetString();
        text.AppendLine(reason switch
        {
            "Held-out validation passed" => "获选配置通过独立样本验证，可以应用。",
            "No configuration improved the development results" => "没有配置在调参样本上达到改善标准。",
            "Winner did not improve held-out results; keep original settings" => "调参选出的配置未在独立样本上达到改善标准，不自动更换。",
            _ => reason
        });
        if (report.TryGetProperty("baseline_holdout", out var old) && report.TryGetProperty("winner_holdout", out var next))
        {
            text.AppendLine($"验证集字符错误率：{old.GetProperty("cer").GetDouble():P2} → {next.GetProperty("cer").GetDouble():P2}");
            text.AppendLine($"验证集 p95 识别耗时：{old.GetProperty("p95_s").GetDouble():F3}s → {next.GetProperty("p95_s").GetDouble():F3}s");
            text.AppendLine($"验证集实时率：{old.GetProperty("rtf").GetDouble():F3} → {next.GetProperty("rtf").GetDouble():F3}");
            text.AppendLine("验证候选：" + report.GetProperty("recommendation").GetProperty("name").GetString());
        }
        text.AppendLine("仅衡量识别 worker；不代表中文翻译质量、完整字幕延迟或游戏帧率表现。");
        return text.ToString();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancel?.Cancel();
    private void ReloadRuns(string? select = null)
    {
        if (!Directory.Exists(AsrCalibrationSettings.Root)) return;
        var folders = Directory.GetDirectories(AsrCalibrationSettings.Root).OrderDescending().ToList();
        RunsBox.ItemsSource = folders; RunsBox.SelectedItem = select is not null && folders.Contains(select) ? select : folders.FirstOrDefault();
    }
    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (RunsBox.SelectedItem is not string folder) return;
        try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_running || RunsBox.SelectedItem is not string folder) return;
        try { AsrCalibrationSettings.Restore(_active, folder); ProgressText.Text = "已恢复该次校准前的识别设置"; LoadCandidates(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_running) return;
        e.Cancel = true; _cancel?.Cancel(); ProgressText.Text = "正在取消，请等待校准进程退出后关闭";
    }
    private sealed class ProcessCleanup(Process process) : IDisposable
    {
        public void Dispose() { try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); } } catch { } }
    }
}
