using ACGTranslator.Models;
namespace ACGTranslator.Services;

// Translation may lag behind recognition. Retain each pending group until its
// translation arrives, so advancing recognition cannot silently hide Chinese text.
public sealed class SubtitleDisplay
{
    private sealed class Group(long sequence)
    {
        public long Sequence { get; } = sequence;
        public List<SubtitleEntry> Entries { get; } = [];
    }

    private readonly Dictionary<Guid, Group> _groups = [];
    private Group? _visible;
    private long _sequence, _lastCompleted = -1;
    public IReadOnlyList<SubtitleEntry> Entries => _visible?.Entries ?? [];

    public void Recognized(SubtitleEntry entry)
    {
        var key = entry.TurnGroupId ?? entry.Id;
        if (!_groups.TryGetValue(key, out var group))
            _groups[key] = group = new Group(_sequence++);
        if (group.Entries.All(e => e.Id != entry.Id)) group.Entries.Add(entry);
        // Once Chinese text is visible, preserve the matched original and
        // translation until the next translation finishes.
        if (_lastCompleted < 0) _visible = group;
    }

    public bool Completed(SubtitleEntry entry)
    {
        var key = entry.TurnGroupId ?? entry.Id;
        if (!_groups.TryGetValue(key, out var group) || group.Sequence < _lastCompleted)
            return false;
        var index = group.Entries.FindIndex(e => e.Id == entry.Id);
        if (index < 0) return false;
        group.Entries[index] = entry;
        _visible = group;
        _lastCompleted = group.Sequence;
        foreach (var old in _groups.Where(p => p.Value.Sequence < _lastCompleted).Select(p => p.Key).ToArray())
            _groups.Remove(old);
        return true;
    }

    public void Clear()
    {
        _groups.Clear();
        _visible = null;
        _sequence = 0;
        _lastCompleted = -1;
    }
}
