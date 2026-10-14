using System.Windows;
using Microsoft.Win32;
using ACGTranslator.Models;
using ACGTranslator.Services;

namespace ACGTranslator;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ModelStore _modelStore;

    public SettingsWindow(AppSettings settings, ModelStore modelStore)
    {
        InitializeComponent();
        _settings = settings;
        _modelStore = modelStore;
        var devices = AudioCaptureService.GetOutputDevices();
        OutputDeviceBox.ItemsSource = devices;
        OutputDeviceBox.SelectedItem = devices.FirstOrDefault(device => device.Id == settings.OutputDeviceId) ?? devices[0];
        LanguageBox.SelectedValue = settings.Language is "ja" or "en" ? settings.Language : "auto";
        DeviceBox.SelectedValue = settings.AsrDevice is "cuda" or "cpu" or "auto" ? settings.AsrDevice : "auto";
        SegmentSlider.Value = Math.Clamp(settings.MaxSegmentMs / 1000d, SegmentSlider.Minimum, SegmentSlider.Maximum);
        ShowSourceBox.IsChecked = settings.ShowSource;
        AlwaysOnTopBox.IsChecked = settings.IsAlwaysOnTop;
        SaveHistoryBox.IsChecked = settings.SaveHistory;
        TurnEnhancementBox.IsChecked = settings.TurnEnhancement;
        FontSizeSlider.Value = settings.FontSize;
        OpacitySlider.Value = settings.OverlayOpacity;
        PythonBox.Text = settings.PythonExecutable;
        ModelBox.Text = settings.AsrModelPath;
        DataPathText.Text = $"设置与历史保存在：{AppPaths.Data}";
        UpdateLabels();
        UpdateModelLabel();
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

    private void UpdateLabels()
    {
        // ValueChanged fires during InitializeComponent, before every control exists.
        if (SegmentLabel is null || FontSizeLabel is null || OpacityLabel is null) return;
        SegmentLabel.Text = $"最长片段：{SegmentSlider.Value:0} 秒（越短字幕越及时，越长断句越完整）";
        FontSizeLabel.Text = $"中文字号：{FontSizeSlider.Value:0}";
        OpacityLabel.Text = $"窗口不透明度：{OpacitySlider.Value:P0}";
    }

    private void UpdateModelLabel()
    {
        TurnModelLabel.Text = _modelStore.Installed(CatalogKind.Speaker).Any()
            ? "接话模型已安装；保存设置后生效。"
            : "接话模型未安装，当前使用普通识别。请在模型管理中下载 WeSpeaker 接话模型。";
        var asr = _modelStore.Catalog.Find(_settings.AsrModelId)?.Name ?? "自动选择已安装识别模型";
        var translator = _modelStore.Catalog.Find(_settings.TranslatorId)?.Name ?? "自动选择已安装翻译引擎";
        ModelLabel.Text = $"当前识别：{asr}\n当前翻译：{translator}";
    }

    private void ModelManager_Click(object sender, RoutedEventArgs e)
    {
        new ModelWindow(_settings, _modelStore) { Owner = this }.ShowDialog();
        UpdateModelLabel();
    }

    private void BrowsePython_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Python 解释器|python.exe|可执行文件|*.exe" };
        if (dialog.ShowDialog(this) == true) PythonBox.Text = dialog.FileName;
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择包含 model.bin 的 Whisper 模型目录" };
        if (dialog.ShowDialog(this) == true) ModelBox.Text = dialog.FolderName;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var python = PythonBox.Text.Trim();
        var model = ModelBox.Text.Trim();
        if (python.Length > 0 && !File.Exists(python))
        {
            MessageBox.Show(this, "找不到指定的 Python 解释器。", "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (model.Length > 0 && !File.Exists(Path.Combine(model, "model.bin")))
        {
            MessageBox.Show(this, "模型目录中没有 model.bin，请选择 faster-whisper 格式的模型目录。", "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.OutputDeviceId = (OutputDeviceBox.SelectedItem as AudioDeviceOption)?.Id ?? "";
        _settings.Language = LanguageBox.SelectedValue as string ?? "auto";
        _settings.AsrDevice = DeviceBox.SelectedValue as string ?? "cpu";
        _settings.MaxSegmentMs = (int)SegmentSlider.Value * 1000;
        _settings.ShowSource = ShowSourceBox.IsChecked == true;
        _settings.IsAlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _settings.SaveHistory = SaveHistoryBox.IsChecked == true;
        _settings.TurnEnhancement = TurnEnhancementBox.IsChecked == true;
        _settings.FontSize = FontSizeSlider.Value;
        _settings.OverlayOpacity = OpacitySlider.Value;
        _settings.PythonExecutable = python;
        _settings.AsrModelPath = model;
        try { App.Settings.Save(_settings); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"设置已应用，但写入文件失败：{ex.Message}", "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
