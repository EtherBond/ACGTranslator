using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using OfflineSubtitleTranslator.Models;
using OfflineSubtitleTranslator.Services;

namespace OfflineSubtitleTranslator;

public partial class HistoryWindow : Window
{
    private List<SubtitleEntry> _allEntries = [];
    private readonly ObservableCollection<SubtitleEntry> _visibleEntries = [];

    public HistoryWindow()
    {
        InitializeComponent();
        EntriesList.ItemsSource = _visibleEntries;
        Reload();
    }

    private void Reload()
    {
        _allEntries = App.History.Load().OrderByDescending(e => e.Timestamp).ToList();
        Refresh();
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (EntriesList is null) return;
        var query = SearchBox.Text.Trim();
        var filtered = query.Length == 0
            ? _allEntries
            : _allEntries.Where(e => e.SourceText.Contains(query, StringComparison.OrdinalIgnoreCase) || e.ChineseText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        _visibleEntries.Clear();
        foreach (var item in filtered) _visibleEntries.Add(item);
        CountText.Text = query.Length == 0 ? $"共 {_allEntries.Count} 条" : $"匹配 {filtered.Count} / {_allEntries.Count} 条";
    }

    private List<SubtitleEntry> Selected => EntriesList.SelectedItems.Cast<SubtitleEntry>().ToList();

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected.Count == 0) return;
        App.History.Delete(selected.Select(entry => entry.Id));
        Reload();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "确定清空全部字幕历史吗？此操作无法撤销。", "确认清空", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        App.History.Clear();
        Reload();
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        // Export the selection's sessions when something is selected, otherwise everything visible.
        var sessions = Selected.Select(entry => entry.SessionId).ToHashSet();
        var entries = sessions.Count > 0 ? _allEntries.Where(entry => sessions.Contains(entry.SessionId)).ToList() : _visibleEntries.ToList();
        if (entries.Count == 0)
        {
            MessageBox.Show(this, "没有可导出的字幕。", "导出", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog { Filter = "SubRip 字幕|*.srt", FileName = $"字幕-{DateTime.Now:yyyyMMdd-HHmmss}.srt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, SubtitleExporter.ToSrt(entries, BilingualBox.IsChecked == true), new System.Text.UTF8Encoding(true));
            MessageBox.Show(this, $"已导出 {entries.Count} 条字幕。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
