using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ACGTranslator.Services;

namespace ACGTranslator;

public sealed class RegionSelectionWindow : Window
{
    private readonly Canvas _canvas;
    private readonly Rectangle _selection = new() { Stroke = Brushes.OrangeRed, StrokeThickness = 3, Fill = new SolidColorBrush(Color.FromArgb(35, 255, 80, 0)), IsHitTestVisible = false };
    private Point _start;
    private readonly int _width, _height;
    public CaptureRegion? Region { get; private set; }
    public RegionSelectionWindow(BitmapSource image, string label)
    {
        Title = $"框选{label}区域 · 按住左键拖动，确认后保存";
        Width = 1100; Height = 760; MinWidth = 500; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _width = image.PixelWidth; _height = image.PixelHeight;
        var panel = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var ok = new Button { Content = "确认区域", IsDefault = true };
        ok.Click += (_, _) => { if (Region?.Fits(_width, _height) == true) DialogResult = true; else MessageBox.Show(this, "请拖动框选至少 8×8 像素的区域"); };
        var cancel = new Button { Content = "取消", IsCancel = true };
        actions.Children.Add(ok); actions.Children.Add(cancel);
        actions.Children.Add(new TextBlock { Text = "只框选对应语言的台词；名字建议另选区域", Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        _canvas = new Canvas { Width = _width, Height = _height, Background = Brushes.Black, ClipToBounds = true };
        _canvas.Children.Add(new Image { Source = image, Width = _width, Height = _height, IsHitTestVisible = false });
        _canvas.Children.Add(_selection);
        _canvas.MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(_canvas); Region = null; _canvas.CaptureMouse(); Draw(_start); };
        _canvas.MouseMove += (_, e) => { if (_canvas.IsMouseCaptured) Draw(e.GetPosition(_canvas)); };
        _canvas.MouseLeftButtonUp += (_, e) => { if (_canvas.IsMouseCaptured) { Draw(e.GetPosition(_canvas)); _canvas.ReleaseMouseCapture(); } };
        panel.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = _canvas });
        Content = panel;
    }
    private void Draw(Point point)
    {
        int x = (int)Math.Clamp(Math.Min(_start.X, point.X), 0, _width - 1);
        int y = (int)Math.Clamp(Math.Min(_start.Y, point.Y), 0, _height - 1);
        int right = (int)Math.Clamp(Math.Max(_start.X, point.X), x, _width);
        int bottom = (int)Math.Clamp(Math.Max(_start.Y, point.Y), y, _height);
        Region = new(x, y, right - x, bottom - y);
        Canvas.SetLeft(_selection, x); Canvas.SetTop(_selection, y); _selection.Width = Region.Width; _selection.Height = Region.Height;
    }
}
