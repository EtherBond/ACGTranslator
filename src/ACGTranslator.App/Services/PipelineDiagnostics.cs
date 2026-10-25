using System.Diagnostics;

namespace ACGTranslator.Services;

// Monotonic timings only: no transcript, audio, speaker identity, or wall-clock history.
public sealed record PipelineTiming(Guid Id, Guid? SegmentId, long Enqueued, long? EstimatedGateSpeechEnd, long? SegmentReady,
    long? AsrStart = null, long? AsrEnd = null, long? TranslationStart = null, long? TranslationEnd = null,
    long? RecognizedUiApplied = null, long? TranslationUiApplied = null, string Outcome = "queued", long? OutcomeTimestamp = null, Guid? OwnerId = null);
public sealed record PipelineMetric(int Count, double MeanMs, double P50Ms, double P95Ms, double MaxMs);
public sealed record PipelineSnapshot(long StopwatchFrequency, string Scope, IReadOnlyList<PipelineTiming> Records,
    IReadOnlyDictionary<string, PipelineMetric> Metrics, IReadOnlyDictionary<string, int> Outcomes);

public sealed class PipelineDiagnostics
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<Guid, PipelineTiming> _records = new();
    private readonly Queue<Guid> _order = new();
    public PipelineDiagnostics(int capacity = 512) { if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity)); _capacity = capacity; }
    private void Add(PipelineTiming record)
    {
        _records.Add(record.Id, record); _order.Enqueue(record.Id);
        while (_order.Count > _capacity) _records.Remove(_order.Dequeue());
    }
    public Guid Enqueue(SpeechSegmentEventArgs segment, Guid? ownerId = null)
    {
        var id = Guid.NewGuid();
        lock (_gate) Add(new(id, null, Stopwatch.GetTimestamp(), segment.SpeechEndTimestamp, segment.SegmentReadyTimestamp, OwnerId: ownerId));
        return id;
    }
    public void AttachEntry(Guid segmentId, Guid entryId)
    {
        lock (_gate) if (_records.TryGetValue(segmentId, out var parent))
            Add(parent with { Id = entryId, SegmentId = segmentId, Outcome = "recognized" });
    }
    private void Update(Guid id, Func<PipelineTiming, PipelineTiming> update)
    { lock (_gate) if (_records.TryGetValue(id, out var record)) _records[id] = update(record); }
    public void MarkAsrStart(Guid id) => Update(id, r => r with { AsrStart = Stopwatch.GetTimestamp(), Outcome = "recognizing" });
    public void MarkAsrEnd(Guid id) => Update(id, r => r with { AsrEnd = r.AsrEnd ?? Stopwatch.GetTimestamp(), Outcome = "recognized" });
    public void MarkTranslationStart(Guid id) => Update(id, r => r with { TranslationStart = Stopwatch.GetTimestamp(), Outcome = "translating" });
    public void MarkTranslationEnd(Guid id) => Update(id, r => r with { TranslationEnd = Stopwatch.GetTimestamp(), Outcome = "completed" });
    public void MarkOutcome(Guid id, string outcome) => Update(id, r => r with { Outcome = outcome, OutcomeTimestamp = Stopwatch.GetTimestamp() });
    public void MarkUiApplied(Guid entryId, bool translation) => Update(entryId, r => translation
        ? r with { TranslationUiApplied = r.TranslationUiApplied ?? Stopwatch.GetTimestamp() }
        : r with { RecognizedUiApplied = r.RecognizedUiApplied ?? Stopwatch.GetTimestamp() });
    public void CancelOutstanding(Guid? ownerId = null)
    {
        lock (_gate) foreach (var (id, r) in _records.ToArray())
            if ((ownerId == null || r.OwnerId == ownerId) && (r.Outcome is "queued" or "recognizing" or "translating" || (r.SegmentId != null && r.Outcome == "recognized")))
                _records[id] = r with { Outcome = "cancelled", OutcomeTimestamp = Stopwatch.GetTimestamp() };
    }
    public void Clear() { lock (_gate) { _records.Clear(); _order.Clear(); } }
    public PipelineSnapshot Snapshot()
    {
        PipelineTiming[] rows; lock (_gate) rows = _order.Select(id => _records[id]).ToArray();
        var metrics = new Dictionary<string, PipelineMetric>();
        void Metric(string name, IEnumerable<(long? Start, long? End)> pairs)
        {
            var values = pairs.Where(p => p.Start.HasValue && p.End >= p.Start).Select(p => (p.End!.Value - p.Start!.Value) * 1000d / Stopwatch.Frequency).Order().ToArray();
            if (values.Length == 0) return;
            double Percentile(double q) => values[(int)Math.Ceiling(q * values.Length) - 1];
            metrics[name] = new(values.Length, values.Average(), Percentile(.5), Percentile(.95), values[^1]);
        }
        var segments = rows.Where(r => r.SegmentId == null).ToArray();
        var entries = rows.Where(r => r.SegmentId != null).ToArray();
        Metric("enqueue_to_asr_start", segments.Select(r => ((long?)r.Enqueued, r.AsrStart)));
        Metric("asr", segments.Select(r => (r.AsrStart, r.AsrEnd)));
        Metric("estimated_gate_end_to_segment_ready", segments.Select(r => (r.EstimatedGateSpeechEnd, r.SegmentReady)));
        Metric("translation_queue", entries.Select(r => (r.AsrEnd, r.TranslationStart)));
        Metric("translation", entries.Select(r => (r.TranslationStart, r.TranslationEnd)));
        Metric("estimated_gate_end_to_recognized_ui_applied", entries.Select(r => (r.EstimatedGateSpeechEnd, r.RecognizedUiApplied)));
        Metric("estimated_gate_end_to_translation_ui_applied", entries.Select(r => (r.EstimatedGateSpeechEnd, r.TranslationUiApplied)));
        Metric("enqueue_to_recognized_ui_applied", entries.Select(r => ((long?)r.Enqueued, r.RecognizedUiApplied)));
        Metric("enqueue_to_translation_ui_applied", entries.Select(r => ((long?)r.Enqueued, r.TranslationUiApplied)));
        Metric("recognized_dispatch", entries.Select(r => (r.AsrEnd, r.RecognizedUiApplied)));
        Metric("translation_dispatch", entries.Select(r => (r.TranslationEnd, r.TranslationUiApplied)));
        return new(Stopwatch.Frequency, "Recent bounded records only; gate endpoint estimated from capture timeline; UI applied is not screen present. Offline endpoint latency omitted. Translation includes context reset and language bypass.", rows, metrics,
            segments.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count()));
    }
}
