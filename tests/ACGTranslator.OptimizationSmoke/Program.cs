using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACGTranslator;
using ACGTranslator.Models;
using ACGTranslator.Services;
using NAudio.Wave;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 3 or > 4 || (args.Length == 4 && args[3] != "--turns")) return 2;
        var verifyTurns = args.Length == 4;
        var root = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]); var audio = Path.GetFullPath(args[2]);
        if (Directory.Exists(output)) return 2;
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SUBTITLE_ROOT", root);
        Environment.SetEnvironmentVariable("SUBTITLE_DATA_DIR", Path.Combine(output, "settings"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                var settings = new AppSettings { SpeechSegmentation = true, AdaptiveAsr = true, PreserveAsrContext = true, AsrComputeType = "int8_float16", Language = "ja", AsrHotwords = "来海 天音" };
                var store = new SettingsStore(); store.Save(settings); var loaded = store.Load();
                if (!loaded.SpeechSegmentation || !loaded.AdaptiveAsr || !loaded.PreserveAsrContext || loaded.AsrComputeType != "int8_float16" || loaded.AsrHotwords != "来海 天音") throw new Exception("Settings round trip failed");
                var modelStore = new ModelStore(ModelCatalog.LoadDefault(), AppPaths.Models);
                var window = new SettingsWindow(loaded, modelStore);
                if (((CheckBox)window.FindName("SpeechSegmentationBox")).IsChecked != true ||
                    ((ComboBox)window.FindName("ComputeTypeBox")).SelectedValue as string != "int8_float16") throw new Exception("UI does not reflect settings");
                var content = (FrameworkElement)window.Content;
                ((ScrollViewer)content).Background = Brushes.White;
                content.Measure(new Size(504, 790)); content.Arrange(new Rect(0, 0, 504, 790)); content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(504, 790, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(output, "settings.png"))) encoder.Save(file);
                window.Close();
                using var reader = new WaveFileReader(audio);
                var bytes = new byte[reader.Length]; reader.ReadExactly(bytes);
                var samples = new float[bytes.Length / 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i*2) / 32768f;
                var plan = new AsrPlan("smoke", Path.Combine(root,"models","asr","whisper-large-v3-turbo"),"cuda", [Path.Combine(root,"models","runtimes","cuda12-cublas")]);
                using var client = new LocalAsrClient(AppPaths.Python(null), plan, "ja",
                    verifyTurns ? Path.Combine(root,"models","speakers","wespeaker-resnet34","speaker.onnx") : null,
                    5, true, !verifyTurns, verifyTurns ? "default" : "int8_float16", verifyTurns ? "" : loaded.AsrHotwords, true);
                await client.WarmUpAsync(); var result = await client.RecognizeAsync(samples);
                if (string.IsNullOrWhiteSpace(result.Text)) throw new Exception("Real worker returned empty text on labelled speech");
                if (verifyTurns && result.Segments.Count == 0) throw new Exception("Turn fixture did not exercise segment parsing");
                foreach (var segment in result.Segments)
                    if (segment.StartSeconds < 0 || segment.EndSeconds < segment.StartSeconds || segment.EndSeconds > samples.Length/16000.0)
                        throw new Exception("Turn segment time outside input audio");
                var silent = await client.RecognizeAsync(new float[48000]);
                if (!string.IsNullOrWhiteSpace(silent.Text)) throw new Exception("Silence produced hallucinated text");
                File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new { ok=true, result.Text, result.TurnStatus, segmentCount=result.Segments.Count, verifyTurns,
                    scope="Hidden WPF render, isolated settings roundtrip and actual C# client to CUDA worker. No live capture or translation." }));
                Console.WriteLine("Optimization smoke passed"); app.Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
        };
        return app.Run();
    }
}
