namespace OfflineSubtitleTranslator.Models;

public sealed class AsrResult
{
    public string Language { get; init; } = "unknown";
    public string Text { get; init; } = string.Empty;
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
}
