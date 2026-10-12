using System.Text;
using ACGTranslator.Models;

namespace ACGTranslator.Services;

public static class SubtitleExporter
{
    // Entries from several sessions are laid out back to back, because each session's clock starts at zero.
    public static string ToSrt(IEnumerable<SubtitleEntry> entries, bool bilingual = false)
    {
        var builder = new StringBuilder();
        var index = 1;
        var offset = TimeSpan.Zero;
        foreach (var session in entries.GroupBy(e => e.SessionId).OrderBy(g => g.Min(e => e.Timestamp)))
        {
            var sessionEnd = TimeSpan.Zero;
            foreach (var entry in session.OrderBy(e => e.Start))
            {
                var lines = Lines(entry, bilingual);
                if (lines.Count == 0) continue;
                var end = entry.End > entry.Start ? entry.End : entry.Start + TimeSpan.FromSeconds(3);
                builder.AppendLine(index++.ToString());
                builder.AppendLine($"{Format(offset + entry.Start)} --> {Format(offset + end)}");
                foreach (var line in lines) builder.AppendLine(line);
                builder.AppendLine();
                if (end > sessionEnd) sessionEnd = end;
            }
            offset += sessionEnd + TimeSpan.FromSeconds(1);
        }
        return builder.ToString();
    }

    private static List<string> Lines(SubtitleEntry entry, bool bilingual)
    {
        static string Clean(string text) => string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var chinese = Clean(entry.ChineseText);
        var source = Clean(entry.SourceText);
        if (bilingual) return new[] { chinese, source }.Where(t => t.Length > 0).Distinct().ToList();
        var text = chinese.Length > 0 ? chinese : source;
        return text.Length > 0 ? [text] : [];
    }

    private static string Format(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";
}
