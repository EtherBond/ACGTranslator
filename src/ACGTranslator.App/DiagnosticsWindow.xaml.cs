using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ACGTranslator.Services;
using Microsoft.Win32;

namespace ACGTranslator;

public partial class DiagnosticsWindow : Window
{
    private readonly PipelineDiagnostics _diagnostics;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private static readonly (string Key, string Label)[] Stages =
    [
        ("estimated_gate_end_to_segment_ready", "估计语音结束 → 分段就绪"),
        ("enqueue_to_asr_start", "识别排队"),
        ("asr", "识别（包含声线分析）"),
        ("translation_queue", "翻译排队（含上游背压）"),
        ("translation", "翻译（包含会话上下文重置）"),
        ("recognized_dispatch", "识别完成 → 原文控件更新"),
        ("translation_dispatch", "翻译完成 → 中文控件更新"),
        ("enqueue_to_recognized_ui_applied", "片段入队 → 原文控件更新"),
        ("enqueue_to_translation_ui_applied", "片段入队 → 中文控件更新"),
        ("estimated_gate_end_to_recognized_ui_applied", "估计语音结束 → 原文控件更新"),
        ("estimated_gate_end_to_translation_ui_applied", "估计语音结束 → 中文控件更新")
    ];
    private static readonly Dictionary<string, string> Outcomes = new()
    {
        ["queued"] = "等待识别", ["recognizing"] = "识别中", ["recognized"] = "识别成功",
        ["empty"] = "无有效文本", ["dropped"] = "积压丢弃", ["asr_failed"] = "识别失败",
        ["cancelled"] = "已取消", ["rejected"] = "停止后拒收"
    };

    public DiagnosticsWindow(PipelineDiagnostics diagnostics)
    {
        InitializeComponent();
        _diagnostics = diagnostics;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var snapshot = _diagnostics.Snapshot();
        CountText.Text = $"保留记录 {snapshot.Records.Count} / 512 · 片段状态仅统计保留记录 · 启停识别不会自动清空";
        OutcomeText.Text = snapshot.Outcomes.Count == 0 ? "等待开始识别。" : string.Join(" · ",
            snapshot.Outcomes.Select(pair => $"{Outcomes.GetValueOrDefault(pair.Key, pair.Key)} {pair.Value}"));
        var translations = snapshot.Records.Where(record => record.SegmentId != null).ToArray();
        if (translations.Length > 0)
            OutcomeText.Text += $"\n翻译阶段完成（含跳过） {translations.Count(record => record.Outcome == "completed")} · 翻译失败 {translations.Count(record => record.Outcome == "translation_failed")} · 翻译取消 {translations.Count(record => record.Outcome == "cancelled")}";
        MetricsGrid.ItemsSource = Stages.Select(stage =>
        {
            snapshot.Metrics.TryGetValue(stage.Key, out var metric);
            string Format(double? value) => metric?.Count > 0 && value is { } v
                ? v.ToString("F1", CultureInfo.CurrentCulture) : "—";
            return new { stage.Label, Count = metric?.Count ?? 0, Mean = Format(metric?.MeanMs),
                P50 = Format(metric?.P50Ms), P95 = Format(metric?.P95Ms), Max = Format(metric?.MaxMs) };
        }).ToArray();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "JSON 文件|*.json", FileName = $"acgtranslator-latency-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var snapshot = _diagnostics.Snapshot();
            var json = JsonSerializer.Serialize(new { Schema = 1, ExportedAt = DateTimeOffset.Now,
                AppVersion = typeof(DiagnosticsWindow).Assembly.GetName().Version?.ToString(), Snapshot = snapshot },
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(dialog.FileName, json, new UTF8Encoding(false));
            MessageText.Text = "统计已导出。导出的文件可独立管理。";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _diagnostics.Clear();
        Refresh();
        MessageText.Text = "当前统计已清空。已导出的文件不受影响。";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
