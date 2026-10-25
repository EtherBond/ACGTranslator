using System.Threading.Channels;
using ACGTranslator.Models;

namespace ACGTranslator.Services;

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
    private readonly Channel<(SpeechSegmentEventArgs Segment, SessionInfo Session, Guid DiagnosticId)> _segments;
    private readonly Channel<(SubtitleEntry Entry, Guid DiagnosticId)> _recognized = Channel.CreateBounded<(SubtitleEntry, Guid)>(
        new BoundedChannelOptions(DefaultBacklog) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _recognizing, _translating;
    private Guid? _translationSession;
    private int _dropped;
    private readonly Guid _diagnosticOwner = Guid.NewGuid();

    public SubtitleProcessor(ISpeechRecognizer recognizer, ITextTranslator translator, int backlog = DefaultBacklog, PipelineDiagnostics? diagnostics = null)
    {
        Diagnostics = diagnostics ?? new PipelineDiagnostics();
        _recognizer = recognizer;
        _translator = translator;
        _segments = Channel.CreateBounded<(SpeechSegmentEventArgs, SessionInfo, Guid)>(
            new BoundedChannelOptions(backlog) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            item => { Interlocked.Increment(ref _dropped); Diagnostics.MarkOutcome(item.Item3, "dropped"); });
        _recognizing = Task.Run(RecognizeLoopAsync);
        _translating = Task.Run(TranslateLoopAsync);
    }

    public PipelineDiagnostics Diagnostics { get; }
    public int Dropped => Volatile.Read(ref _dropped);
    // Recognized text is available; the Chinese line is still pending.
    public event EventHandler<SubtitleEventArgs>? Recognized;
    public event EventHandler<SubtitleEventArgs>? Completed;
    public event EventHandler<ProcessingErrorEventArgs>? Failed;

    public void Enqueue(SpeechSegmentEventArgs segment, SessionInfo session)
    {
        var id = Diagnostics.Enqueue(segment, _diagnosticOwner);
        if (!_segments.Writer.TryWrite((segment, session, id))) Diagnostics.MarkOutcome(id, "rejected");
    }

    private async Task RecognizeLoopAsync()
    {
        try
        {
        await foreach (var (segment, session, diagnosticId) in _segments.Reader.ReadAllAsync(_stop.Token))
        {
            try
            {
                Diagnostics.MarkAsrStart(diagnosticId);
                var result = await _recognizer.RecognizeAsync(segment.Samples, _stop.Token);
                Diagnostics.MarkAsrEnd(diagnosticId);
                if (result.Warning is not null) Failed?.Invoke(this, new(result.Warning));
                var results = result.Segments.Count > 0 ? result.Segments : new[] { result };
                Guid? turnGroup = result.Segments.Count > 1 ? Guid.NewGuid() : null;
                var emitted = false;
                foreach (var part in results)
                {
                    if (string.IsNullOrWhiteSpace(part.Text)) continue;
                    var entry = new SubtitleEntry
                    {
                        TurnGroupId = turnGroup,
                        SessionId = session.Id,
                        SessionName = session.Name,
                        Timestamp = DateTimeOffset.Now,
                        Start = segment.Start + TimeSpan.FromSeconds(part.StartSeconds),
                        End = segment.Start + TimeSpan.FromSeconds(Math.Max(part.EndSeconds, part.StartSeconds + 0.01)),
                        SourceLanguage = part.Language,
                        SourceText = part.Text,
                        Status = "识别完成"
                    };
                    emitted = true;
                    Diagnostics.AttachEntry(diagnosticId, entry.Id);
                    Recognized?.Invoke(this, new(entry));
                    await _recognized.Writer.WriteAsync((entry, diagnosticId), _stop.Token);
                }
                if (!emitted) Diagnostics.MarkOutcome(diagnosticId, "empty");
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { Diagnostics.MarkOutcome(diagnosticId, "cancelled"); break; }
            catch (Exception ex) { Diagnostics.MarkAsrEnd(diagnosticId); Diagnostics.MarkOutcome(diagnosticId, "asr_failed"); Failed?.Invoke(this, new($"识别失败：{ex.Message}")); }
        }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { _recognized.Writer.TryComplete(); }
    }

    private async Task TranslateLoopAsync()
    {
        try
        {
        await foreach (var (entry, diagnosticId) in _recognized.Reader.ReadAllAsync(_stop.Token))
        {
            try
            {
                Diagnostics.MarkTranslationStart(entry.Id);
                if (_translationSession != entry.SessionId)
                {
                    if (_translator is ITranslationContext context)
                        await context.ResetContextAsync(_stop.Token);
                    _translationSession = entry.SessionId;
                }
                if (IsChinese(entry.SourceLanguage)) entry.ChineseText = entry.SourceText;
                else if (IsTranslatable(entry.SourceLanguage)) entry.ChineseText = await _translator.TranslateAsync(entry.SourceText, entry.SourceLanguage, _stop.Token);
                entry.Status = string.IsNullOrWhiteSpace(entry.ChineseText) ? "未翻译" : "完成";
                Diagnostics.MarkTranslationEnd(entry.Id);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { Diagnostics.MarkOutcome(entry.Id, "cancelled"); break; }
            catch (Exception ex)
            {
                Diagnostics.MarkTranslationEnd(entry.Id); Diagnostics.MarkOutcome(entry.Id, "translation_failed");
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
        Diagnostics.CancelOutstanding(_diagnosticOwner);
        _stop.Dispose();
    }
}

public sealed record SessionInfo(Guid Id, string Name);
