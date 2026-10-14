namespace ACGTranslator.Models;

public sealed class AsrResult
{
    public IReadOnlyList<AsrResult> Segments { get; init; } = [];
    public string? Warning { get; init; }
    public string? TurnStatus { get; init; }
    public string Language { get; init; } = "unknown";
    public string Text { get; init; } = string.Empty;
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
}
