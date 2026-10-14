using System.Runtime.InteropServices;
using ACGTranslator.Models;

namespace ACGTranslator.Services;

public sealed class LocalAsrClient(string python, AsrPlan plan, string language, string? turnModel = null) : WorkerProcess(python), ISpeechRecognizer
{
    private readonly string _language = language is "ja" or "en" ? language : "auto";

    public AsrPlan Plan { get; } = plan;
    protected override string Name => "本地识别进程";
    protected override string ScriptName => "asr_worker.py";
    protected override IEnumerable<string> Arguments =>
        ["--model", Plan.ModelPath, "--device", Plan.Device, .. Plan.DllDirectories.SelectMany(d => new[] { "--dll-dir", d }),
         .. (turnModel is null ? Array.Empty<string>() : new[] { "--turn-model", turnModel })];

    protected override void Validate()
    {
        if (!File.Exists(Path.Combine(Plan.ModelPath, "model.bin"))) throw new DirectoryNotFoundException($"识别模型不完整：{Plan.ModelPath}");
    }

    public async Task<AsrResult> RecognizeAsync(float[] samples, CancellationToken cancellationToken = default)
    {
        if (samples.Length == 0) return new AsrResult();
        var audio = Convert.ToBase64String(MemoryMarshal.AsBytes(samples.AsSpan()));
        var root = await SendAsync(new { type = "transcribe", sample_rate = VadSegmenter.SampleRate, audio_base64 = audio, language = _language }, cancellationToken);
        return ParseResult(root, samples.Length / (double)VadSegmenter.SampleRate);
    }

    internal static AsrResult ParseResult(System.Text.Json.JsonElement root, double duration)
    {
        return new AsrResult
        {
            Segments = root.TryGetProperty("segments", out var segments) && segments.ValueKind == System.Text.Json.JsonValueKind.Array
                ? segments.EnumerateArray().Select(s => ParseResult(s, duration)).ToArray() : [],
            Warning = root.TryGetProperty("warning", out var warning) ? warning.GetString() : null,
            TurnStatus = root.TryGetProperty("turn_status", out var status) ? status.GetString() : null,
            Language = root.TryGetProperty("language", out var detected) ? detected.GetString() ?? "unknown" : "unknown",
            Text = root.TryGetProperty("text", out var text) ? text.GetString()?.Trim() ?? string.Empty : string.Empty,
            StartSeconds = root.TryGetProperty("start", out var start) ? start.GetDouble() : 0,
            EndSeconds = root.TryGetProperty("end", out var end) ? end.GetDouble() : duration
        };
    }
}
