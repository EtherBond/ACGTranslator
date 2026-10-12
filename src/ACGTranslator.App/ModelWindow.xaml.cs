using System.Collections.ObjectModel;
using System.Windows;
using ACGTranslator.Models;
using ACGTranslator.Services;

namespace ACGTranslator;

public partial class ModelWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ModelStore _store;
    private readonly ModelDownloader _downloader;
    private readonly ObservableCollection<ModelRow> _rows = [];

    public ModelWindow(AppSettings settings, ModelStore store)
    {
        InitializeComponent();
        _settings = settings;
        _store = store;
        _downloader = new ModelDownloader(store);
        ModelsList.ItemsSource = _rows;
        Refresh();
    }

    private void Refresh()
    {
        _rows.Clear();
        foreach (var item in _store.Catalog.Items.Where(item => item.Kind is CatalogKind.Asr or CatalogKind.Translator or CatalogKind.Runtime))
        {
            bool installed;
            try { installed = _store.IsInstalled(item); }
            catch { installed = false; }
            var selected = item.Kind == CatalogKind.Asr && _settings.AsrModelId == item.Id ||
                           item.Kind == CatalogKind.Translator && _settings.TranslatorId == item.Id;
            _rows.Add(new ModelRow(item, installed, selected));
        }
        StatusText.Text = $"已安装 {_rows.Count(row => row.Installed)} / {_rows.Count} 个模型";
    }

    private ModelRow? Selected => ModelsList.SelectedItem as ModelRow;

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Refresh();

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Installed: true } row)
        {
            MessageBox.Show(this, "请先选择已安装的模型。", "模型管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (row.Item.Kind == CatalogKind.Asr) _settings.AsrModelId = row.Item.Id;
        else if (row.Item.Kind == CatalogKind.Translator) _settings.TranslatorId = row.Item.Id;
        else
        {
            StatusText.Text = "运行库安装后会由引擎自动使用，无需单独选择";
            return;
        }
        Refresh();
        StatusText.Text = $"已选择：{row.Name}（关闭后点击设置窗口的保存）";
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        ModelsList.IsEnabled = false;
        try
        {
            var progress = new Progress<DownloadProgress>(value =>
                StatusText.Text = value.Total > 0
                    ? $"{value.Stage}：{value.Received / 1048576d:0.0} / {value.Total / 1048576d:0.0} MB"
                    : value.Stage);
            await _downloader.InstallAsync(row.Item, progress);
            Refresh();
            StatusText.Text = $"已安装：{row.Name}";
        }
        catch (OperationCanceledException) { StatusText.Text = "下载已取消"; }
        catch (Exception ex) { StatusText.Text = "下载失败"; MessageBox.Show(this, ex.Message, "模型下载失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { ModelsList.IsEnabled = true; }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Installed: true } row) return;
        if (MessageBox.Show(this, $"确定删除“{row.Name}”吗？", "删除模型", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { _store.Delete(row.Item); Refresh(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class ModelRow(CatalogItem item, bool installed, bool selected)
    {
        public CatalogItem Item { get; } = item;
        public bool Installed { get; } = installed;
        public string Status => Installed ? (selected ? "当前" : "已安装") : "未安装";
        public string KindName => Item.Kind switch { CatalogKind.Asr => "识别", CatalogKind.Translator => "翻译", _ => "运行库" };
        public string Name => Item.Name;
        public string SizeText => Item.TotalSize >= 1073741824 ? $"{Item.TotalSize / 1073741824d:0.0} GB" : $"{Item.TotalSize / 1048576d:0} MB";
        public string Description => Item.Description;
    }
}
