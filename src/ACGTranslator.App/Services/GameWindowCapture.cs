using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ACGTranslator.Services;

public sealed record GameWindow(nint Handle, string Title)
{
    public override string ToString() => Title;
}

public static class GameWindowCapture
{
    private delegate bool WindowCallback(nint handle, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint handle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint GetDC(nint handle);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint handle, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);

    public static List<GameWindow> List()
    {
        var windows = new List<GameWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var text = new StringBuilder(512); GetWindowText(handle, text, text.Capacity);
            if (text.Length > 0) windows.Add(new(handle, text.ToString()));
            return true;
        }, 0);
        return windows;
    }
    public static BitmapSource Capture(nint handle)
    {
        if (IsIconic(handle) || GetForegroundWindow() != handle) throw new InvalidOperationException("游戏不在前台，画面采集暂停；请返回游戏");
        nint previous = SetThreadDpiAwarenessContext(-4), screen = 0, memory = 0, bitmap = 0, old = 0;
        try
        {
            if (!GetClientRect(handle, out var rect)) throw new IOException("无法读取游戏窗口");
            var point = new NativePoint();
            if (!ClientToScreen(handle, ref point)) throw new IOException("无法定位游戏窗口");
            int width = rect.Right, height = rect.Bottom;
            if (width < 32 || height < 32 || width > 8192 || height > 8192) throw new IOException("游戏窗口尺寸不受支持");
            screen = GetDC(0); memory = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, width, height);
            if (screen == 0 || memory == 0 || bitmap == 0) throw new IOException("无法创建截图缓冲区");
            old = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, point.X, point.Y, 0x00CC0020)) throw new IOException("无法截取游戏，请尝试窗口化或无边框模式");
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); return image;
        }
        finally
        {
            if (old != 0) SelectObject(memory, old);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }
    }
    public static void Save(BitmapSource source, CaptureRegion region, string path)
    {
        if (!region.Fits(source.PixelWidth, source.PixelHeight)) throw new InvalidDataException("框选区域超出当前游戏画面，请重新框选");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(source, new Int32Rect(region.X, region.Y, region.Width, region.Height))));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    public static string Fingerprint(BitmapSource source, IEnumerable<CaptureRegion> regions)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var region in regions)
        {
            if (!region.Fits(source.PixelWidth, source.PixelHeight)) throw new InvalidDataException("字幕区域越界");
            var crop = new CroppedBitmap(source, new Int32Rect(region.X, region.Y, region.Width, region.Height));
            int stride = (crop.PixelWidth * crop.Format.BitsPerPixel + 7) / 8;
            var bytes = new byte[stride * crop.PixelHeight]; crop.CopyPixels(bytes, stride, 0); hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
