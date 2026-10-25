using System.Text.Json;
using ACGTranslator.Services;

namespace ACGTranslator.Tests;

public class CaptureHandoffTests
{
    [Fact]
    public void Snapshot_includes_every_status_and_does_not_promote_ocr()
    {
        string root = Path.Combine(Path.GetTempPath(), "acg-handoff-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "capture.wav"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "evidence.png"), [4, 5, 6]);
            var session = new CaptureSession { State = "待整理", Entries = [
                new() { Japanese = "一", Review = "已确认", Screenshot = "evidence.png" },
                new() { Japanese = "二", Review = "已排除", Screenshot = "evidence.png" },
                new() { Japanese = "三", Review = "待复核", Screenshot = "missing.png" }] };
            string folder = CaptureHandoff.Export(session, root);
            var rows = File.ReadAllLines(Path.Combine(folder, "candidates.jsonl")).Select(line => JsonDocument.Parse(line).RootElement).ToArray();
            Assert.Equal(3, rows.Length); Assert.All(rows, r => Assert.False(r.GetProperty("verified_original").GetBoolean()));
            Assert.Equal("已排除", rows[1].GetProperty("previous_review").GetString());
            using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "bundle.json")));
            Assert.Contains("missing.png", marker.RootElement.GetProperty("missing_files").EnumerateArray().Select(x => x.GetString()));
            session.Entries[0].Japanese = "改";
            Assert.Equal("一", rows[0].GetProperty("original_ocr").GetString());
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(folder, "capture.wav")));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void Snapshot_rejects_outside_evidence_and_active_capture()
    {
        var active = new CaptureSession(); Assert.Throws<InvalidOperationException>(() => CaptureHandoff.Export(active));
        string root = Path.Combine(Path.GetTempPath(), "acg-handoff-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "capture.wav"), [1]);
            var s = new CaptureSession { State = "待整理", Entries = [new() { Screenshot = "../outside.png" }] };
            Assert.Throws<InvalidDataException>(() => CaptureHandoff.Export(s, root));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "handoffs"), "bundle.json", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void Evidence_region_preserves_padding_without_leaving_window()
    {
        var p = new CaptureProfile { Width = 800, Height = 600, Japanese = new(100, 500, 600, 80), Chinese = new(100, 450, 500, 40) };
        var r = p.EvidenceRegion(); Assert.True(r.Fits(800, 600)); Assert.True(r.Y < 450); Assert.Equal(600, r.Y + r.Height);
    }
}
