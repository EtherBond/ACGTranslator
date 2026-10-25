using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACGTranslator;
using ACGTranslator.Services;
using NAudio.Wave;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--package-session")
        {
            string source = Path.GetFullPath(args[1]);
            var existing = JsonSerializer.Deserialize<CaptureSession>(File.ReadAllText(Path.Combine(source, "session.json")))!;
            Console.WriteLine(CaptureHandoff.Export(existing, source)); return 0;
        }
        if (args.Length != 1) { Console.Error.WriteLine("Pass an empty output directory."); return 2; }
        string output = Path.GetFullPath(args[0]);
        if (Directory.Exists(output)) { Console.Error.WriteLine("Output already exists."); return 2; }
        Directory.CreateDirectory(output);
        // Isolate all generated session data from the user's real captures.
        Environment.SetEnvironmentVariable("SUBTITLE_ROOT", output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                string ja = Path.Combine(output, "ja.png"), zh = Path.Combine(output, "zh.png");
                TextImage(ja, "これは音声認識のテストです。", "Yu Gothic UI");
                TextImage(zh, "这是语音识别测试。", "Microsoft YaHei UI");
                using var ocr = new GameOcrClient();
                var text = await ocr.ReadAsync(ja, zh, null, CancellationToken.None);
                if (!text.Japanese.Contains("テスト") || !text.Chinese.Contains("测试")) throw new Exception("OCR did not read both fixture languages: " + JsonSerializer.Serialize(text));
                var session = new CaptureSession { Game = "工具验证（人工绘制文字，非游戏数据）", State = "待复核", DurationSeconds = 3 };
                CaptureStore.Save(session);
                string folder = CaptureStore.Folder(session.Id);
                File.Copy(ja, Path.Combine(folder, "evidence.png"));
                using (var wave = new WaveFileWriter(Path.Combine(folder, "capture.wav"), new WaveFormat(16000, 16, 1)))
                    wave.WriteSamples(new float[48000], 0, 48000);
                session.Entries.Add(new CaptureEntry { Japanese = text.Japanese, Chinese = text.Chinese, Speaker = "测试角色", StartSeconds = .2, EndSeconds = 2.5,
                    VisualStart = .45, VisualEnd = 2.35, Screenshot = "evidence.png", Review = "已确认", Note = "仅验证导出管线；音频为合成静音，不用于准确率测试" });
                CaptureStore.Save(session);
                string manifest = CaptureAudioExport.Export(session);
                session.Entries.Add(new CaptureEntry { Japanese = "未确认候选", StartSeconds = 0, EndSeconds = .1, Review = "待复核" });
                session.Entries.Add(new CaptureEntry { Japanese = "过去排除的候选", Review = "已排除" });
                CaptureStore.Save(session);
                string handoff = CaptureHandoff.Export(session);
                if (File.ReadAllLines(Path.Combine(handoff, "candidates.jsonl")).Length != 3) throw new Exception("Handoff dropped unconfirmed/excluded candidates.");
                // Hidden render only: do not activate a test window over the user's desktop.
                var window = new CaptureWindow();
                ((DataGrid)window.FindName("EntriesGrid")).SelectedIndex = 0;
                var selected = (CaptureEntry)((DataGrid)window.FindName("EntriesGrid")).SelectedItem;
                ((TextBox)window.FindName("BeginText")).Text = "0.300";
                if (selected.Review != "待复核" || ((TextBox)window.FindName("BeginText")).Text != "0.300")
                    throw new Exception("Editing a confirmed row must invalidate approval without discarding edits.");
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1208, 800)); content.Arrange(new Rect(0, 0, 1208, 800)); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1240, 832, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(output, "capture-ui.png"))) encoder.Save(stream);
                window.Close();
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { ok = true, text, manifest, handoff, session.Id,
                    scope = "Real Windows OCR on artificial text, hidden WPF render, persistence and WAV export. No live game/loopback validation." }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("Capture smoke passed: " + output); app.Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); Console.Error.WriteLine(ex); app.Shutdown(1); }
        };
        return app.Run();
    }
    private static void TextImage(string path, string text, string font)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1200, 130));
            drawing.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal), 45, Brushes.Black, 1), new Point(25, 30));
        }
        var bitmap = new RenderTargetBitmap(1200, 130, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
