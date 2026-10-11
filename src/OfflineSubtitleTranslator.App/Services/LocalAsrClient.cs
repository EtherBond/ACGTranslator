using System.Runtime.InteropServices;
using OfflineSubtitleTranslator.Models;

namespace OfflineSubtitleTranslator.Services;

public sealed class LocalAsrClient(string python, AsrPlan plan, string language) : WorkerProcess(python), ISpeechRecognizer
{
    private readonly string _language = language is "ja" or "en" ? language : "auto";

    public AsrPlan Plan { get; } = plan;
    protected override string Name => "本地识别进程";
    protected override string ScriptName => "asr_worker.py";
    protected override IEnumerable<string> Arguments =>
        ["--model", Plan.ModelPath, "--device", Plan.Device, .. Plan.DllDirectories.SelectMany(d => new[] { "--dll-dir", d })];

    protected override void Validate()
    {
        if (!File.Exists(Path.Combine(Plan.ModelPath, "model.bin"))) throw new DirectoryNotFoundException($"识别模型不完整：{Plan.ModelPath}");
    }

    public async Task<AsrResult> RecognizeAsync(float[] samples, CancellationToken cancellationToken = default)
    {
        if (samples.Length == 0) return new AsrResult();
        var audio = Convert.ToBase64String(MemoryMarshal.AsBytes(samples.AsSpan()));
        var root = await SendAsync(new { type = "transcribe", sample_rate = VadSegmenter.SampleRate, audio_base64 = audio, language = _language }, cancellationToken);
        return new AsrResult
        {
            Language = root.TryGetProperty("language", out var detected) ? detected.GetString() ?? "unknown" : "unknown",
            Text = root.TryGetProperty("text", out var text) ? text.GetString()?.Trim() ?? string.Empty : string.Empty,
            StartSeconds = root.TryGetProperty("start", out var start) ? start.GetDouble() : 0,
            EndSeconds = root.TryGetProperty("end", out var end) ? end.GetDouble() : samples.Length / (double)VadSegmenter.SampleRate
        };
    }
}
