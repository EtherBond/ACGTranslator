using System.Threading.Channels;
using OfflineSubtitleTranslator.Models;

namespace OfflineSubtitleTranslator.Services;

public sealed class SubtitleEventArgs(SubtitleEntry entry) : EventArgs
{
    public SubtitleEntry Entry { get; } = entry;
}

public sealed class ProcessingErrorEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

// Two sequential stages connected by queues: recognition of the next segment overlaps translation
// of the previous one, and subtitles always come out in speech order. When recognition falls
// behind, the oldest waiting segment is dropped so the overlay stays close to real time.
public sealed class SubtitleProcessor : IAsyncDisposable
{
    public const int DefaultBacklog = 3;
    private readonly ISpeechRecognizer _recognizer;
    private readonly ITextTranslator _translator;
    private readonly Channel<(SpeechSegmentEventArgs Segment, SessionInfo Session)> _segments;
    private readonly Channel<SubtitleEntry> _recognized = Channel.CreateBounded<SubtitleEntry>(
        new BoundedChannelOptions(DefaultBacklog) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _recognizing, _translating;
    private int _dropped;

    public SubtitleProcessor(ISpeechRecognizer recognizer, ITextTranslator translator, int backlog = DefaultBacklog)
    {
        _recognizer = recognizer;
        _translator = translator;
        _segments = Channel.CreateBounded<(SpeechSegmentEventArgs, SessionInfo)>(
            new BoundedChannelOptions(backlog) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _dropped));
        _recognizing = Task.Run(RecognizeLoopAsync);
        _translating = Task.Run(TranslateLoopAsync);
    }

    public int Dropped => Volatile.Read(ref _dropped);
    // Recognized text is available; the Chinese line is still pending.
    public event EventHandler<SubtitleEventArgs>? Recognized;
    public event EventHandler<SubtitleEventArgs>? Completed;
    public event EventHandler<ProcessingErrorEventArgs>? Failed;

    public void Enqueue(SpeechSegmentEventArgs segment, SessionInfo session) => _segments.Writer.TryWrite((segment, session));

    private async Task RecognizeLoopAsync()
    {
        try
        {
        await foreach (var (segment, session) in _segments.Reader.ReadAllAsync(_stop.Token))
        {
            try
            {
                var result = await _recognizer.RecognizeAsync(segment.Samples, _stop.Token);
                if (string.IsNullOrWhiteSpace(result.Text)) continue;
                var entry = new SubtitleEntry
                {
                    SessionId = session.Id,
                    SessionName = session.Name,
                    Timestamp = DateTimeOffset.Now,
                    Start = segment.Start + TimeSpan.FromSeconds(result.StartSeconds),
                    End = segment.Start + TimeSpan.FromSeconds(Math.Max(result.EndSeconds, result.StartSeconds + 0.5)),
                    SourceLanguage = result.Language,
                    SourceText = result.Text,
                    Status = "识别完成"
                };
                Recognized?.Invoke(this, new(entry));
                await _recognized.Writer.WriteAsync(entry, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Failed?.Invoke(this, new($"识别失败：{ex.Message}")); }
        }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { _recognized.Writer.TryComplete(); }
    }

    private async Task TranslateLoopAsync()
    {
        try
        {
        await foreach (var entry in _recognized.Reader.ReadAllAsync(_stop.Token))
        {
            try
            {
                if (IsChinese(entry.SourceLanguage)) entry.ChineseText = entry.SourceText;
                else if (IsTranslatable(entry.SourceLanguage)) entry.ChineseText = await _translator.TranslateAsync(entry.SourceText, entry.SourceLanguage, _stop.Token);
                entry.Status = string.IsNullOrWhiteSpace(entry.ChineseText) ? "未翻译" : "完成";
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                entry.Status = "翻译失败";
                Failed?.Invoke(this, new($"翻译失败：{ex.Message}"));
            }
            Completed?.Invoke(this, new(entry));
        }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public static bool IsTranslatable(string language) => language.StartsWith("ja", StringComparison.OrdinalIgnoreCase) || language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
    private static bool IsChinese(string language) => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    // Lets queued segments finish, up to the timeout, then cancels whatever is still running.
    public async ValueTask DisposeAsync()
    {
        _segments.Writer.TryComplete();
        // ConfigureAwait(false): the window may block on this during shutdown.
        try { await Task.WhenAll(_recognizing, _translating).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        _stop.Cancel();
        try { await Task.WhenAll(_recognizing, _translating).ConfigureAwait(false); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}

public sealed record SessionInfo(Guid Id, string Name);
