using System.Text;
using System.Text.Json;
using ACGTranslator.Models;
namespace ACGTranslator.Services;

// JSON Lines file: new entries are appended, so recording cost does not grow with history size.
// A torn final line (e.g. after a crash) is skipped instead of discarding the whole history.
public sealed class HistoryStore(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly string _path = path ?? Path.Combine(AppPaths.Data, "history.jsonl");
    private readonly string? _legacyPath = path is null ? Path.Combine(AppPaths.LegacyData, "history.jsonl") : null;
    private readonly object _sync = new();
    private List<SubtitleEntry>? _entries;

    public IReadOnlyList<SubtitleEntry> Load()
    {
        lock (_sync) return [.. Entries];
    }

    public void Add(SubtitleEntry entry)
    {
        lock (_sync)
        {
            // Load the cache before appending, otherwise the first entry would be read back and added twice.
            var entries = Entries;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (!File.Exists(_path) && _legacyPath is { } legacy && File.Exists(legacy)) File.Copy(legacy, _path);
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, Options) + "\n", Utf8);
            entries.Add(entry);
        }
    }

    public void Delete(IEnumerable<Guid> ids)
    {
        var remove = ids.ToHashSet();
        lock (_sync)
        {
            if (Entries.RemoveAll(e => remove.Contains(e.Id)) > 0) Rewrite();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Entries.Clear();
            Rewrite();
        }
    }

    private List<SubtitleEntry> Entries => _entries ??= Read();

    private List<SubtitleEntry> Read()
    {
        var list = new List<SubtitleEntry>();
        var source = File.Exists(_path) ? _path : _legacyPath;
        if (source is null || !File.Exists(source)) return list;
        foreach (var line in File.ReadLines(source, Utf8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<SubtitleEntry>(line, Options) is { } entry) list.Add(entry); }
            catch (JsonException) { }
        }
        return list;
    }

    private void Rewrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllLines(tmp, _entries!.Select(e => JsonSerializer.Serialize(e, Options)), Utf8);
        File.Move(tmp, _path, true);
    }
}
