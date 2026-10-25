using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using ACGTranslator.Services;

if (args.Length != 3) throw new ArgumentException("Usage: recording.wav out-directory silero_vad_v6.onnx");
if (Directory.Exists(args[1])) throw new IOException("Output must be a new directory.");
Directory.CreateDirectory(args[1]);
var results = new List<object>();
foreach (var mode in new[] { "legacy", "speech" })
{
    var folder = Path.Combine(args[1], mode);
    Directory.CreateDirectory(folder);
    using var manifest = new StreamWriter(Path.Combine(folder, "segments.jsonl"));
    using var reader = new WaveFileReader(args[0]);
    if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1 || reader.WaveFormat.BitsPerSample != 16)
        throw new InvalidDataException("Expected 16 kHz mono PCM16.");
    var energy = new VadSegmenter();
    using var gate = mode == "speech" ? new SileroSpeechGate(args[2]) : null;
    using var speech = gate is null ? null : new StreamingSpeechSegmenter(gate.Probability);
    int count = 0;
    double frontier = 0, emittedSeconds = 0;
    void Write(object? sender, SpeechSegmentEventArgs e)
    {
        var name = $"{++count:000000}.wav";
        using var writer = new WaveFileWriter(Path.Combine(folder, name), new WaveFormat(16000, 16, 1));
        var bytes = new byte[e.Samples.Length * 2];
        for (int i = 0; i < e.Samples.Length; i++)
        {
            var pcm = (short)Math.Clamp(Math.Round(e.Samples[i] * 32768), -32768, 32767);
            bytes[i*2] = (byte)(pcm & 255); bytes[i*2+1] = (byte)((pcm >> 8) & 255);
        }
        writer.Write(bytes, 0, bytes.Length);
        emittedSeconds += e.Samples.Length / 16000d;
        manifest.WriteLine(JsonSerializer.Serialize(new { id = $"{mode}-{count}", audio = name,
            start_s = e.Start.TotalSeconds, end_s = e.End.TotalSeconds, emitted_at_s = frontier,
            duration_s = e.Samples.Length / 16000d }));
    }
    energy.SegmentReady += Write;
    if (speech is not null) speech.SegmentReady += Write;
    var buffer = new byte[640];
    int read;
    var timer = Stopwatch.StartNew();
    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
    {
        var samples = new float[read / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(buffer, 2 * i) / 32768f;
        frontier += samples.Length / 16000d;
        if (speech is null) energy.Push(samples); else speech.Push(samples);
    }
    if (speech is null) energy.Flush(); else speech.Flush();
    timer.Stop();
    results.Add(new { mode, count, source_duration_s = frontier, emitted_audio_s = emittedSeconds, processing_s = timer.Elapsed.TotalSeconds });
    Console.WriteLine(JsonSerializer.Serialize(results.Last()));
}
File.WriteAllText(Path.Combine(args[1], "summary.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
