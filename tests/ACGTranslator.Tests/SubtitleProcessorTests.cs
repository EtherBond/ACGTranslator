using System.Collections.Concurrent;
using ACGTranslator.Models;
using ACGTranslator.Services;

namespace ACGTranslator.Tests;

public class SubtitleProcessorTests
{
    private static readonly SessionInfo Session = new(Guid.NewGuid(), "测试会话");

    private static SpeechSegmentEventArgs Segment(int id, double start = 0) =>
        new([id], TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(start + 1));

    private sealed class FakeRecognizer(Func<float[], Task<AsrResult>> recognize) : ISpeechRecognizer
    {
        public Task<AsrResult> RecognizeAsync(float[] samples, CancellationToken cancellationToken = default) => recognize(samples);
    }

    private class FakeTranslator(Func<string, Task<string>> translate) : ITextTranslator
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public Task<string> TranslateAsync(string text, string sourceLanguage, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(text);
            return translate(text);
        }
    }

    private sealed class ContextTranslator : FakeTranslator, ITranslationContext
    {
        public int Resets { get; private set; }
        public ContextTranslator() : base(Task.FromResult) { }
        public Task ResetContextAsync(CancellationToken cancellationToken = default)
        {
            Resets++;
            return Task.CompletedTask;
        }
    }

    private static AsrResult Result(float[] samples, string language = "ja") =>
        new() { Language = language, Text = $"text{samples[0]}", StartSeconds = 0.2, EndSeconds = 0.9 };

    [Fact]
    public async Task Produces_translated_entries_in_order_with_absolute_times()
    {
        var translator = new FakeTranslator(text => Task.FromResult("译" + text));
        var processor = new SubtitleProcessor(new FakeRecognizer(s => Task.FromResult(Result(s))), translator);
        var completed = new ConcurrentQueue<SubtitleEntry>();
        processor.Completed += (_, e) => completed.Enqueue(e.Entry);

        processor.Enqueue(Segment(1, start: 10), Session);
        processor.Enqueue(Segment(2, start: 20), Session);
        await processor.DisposeAsync();

        Assert.Equal(["译text1", "译text2"], completed.Select(e => e.ChineseText));
        var first = completed.First();
        Assert.Equal(TimeSpan.FromSeconds(10.2), first.Start);
        Assert.Equal(TimeSpan.FromSeconds(10.9), first.End);
        Assert.Equal(Session.Id, first.SessionId);
        Assert.Equal("完成", first.Status);
    }

    [Fact]
    public async Task Empty_recognition_is_skipped()
    {
        var processor = new SubtitleProcessor(new FakeRecognizer(_ => Task.FromResult(new AsrResult { Language = "en", Text = "" })), new FakeTranslator(Task.FromResult));
        var count = 0;
        processor.Completed += (_, _) => Interlocked.Increment(ref count);

        processor.Enqueue(Segment(1), Session);
        await processor.DisposeAsync();

        Assert.Equal(0, count);
        Assert.Equal("empty", Assert.Single(processor.Diagnostics.Snapshot().Records).Outcome);
    }

    [Fact]
    public async Task Untranslatable_language_is_kept_as_source_without_calling_translator()
    {
        var translator = new FakeTranslator(Task.FromResult);
        var processor = new SubtitleProcessor(new FakeRecognizer(s => Task.FromResult(Result(s, "ko"))), translator);
        SubtitleEntry? entry = null;
        processor.Completed += (_, e) => entry = e.Entry;

        processor.Enqueue(Segment(1), Session);
        await processor.DisposeAsync();

        Assert.Empty(translator.Calls);
        Assert.Equal("未翻译", entry?.Status);
    }

    [Fact]
    public async Task Translation_failure_reports_error_and_still_completes_entry()
    {
        var processor = new SubtitleProcessor(new FakeRecognizer(s => Task.FromResult(Result(s))), new FakeTranslator(_ => throw new InvalidOperationException("boom")));
        var errors = new ConcurrentQueue<string>();
        SubtitleEntry? entry = null;
        processor.Failed += (_, e) => errors.Enqueue(e.Message);
        processor.Completed += (_, e) => entry = e.Entry;

        processor.Enqueue(Segment(1), Session);
        await processor.DisposeAsync();

        Assert.Contains("boom", Assert.Single(errors));
        Assert.Equal("翻译失败", entry?.Status);
    }

    [Fact]
    public async Task Oldest_waiting_segment_is_dropped_when_recognition_falls_behind()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var recognizer = new FakeRecognizer(async s =>
        {
            if (s[0] == 1) { started.TrySetResult(); await gate.Task; }
            return Result(s);
        });
        var processor = new SubtitleProcessor(recognizer, new FakeTranslator(Task.FromResult), backlog: 2);
        var completed = new ConcurrentQueue<string>();
        processor.Completed += (_, e) => completed.Enqueue(e.Entry.SourceText);

        processor.Enqueue(Segment(1), Session);
        await started.Task;
        for (int i = 2; i <= 5; i++) processor.Enqueue(Segment(i), Session);
        gate.SetResult();
        await processor.DisposeAsync();

        Assert.Equal(2, processor.Dropped);
        Assert.Equal(2, processor.Diagnostics.Snapshot().Outcomes["dropped"]);
        Assert.Equal(["text1", "text4", "text5"], completed);
    }

    [Fact]
    public async Task Translation_context_is_reset_between_sessions()
    {
        var translator = new ContextTranslator();
        var processor = new SubtitleProcessor(new FakeRecognizer(s => Task.FromResult(Result(s))), translator);
        var first = new SessionInfo(Guid.NewGuid(), "第一段");
        var second = new SessionInfo(Guid.NewGuid(), "第二段");

        processor.Enqueue(Segment(1), first);
        processor.Enqueue(Segment(2), second);
        await processor.DisposeAsync();

        Assert.Equal(2, translator.Resets);
    }
}
