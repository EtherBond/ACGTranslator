using System.Diagnostics;
using System.Text.Json;
using ACGTranslator.Models;
using ACGTranslator.Services;
namespace ACGTranslator.Tests;
public class PipelineDiagnosticsTests
{
    private static SpeechSegmentEventArgs Segment(bool live = false) => new([1], TimeSpan.Zero, TimeSpan.FromSeconds(1))
    { SpeechEndTimestamp = live ? Stopwatch.GetTimestamp() : null, SegmentReadyTimestamp = live ? Stopwatch.GetTimestamp() : null };
    [Fact]
    public void Bounded_clear_does_not_resurrect_inflight_or_evicted_entries()
    {
        var d = new PipelineDiagnostics(2);
        var old = d.Enqueue(Segment()); d.Enqueue(Segment()); d.Enqueue(Segment());
        d.MarkAsrStart(old); d.AttachEntry(old, Guid.NewGuid());
        Assert.Equal(2, d.Snapshot().Records.Count);
        var active = d.Snapshot().Records[0].Id;
        d.Clear(); d.MarkAsrEnd(active); d.AttachEntry(active, Guid.NewGuid());
        Assert.Empty(d.Snapshot().Records); Assert.Empty(d.Snapshot().Metrics);
    }
    [Fact]
    public void Offline_has_no_endpoint_latency_and_ui_is_idempotent()
    {
        var d = new PipelineDiagnostics(); var id = d.Enqueue(Segment()); var entry = Guid.NewGuid();
        d.MarkAsrStart(id); d.MarkAsrEnd(id); d.AttachEntry(id, entry);
        d.MarkUiApplied(entry, false); var first = d.Snapshot().Records.Last().RecognizedUiApplied;
        d.MarkUiApplied(entry, false);
        Assert.Equal(first, d.Snapshot().Records.Last().RecognizedUiApplied);
        Assert.DoesNotContain("estimated_gate_end_to_recognized_ui_applied", d.Snapshot().Metrics.Keys);
        Assert.Equal(1, d.Snapshot().Metrics["asr"].Count);
    }
    [Fact]
    public void Correlated_split_entries_do_not_duplicate_asr_aggregate_or_leak_content()
    {
        var d = new PipelineDiagnostics(); var id = d.Enqueue(Segment(true));
        d.MarkAsrStart(id); d.MarkAsrEnd(id);
        for (var i = 0; i < 2; i++) { var child = Guid.NewGuid(); d.AttachEntry(id, child); d.MarkTranslationStart(child); d.MarkTranslationEnd(child); d.MarkUiApplied(child, true); }
        var s = d.Snapshot();
        Assert.Equal(1, s.Metrics["asr"].Count); Assert.Equal(2, s.Metrics["translation"].Count);
        Assert.Equal(2, s.Metrics["estimated_gate_end_to_translation_ui_applied"].Count);
        Assert.All(s.Records.Skip(1), r => Assert.Equal(id, r.SegmentId));
        Assert.DoesNotContain("SourceText", JsonSerializer.Serialize(s));
    }
    [Fact]
    public void Cancellation_only_marks_outstanding_and_records_monotonic_terminal_time()
    {
        var d = new PipelineDiagnostics(); var queued = d.Enqueue(Segment()); var dropped = d.Enqueue(Segment());
        d.MarkOutcome(dropped, "dropped"); d.CancelOutstanding();
        var s = d.Snapshot(); Assert.Equal(1, s.Outcomes["cancelled"]); Assert.Equal(1, s.Outcomes["dropped"]);
        Assert.All(s.Records, r => Assert.True(r.OutcomeTimestamp >= r.Enqueued));
    }
    [Fact]
    public void Shared_diagnostics_cancellation_is_scoped_and_asr_end_is_first_observation()
    {
        var d = new PipelineDiagnostics(); var owner1 = Guid.NewGuid(); var owner2 = Guid.NewGuid();
        var first = d.Enqueue(Segment(), owner1); var second = d.Enqueue(Segment(), owner2);
        d.MarkAsrStart(first); d.MarkAsrEnd(first); var end = d.Snapshot().Records[0].AsrEnd;
        d.MarkAsrEnd(first); Assert.Equal(end, d.Snapshot().Records[0].AsrEnd);
        var queuedFirst = d.Enqueue(Segment(), owner1);
        d.CancelOutstanding(owner1);
        Assert.Equal("cancelled", d.Snapshot().Records.Single(r => r.Id == queuedFirst).Outcome);
        Assert.Equal("queued", d.Snapshot().Records.Single(r => r.Id == second).Outcome);
    }
    private sealed class Recognizer(bool fail = false) : ISpeechRecognizer
    { public Task<AsrResult> RecognizeAsync(float[] samples, CancellationToken cancellationToken = default) => fail ? Task.FromException<AsrResult>(new InvalidOperationException("private message")) : Task.FromResult(new AsrResult { Text = "private transcript", Language = "ja" }); }
    private sealed class Translator(bool fail = false) : ITextTranslator
    { public Task<string> TranslateAsync(string text, string sourceLanguage, CancellationToken cancellationToken = default) => fail ? Task.FromException<string>(new InvalidOperationException("private failure")) : Task.FromResult("private translation"); }
    [Fact]
    public async Task Processor_correlates_success_failure_and_rejected_after_stop_without_text()
    {
        var d = new PipelineDiagnostics(); var p = new SubtitleProcessor(new Recognizer(), new Translator(true), diagnostics: d);
        p.Enqueue(Segment(), new(Guid.NewGuid(), "private session")); await p.DisposeAsync();
        p.Enqueue(Segment(), new(Guid.NewGuid(), "private session"));
        var s = d.Snapshot(); Assert.Contains(s.Records, r => r.Outcome == "translation_failed"); Assert.Contains(s.Records, r => r.Outcome == "rejected");
        Assert.DoesNotContain("private", JsonSerializer.Serialize(s));
        var d2 = new PipelineDiagnostics(); var p2 = new SubtitleProcessor(new Recognizer(true), new Translator(), diagnostics: d2);
        p2.Enqueue(Segment(), new(Guid.NewGuid(), "session")); await p2.DisposeAsync();
        Assert.Equal("asr_failed", Assert.Single(d2.Snapshot().Records).Outcome);
    }
}
