namespace ACGTranslator.Models;
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
    // 0 keeps the device default; calibration may choose 1, 2 or 5.
    public int AsrBeamSize { get; set; } = 0;
    // Opt-in until independently evaluated on more recordings.
    public bool SpeechSegmentation { get; set; }
    public bool AdaptiveAsr { get; set; }
    public bool PreserveAsrContext { get; set; }
    public string AsrComputeType { get; set; } = "default";
    public string AsrHotwords { get; set; } = "";
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
    // Local, ephemeral comparison; no persistent speaker identity.
    public bool TurnEnhancement { get; set; } = true;
    public bool SaveHistory { get; set; } = true;
}
