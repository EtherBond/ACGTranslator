using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACGTranslator;
using ACGTranslator.Models;
using ACGTranslator.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Pass a new output directory and an installed Whisper model directory."); return 2; }
        string output = Path.GetFullPath(args[0]), model = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) || !File.Exists(Path.Combine(model, "model.bin"))) return 2;
        Directory.CreateDirectory(output);
        // Set isolation before touching App or AppPaths; never load/save the user's settings.
        Environment.SetEnvironmentVariable("SUBTITLE_ROOT", output);
        Environment.SetEnvironmentVariable("SUBTITLE_DATA_DIR", Path.Combine(output, "settings"));
        Environment.SetEnvironmentVariable("SUBTITLE_MODELS_DIR", Path.Combine(output, "empty-model-store"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            try
            {
                var original = new AppSettings { AsrModelPath = model, AsrDevice = "cpu", Language = "ja", FontSize = 31 };
                App.Settings.Save(original);
                string run = Path.Combine(AsrCalibrationSettings.Root, "isolated-smoke"); Directory.CreateDirectory(run);
                File.WriteAllText(Path.Combine(run, "settings-before.json"), JsonSerializer.Serialize(original));
                using var candidate = JsonDocument.Parse(JsonSerializer.Serialize(new { model, device = "cpu", beam = 1 }));
                var active = App.Settings.Load();
                // A concurrent unrelated edit must survive calibration and restoration.
                var unrelated = App.Settings.Load(); unrelated.FontSize = 33; App.Settings.Save(unrelated);
                AsrCalibrationSettings.Apply(active, original, candidate.RootElement, run);
                var saved = App.Settings.Load();
                Check(saved.AsrBeamSize == 1 && active.AsrBeamSize == 1 && saved.FontSize == 33 && saved.Language == "ja", "Apply or preservation failed");
                AsrCalibrationSettings.Restore(active, run);
                saved = App.Settings.Load();
                Check(saved.AsrBeamSize == 0 && active.AsrBeamSize == 0 && saved.FontSize == 33 && saved.Language == "ja", "Restore failed");
                saved.Language = "en"; App.Settings.Save(saved);
                bool blocked = false;
                try { AsrCalibrationSettings.Apply(active, original, candidate.RootElement, run); }
                catch (InvalidOperationException) { blocked = true; }
                Check(blocked && App.Settings.Load().Language == "en" && App.Settings.Load().AsrBeamSize == 0, "Concurrent recognition edit was overwritten");

                var window = new CalibrationWindow(App.Settings.Load());
                Check(((Button)window.FindName("RunButton")).IsEnabled, "Installed model was not discovered");
                string summary = ((TextBlock)window.FindName("CandidateText")).Text;
                Check(summary.Contains("共 2 个配置"), "Unexpected candidate list: " + summary);
                ((TextBox)window.FindName("ResultsText")).Text = "隔离验证通过：应用设置、备份恢复、并发修改保护。\n真实识别试跑结果请查看对应 report.json。";
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1004, 745)); content.Arrange(new Rect(0, 0, 1004, 745)); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1040, 781, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(output, "calibration-ui.png"))) encoder.Save(stream);
                window.Close();
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { ok = true, summary,
                    scope = "Isolated settings apply/restore/conflict guard and hidden WPF render; no user settings changes." }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("Calibration UI/settings smoke passed: " + output); app.Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); Console.Error.WriteLine(ex); app.Shutdown(1); }
        };
        return app.Run();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
