using ACGTranslator.Models;
using ACGTranslator.Services;

namespace ACGTranslator.Tests;

public class SubtitleExporterTests
{
    private static SubtitleEntry Entry(Guid session, double start, double end, string chinese, string source = "source", int minute = 0) => new()
    {
        SessionId = session,
        Timestamp = new DateTimeOffset(2026, 10, 11, 12, minute, 0, TimeSpan.Zero),
        Start = TimeSpan.FromSeconds(start),
        End = TimeSpan.FromSeconds(end),
        SourceText = source,
        ChineseText = chinese
    };

    [Fact]
    public void Writes_numbered_cues_in_start_order()
    {
        var session = Guid.NewGuid();
        var srt = SubtitleExporter.ToSrt([Entry(session, 5, 6.5, "第二句"), Entry(session, 1.25, 2, "第一句")]);

        Assert.Equal("1\r\n00:00:01,250 --> 00:00:02,000\r\n第一句\r\n\r\n2\r\n00:00:05,000 --> 00:00:06,500\r\n第二句\r\n\r\n", srt);
    }

    [Fact]
    public void Sessions_do_not_overlap()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var srt = SubtitleExporter.ToSrt([Entry(second, 0, 2, "后", minute: 30), Entry(first, 0, 10, "前", minute: 0)]);

        // The second session starts after the first one ends, plus a one second gap.
        Assert.Contains("00:00:11,000 --> 00:00:13,000\r\n后", srt);
    }

    [Fact]
    public void Bilingual_export_keeps_both_lines_and_falls_back_to_source()
    {
        var session = Guid.NewGuid();
        var srt = SubtitleExporter.ToSrt([Entry(session, 0, 1, "你好", "Hello"), Entry(session, 2, 3, "", "Untranslated")], bilingual: true);

        Assert.Contains("你好\r\nHello\r\n", srt);
        Assert.Contains("Untranslated\r\n", srt);
    }

    [Fact]
    public void Multiline_text_is_joined_and_empty_entries_skipped()
    {
        var session = Guid.NewGuid();
        var srt = SubtitleExporter.ToSrt([Entry(session, 0, 1, "第一行\r\n第二行"), Entry(session, 2, 3, "", "")]);

        Assert.StartsWith("1\r\n00:00:00,000 --> 00:00:01,000\r\n第一行 第二行\r\n", srt);
        Assert.DoesNotContain("\r\n2\r\n", srt);
    }
}

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "subtitle-tests-" + Guid.NewGuid());
    private string HistoryPath => Path.Combine(_directory, "history.jsonl");

    [Fact]
    public void Entries_survive_a_new_store_instance()
    {
        var entry = new SubtitleEntry { SourceText = "これはテストです。", ChineseText = "这是测试。" };
        new HistoryStore(HistoryPath).Add(entry);

        var loaded = Assert.Single(new HistoryStore(HistoryPath).Load());
        Assert.Equal(entry.Id, loaded.Id);
        Assert.Equal("这是测试。", loaded.ChineseText);
    }

    [Fact]
    public void Delete_and_clear_rewrite_the_file()
    {
        var store = new HistoryStore(HistoryPath);
        var keep = new SubtitleEntry { SourceText = "keep" };
        var drop = new SubtitleEntry { SourceText = "drop" };
        store.Add(keep);
        store.Add(drop);

        store.Delete([drop.Id]);
        Assert.Equal(keep.Id, Assert.Single(new HistoryStore(HistoryPath).Load()).Id);

        store.Clear();
        Assert.Empty(new HistoryStore(HistoryPath).Load());
    }

    [Fact]
    public void A_torn_last_line_does_not_lose_earlier_entries()
    {
        var store = new HistoryStore(HistoryPath);
        store.Add(new SubtitleEntry { SourceText = "intact" });
        File.AppendAllText(HistoryPath, "{\"SourceText\":\"cut off");

        Assert.Equal("intact", Assert.Single(new HistoryStore(HistoryPath).Load()).SourceText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
