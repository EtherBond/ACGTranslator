namespace OfflineSubtitleTranslator.Models;
public sealed class AppSettings
{
    public bool ShowSource { get; set; } = true;
    public bool IsAlwaysOnTop { get; set; } = true;
    public double OverlayOpacity { get; set; } = .92;
    public double FontSize { get; set; } = 26;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public string OutputDeviceId { get; set; } = "";
    // "auto", "ja" or "en". A fixed language skips detection and avoids misdetected segments.
    public string Language { get; set; } = "auto";
    public int MaxSegmentMs { get; set; } = 6000;
    // Catalog ids; empty or not installed means the best installed model is used.
    public string AsrModelId { get; set; } = "";
    public string TranslatorId { get; set; } = "";
    // "auto" uses the GPU when the matching runtime is installed; "cpu" forces CPU.
    public string AsrDevice { get; set; } = "auto";
    public string TranslatorDevice { get; set; } = "auto";
    // Advanced overrides.
    public string PythonExecutable { get; set; } = "";
    public string AsrModelPath { get; set; } = "";
    public bool SaveHistory { get; set; } = true;
}
