using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ACGTranslator;
using ACGTranslator.Models;
using ACGTranslator.Services;
using NAudio.Wave;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    [STAThread]
    private static int Main(string[] args)
    {
        var options = new Dictionary<string,string>();
        for (var i=0;i<args.Length;i+=2) { if(i+1>=args.Length) return 2; options.Add(args[i],args[i+1]); }
        if (!options.ContainsKey("--root") || !options.ContainsKey("--out") || !options.ContainsKey("--manifest")) return 2;
        var root=Path.GetFullPath(options["--root"]); var output=Path.GetFullPath(options["--out"]); var manifest=Path.GetFullPath(options["--manifest"]);
        if (Directory.Exists(output)) throw new IOException("Use a fresh output directory");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SUBTITLE_ROOT",root);
        Environment.SetEnvironmentVariable("SUBTITLE_DATA_DIR",Path.Combine(output,"isolated-settings-unused"));
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        app.Startup+=async (_,_)=> { try { await Run(root,output,manifest,options.GetValueOrDefault("--translator","hymt2-1.8b")); app.Shutdown(0); }
            catch(Exception ex) { File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());Console.Error.WriteLine(ex);app.Shutdown(1); } };
        return app.Run();
    }
    private static async Task Run(string root,string output,string manifest,string translatorId)
    {
        var preparation=Stopwatch.StartNew();
        var fixtures=File.ReadLines(manifest).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>JsonDocument.Parse(x)).Select(doc=> {
            using(doc) { var e=doc.RootElement; var relative=e.GetProperty("audio").GetString()!;
                var path=Path.GetFullPath(Path.IsPathRooted(relative)?relative:Path.Combine(Path.GetDirectoryName(manifest)!,relative));
                return new Fixture(e.GetProperty("id").GetString()!,e.GetProperty("language").GetString()!,path,ReadAudio(path),Hash(path)); }
        }).ToArray();
        if(fixtures.Length<3 || fixtures.Select(f=>f.Id).Distinct().Count()!=fixtures.Length) throw new InvalidDataException("Need >=3 unique fixed fixtures");
        var catalog=ModelCatalog.LoadDefault();var store=new ModelStore(catalog,AppPaths.Models);
        var gpu=HardwareInfo.DetectNvidia();
        var settings=new AppSettings { TranslatorId=translatorId,TranslatorDevice="auto" };
        var translatorPlan=EngineResolver.ResolveTranslator(settings,store,gpu);
        if(translatorPlan.Item.Id!=translatorId) throw new InvalidOperationException("Requested translator unavailable; refusing silent fallback");
        if(fixtures.Any(f=>!translatorPlan.Item.Languages.Contains(f.Language))) throw new InvalidDataException("Translator does not support fixture language");
        var asrPlan=new AsrPlan("whisper-large-v3-turbo",Path.Combine(root,"models/asr/whisper-large-v3-turbo"),"cuda",[Path.Combine(root,"models/runtimes/cuda12-cublas")]);
        var speaker=Path.Combine(root,"models/speakers/wespeaker-resnet34/speaker.onnx");
        if(!File.Exists(speaker)) throw new FileNotFoundException("Speaker model required",speaker);
        using var asr=new LocalAsrClient(AppPaths.Python(null),asrPlan,"auto",speaker,5,true,false,"float16","",false);
        using var mt=new LocalTranslationClient(AppPaths.Python(null),translatorPlan);
        var resourceHashes=Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"Resources"),"*.py").Order().ToDictionary(p=>Path.GetFileName(p)!,p=>Hash(p));
        preparation.Stop();
        var cold=Stopwatch.StartNew();await asr.WarmUpAsync();var asrLoadMs=cold.Elapsed.TotalMilliseconds;
        cold.Restart();await mt.WarmUpAsync();var mtLoadMs=cold.Elapsed.TotalMilliseconds;
        var warm=Stopwatch.StartNew();
        var warmResult=await asr.RecognizeAsync(fixtures[0].Samples);
        if(string.IsNullOrWhiteSpace(warmResult.Text)) throw new InvalidDataException("Warmup ASR empty");
        var warmText=await mt.TranslateAsync(warmResult.Text,fixtures[0].Language);
        if(string.IsNullOrWhiteSpace(warmText)) throw new InvalidDataException("Warmup MT empty");
        await mt.ResetContextAsync();warm.Stop();
        var diagnostics=new PipelineDiagnostics();var original=new TextBlock();var translated=new TextBlock();
        var runs=new List<object>();
        foreach(var f in fixtures)
        {
            var tracking=new TrackingRecognizer(asr);var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed=0;var warnings=new List<string>();var entries=new List<object>();
            await using(var processor=new SubtitleProcessor(tracking,mt,diagnostics:diagnostics))
            {
                processor.Failed+=(_,e)=> { lock(warnings) warnings.Add(e.Message); };
                processor.Recognized+=(_,e)=> Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(()=> {
                    original.Text=e.Entry.SourceText;original.UpdateLayout();diagnostics.MarkUiApplied(e.Entry.Id,false);
                }));
                processor.Completed+=(_,e)=> Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(()=> {
                    translated.Text=e.Entry.ChineseText;translated.UpdateLayout();diagnostics.MarkUiApplied(e.Entry.Id,true);
                    entries.Add(new { e.Entry.Id,e.Entry.SourceLanguage,e.Entry.SourceText,e.Entry.ChineseText,e.Entry.Status });
                    if(string.IsNullOrWhiteSpace(e.Entry.ChineseText)) done.TrySetException(new Exception("Empty/failed translation"));
                    else if(++completed==tracking.Expected) done.TrySetResult();
                }));
                var begin=Stopwatch.GetTimestamp();
                processor.Enqueue(new SpeechSegmentEventArgs(f.Samples,TimeSpan.Zero,TimeSpan.FromSeconds(f.Samples.Length/16000d)),new SessionInfo(Guid.NewGuid(),f.Id));
                var failure=await Task.WhenAny(done.Task,tracking.Empty.Task).WaitAsync(TimeSpan.FromMinutes(6));
                await failure;
                await done.Task.WaitAsync(TimeSpan.FromMinutes(6));
                runs.Add(new { f.Id,f.Language,audio=f.Path,audioSha256=f.Hash,durationSeconds=f.Samples.Length/16000d,enqueueToLastUiAppliedMs=(Stopwatch.GetTimestamp()-begin)*1000d/Stopwatch.Frequency,entries,warnings,processor.Dropped });
            }
        }
        var snapshot=diagnostics.Snapshot();
        if(snapshot.Records.Any(r=>r.EstimatedGateSpeechEnd.HasValue || r.SegmentReady.HasValue)) throw new Exception("Offline fixture incorrectly has live timestamps");
        var window=new DiagnosticsWindow(diagnostics);
        if(window.FindName("MetricsGrid") is not DataGrid grid || grid.Items.Count==0 || grid.Columns[0].Header?.ToString()!="处理阶段") throw new Exception("Diagnostics grid or Chinese labels missing");
        var content=(FrameworkElement)window.Content;content.Measure(new Size(960,580));content.Arrange(new Rect(0,0,960,580));content.UpdateLayout();
        var bitmap=new RenderTargetBitmap(960,580,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,"diagnostics.png"))) encoder.Save(file);window.Close();
        File.WriteAllText(Path.Combine(output,"diagnostics.json"),JsonSerializer.Serialize(snapshot,Json));
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new { ok=true,scope="Sequential offline recording replay through real SubtitleProcessor/ASR/speaker/local MT and hidden WPF Dispatcher TextBlock applied. Not live game capture, real-time paced playback, microphone, or screen-present latency. No speech-end clock invented. Preparation/loading/warmup excluded from per-clip metrics. Three/six fixtures are smoke evidence, not representative quality/latency statistics.",manifest,manifestSha256=Hash(manifest),translator=new {translatorPlan.Item.Id,translatorPlan.Item.Name,translatorPlan.Device,translatorPlan.Arguments},asr=new {model=asrPlan.ModelPath,device="cuda",computeType="float16",beamSize=5,presegmented=true,language="auto",speaker,adaptive=false,preserveContext=false},resourceHashes,preparationMs=preparation.Elapsed.TotalMilliseconds,asrLoadMs,mtLoadMs,warmupMs=warm.Elapsed.TotalMilliseconds,runs },Json));
        Console.WriteLine($"Latency smoke passed: {fixtures.Length} clips");
    }
    private static string Hash(string p) { using var s=File.OpenRead(p);return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
    private static float[] ReadAudio(string path)
    {
        using var wav=new WaveFileReader(path);
        if(wav.WaveFormat.SampleRate!=16000 || wav.WaveFormat.Channels!=1 || wav.WaveFormat.BitsPerSample!=16 || wav.WaveFormat.Encoding!=WaveFormatEncoding.Pcm) throw new InvalidDataException("Need 16kHz mono PCM16 WAV");
        var bytes=new byte[checked((int)wav.Length)];wav.ReadExactly(bytes);var f=new float[bytes.Length/2];for(int i=0;i<f.Length;i++)f[i]=BitConverter.ToInt16(bytes,i*2)/32768f;return f;
    }
    private sealed record Fixture(string Id,string Language,string Path,float[] Samples,string Hash);
    private sealed class TrackingRecognizer(LocalAsrClient client):ISpeechRecognizer
    {
        public int Expected;public TaskCompletionSource Empty=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AsrResult> RecognizeAsync(float[] samples,CancellationToken cancellationToken=default)
        {
            try { var r=await client.RecognizeAsync(samples,cancellationToken);Expected=(r.Segments.Count>0?r.Segments:new[]{r}).Count(x=>!string.IsNullOrWhiteSpace(x.Text));if(Expected==0)Empty.TrySetException(new Exception("Empty fixture ASR"));return r; }
            catch(Exception ex) { Empty.TrySetException(ex);throw; }
        }
    }
}
