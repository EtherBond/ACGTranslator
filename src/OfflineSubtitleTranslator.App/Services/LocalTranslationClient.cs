namespace OfflineSubtitleTranslator.Services;

public sealed class LocalTranslationClient(string python, TranslatorPlan plan) : WorkerProcess(python), ITextTranslator, ITranslationContext
{
    public TranslatorPlan Plan { get; } = plan;
    protected override string Name => "本地翻译进程";
    protected override string ScriptName => "translate_worker.py";
    protected override IEnumerable<string> Arguments => Plan.Arguments;
    // llama.cpp loads several GB into VRAM on first start.
    protected override TimeSpan StartupTimeout => TimeSpan.FromMinutes(6);

    public bool Supports(string language) => Plan.Item.Languages.Any(l => language.StartsWith(l, StringComparison.OrdinalIgnoreCase));

    public async Task<string> TranslateAsync(string text, string sourceLanguage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var source = sourceLanguage.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? "ja"
            : sourceLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en"
            : throw new InvalidOperationException($"暂不支持的翻译源语言：{sourceLanguage}");
        if (!Supports(source)) throw new InvalidOperationException($"{Plan.Item.Name} 不支持{(source == "ja" ? "日语" : "英语")}，请在设置中换一个翻译引擎");
        var root = await SendAsync(new { type = "translate", text, source, target = "zh" }, cancellationToken);
        return root.TryGetProperty("text", out var result) ? result.GetString() ?? string.Empty : string.Empty;
    }

    // Context-aware engines should not carry one session's dialogue into the next.
    public async Task ResetContextAsync(CancellationToken cancellationToken = default)
    {
        try { await SendAsync(new { type = "reset" }, cancellationToken); }
        catch (Exception) { }
    }
}
