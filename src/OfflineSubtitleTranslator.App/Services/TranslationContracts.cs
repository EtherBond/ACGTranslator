using OfflineSubtitleTranslator.Models;

namespace OfflineSubtitleTranslator.Services;

public interface ISpeechRecognizer
{
    Task<AsrResult> RecognizeAsync(float[] samples, CancellationToken cancellationToken = default);
}

// The seam for a future external translation service; the default implementation is offline.
public interface ITextTranslator
{
    Task<string> TranslateAsync(string text, string sourceLanguage, CancellationToken cancellationToken = default);
}

// Context-aware engines keep a short dialogue history. Reset it whenever a new
// capture session starts so names and phrasing cannot leak between videos.
public interface ITranslationContext
{
    Task ResetContextAsync(CancellationToken cancellationToken = default);
}
