using ACGTranslator.Services;
using NAudio.Wave;

namespace ACGTranslator.Tests;

public class GameCaptureTests
{
    [Fact]
    public void Stable_caption_uses_first_changed_frame_not_ocr_completion()
    {
        var tracker = new StableCaptionTracker();
        var rows = new List<CaptureEntry>(); tracker.Completed += rows.Add;
        tracker.Observe(1, "こ", "", "", "a", 0);
        tracker.Observe(1.2, "こんにちは", "你好", "A", "b", 0);
        tracker.Observe(2, "こんにちは", "你好", "A", "b", 0);
        tracker.Observe(3, "次", "下", "B", "c", 0);
        tracker.Observe(3.2, "次の台詞", "下一句", "B", "d", 0);
        tracker.Observe(4, "次の台詞", "下一句", "B", "d", 0);
        Assert.Single(rows); Assert.Equal(1, rows[0].VisualStart); Assert.Equal(3, rows[0].VisualEnd);
        Assert.Equal("こんにちは", rows[0].Japanese); Assert.Equal("待复核", rows[0].Review);
        tracker.Flush(5);
        Assert.Equal(2, rows.Count); Assert.Equal(3, rows[1].VisualStart);
    }
    [Fact]
    public void Chinese_finishing_later_does_not_split_same_japanese()
    {
        var tracker = new StableCaptionTracker(); var rows = new List<CaptureEntry>(); tracker.Completed += rows.Add;
        tracker.Observe(0, "猫です", "猫", "A", "a", 0);
        tracker.Observe(.8, "猫です", "猫", "A", "a", 0);
        tracker.Observe(1, "猫です", "是猫", "A", "b", 0);
        tracker.Observe(1.8, "猫です", "是猫", "A", "b", 0);
        tracker.Flush(3);
        var row = Assert.Single(rows); Assert.Equal("是猫", row.Chinese); Assert.Equal(0, row.VisualStart);
    }
    [Fact]
    public void Blank_or_pause_breaks_repeat_deduplication()
    {
        var tracker = new StableCaptionTracker(); var rows = new List<CaptureEntry>(); tracker.Completed += rows.Add;
        tracker.Observe(0, "はい", "好", "", "a", 0); tracker.Observe(.8, "はい", "好", "", "a", 0);
        tracker.Observe(1, "", "", "", "b", 0); tracker.Observe(1.8, "", "", "", "b", 0);
        tracker.Observe(2, "はい", "好", "", "c", 0); tracker.Observe(2.8, "はい", "好", "", "c", 0);
        tracker.Flush(3);
        Assert.Equal(2, rows.Count); Assert.Equal(1, rows[0].VisualEnd); Assert.Equal(2, rows[1].VisualStart);
        tracker.Observe(4, "はい", "好", "", "d", 1); tracker.Observe(4.8, "はい", "好", "", "d", 1); tracker.Flush(5);
        Assert.Equal(3, rows.Count); Assert.Equal(1, rows[2].Block);
    }
    [Fact]
    public void Unstable_short_caption_is_not_promoted_to_reference()
    {
        var tracker = new StableCaptionTracker(); var rows = new List<CaptureEntry>(); tracker.Completed += rows.Add;
        tracker.Observe(0, "途", "途", "", "a", 0); tracker.Observe(.1, "途中", "途中", "", "b", 0); tracker.Flush(.2);
        Assert.Empty(rows);
    }
    [Fact]
    public void Region_checks_overflow_and_resolution_change()
    {
        Assert.True(new CaptureRegion(10, 20, 100, 40).Fits(800, 600));
        Assert.False(new CaptureRegion(int.MaxValue, 20, 100, 40).Fits(800, 600));
        Assert.False(new CaptureRegion(-1, 20, 100, 40).Fits(800, 600));
        Assert.False(new CaptureRegion(700, 20, 100, 40).Fits(640, 480));
    }
    [Fact]
    public void Session_paths_cannot_escape_managed_directory()
    {
        Assert.Throws<InvalidDataException>(() => CaptureStore.Folder("../game"));
        Assert.Throws<InvalidDataException>(() => CaptureStore.SafeFile(Guid.NewGuid().ToString("N"), "../../game.exe"));
    }
    [Fact]
    public void Export_requires_explicitly_confirmed_entries()
    {
        var session = new CaptureSession { Entries = [new CaptureEntry { Japanese = "テスト", Review = "待复核" }] };
        Assert.Throws<InvalidOperationException>(() => CaptureAudioExport.Export(session));
    }
    [Fact]
    public void Wave_export_cuts_by_sample_and_rejects_invalid_ranges()
    {
        string root = Path.Combine(Path.GetTempPath(), "acg-capture-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.wav"), target = Path.Combine(root, "cut.wav");
            using (var writer = new WaveFileWriter(source, new WaveFormat(16000, 16, 1))) writer.WriteSamples(new float[32000], 0, 32000);
            CaptureAudioExport.Cut(source, target, .25, 1.25);
            using (var reader = new WaveFileReader(target)) { Assert.Equal(32000, reader.Length); Assert.Equal(1, reader.TotalTime.TotalSeconds); }
            Assert.Throws<InvalidDataException>(() => CaptureAudioExport.Cut(source, target, 0, 3));
            Assert.Throws<InvalidDataException>(() => CaptureAudioExport.Cut(source, target, double.NaN, 1));
            Assert.Throws<InvalidDataException>(() => CaptureAudioExport.Cut(source, target, 1, 1));
        }
        finally { Directory.Delete(root, true); }
    }
}
