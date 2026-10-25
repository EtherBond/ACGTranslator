using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;

namespace ACGTranslator.Services;

public sealed record CaptureRegion(int X, int Y, int Width, int Height)
{
    public bool Fits(int width, int height) => X >= 0 && Y >= 0 && Width >= 8 && Height >= 8 &&
        (long)X + Width <= width && (long)Y + Height <= height;
}

public sealed class CaptureProfile
{
    public int Width { get; set; }
    public int Height { get; set; }
    public CaptureRegion? Japanese { get; set; }
    public CaptureRegion? Chinese { get; set; }
    public CaptureRegion? Speaker { get; set; }
    // Preserve surrounding subtitle lines even when an OCR selection is too tight.
    public CaptureRegion EvidenceRegion()
    {
        var regions = new[] { Japanese, Chinese, Speaker }.Where(r => r is not null).Select(r => r!).ToArray();
        if (!Valid) throw new InvalidDataException("字幕选区无效");
        int left = Math.Max(0, regions.Min(r => r.X) - 64), top = Math.Max(0, regions.Min(r => r.Y) - 80);
        int right = Math.Min(Width, regions.Max(r => r.X + r.Width) + 64), bottom = Math.Min(Height, regions.Max(r => r.Y + r.Height) + 80);
        return new(left, top, right - left, bottom - top);
    }
    public bool Valid => Japanese?.Fits(Width, Height) == true && Chinese?.Fits(Width, Height) == true &&
        (Speaker is null || Speaker.Fits(Width, Height));
}

public sealed class CaptureEntry : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Block { get; set; }
    public string Japanese { get; set; } = "";
    public string Chinese { get; set; } = "";
    public string Speaker { get; set; } = "";
    public double VisualStart { get; set; }
    public double VisualEnd { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public string Screenshot { get; set; } = "";
    private string _review = "待复核";
    public string Review { get => _review; set { if (_review == value) return; _review = value; PropertyChanged?.Invoke(this, new(nameof(Review))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Note { get; set; } = "画面估计区间，请听音核验原文、角色和边界";
}

public sealed class CaptureSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Game { get; set; } = "TenShiSouZou";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public string State { get; set; } = "采集中";
    public string AudioDevice { get; set; } = "";
    public string Timing { get; set; } = "Stopwatch + callback-arrival approximation; review required";
    public double DurationSeconds { get; set; }
    public CaptureProfile Profile { get; set; } = new();
    public List<CaptureEntry> Entries { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    [JsonIgnore] public string Label => $"{StartedUtc.ToLocalTime():MM-dd HH:mm:ss} · {Game} · {Entries.Count} 条 · {State}";
}

// Stable observations are not gold labels. A new turn starts at the first changed frame,
// not at the later OCR completion or stability time. Only Japanese determines turn identity.
public sealed class StableCaptionTracker
{
    private string _candidate = "";
    private double _candidateSince, _transition;
    private int _count;
    private CaptureEntry? _active;
    private bool _changing;
    public event Action<CaptureEntry>? Completed;
    public void Observe(double at, string japanese, string chinese, string speaker, string screenshot, int block)
    {
        japanese = japanese.Trim(); chinese = chinese.Trim();
        string key = japanese + "\u001f" + chinese;
        if (_active?.Japanese != japanese && !_changing) { _changing = true; _transition = at; }
        if (key != _candidate) { _candidate = key; _candidateSince = at; _count = 1; return; }
        _count++;
        if (_count < 2 || at - _candidateSince < .6) return;
        if (_active is not null && _active.Japanese == japanese)
        {
            _active.Chinese = chinese; _active.Speaker = speaker; _active.Screenshot = screenshot;
            _changing = false;
            return;
        }
        Finish(_transition);
        if (japanese.Length > 0)
            _active = new CaptureEntry { Japanese = japanese, Chinese = chinese, Speaker = speaker,
                VisualStart = _transition, StartSeconds = Math.Max(0, _transition - .25), Screenshot = screenshot, Block = block };
        _changing = false;
    }
    public void Flush(double at)
    {
        Finish(at); _candidate = ""; _count = 0; _changing = false;
    }
    private void Finish(double at)
    {
        if (_active is null) return;
        _active.VisualEnd = Math.Max(_active.VisualStart, at);
        _active.EndSeconds = Math.Max(_active.StartSeconds, at + .15);
        Completed?.Invoke(_active); _active = null;
    }
}

public static class CaptureStore
{
    public static string Root => Path.Combine(AppPaths.Root, "data", "game-captures");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string Folder(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("无效的采集会话 ID");
        return Path.Combine(Root, id);
    }
    public static void Save(CaptureSession session)
    {
        var folder = Folder(session.Id); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "session.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(session, Json));
        File.Move(file + ".tmp", file, true);
    }
    public static List<CaptureSession> LoadAll()
    {
        if (!Directory.Exists(Root)) return [];
        var sessions = new List<CaptureSession>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) continue;
            try
            {
                var session = JsonSerializer.Deserialize<CaptureSession>(File.ReadAllText(Path.Combine(folder, "session.json")), Json);
                if (session is not null && session.Id == Path.GetFileName(folder)) sessions.Add(session);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return sessions.OrderByDescending(s => s.StartedUtc).ToList();
    }
    public static string SafeFile(string id, string name)
    {
        var root = Path.GetFullPath(Folder(id)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件超出会话目录");
        return path;
    }
    public static void Delete(string id)
    {
        var folder = Folder(id);
        // Refuse directory junctions/reparse points, including all descendants.
        CheckDirectory(folder);
        Directory.Delete(folder, true);
    }
    private static void CheckDirectory(string folder)
    {
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能清理链接目录");
        foreach (var item in Directory.EnumerateFileSystemEntries(folder))
        {
            var attr = File.GetAttributes(item);
            if ((attr & FileAttributes.ReparsePoint) != 0) throw new IOException("不能清理含链接的目录");
            if ((attr & FileAttributes.Directory) != 0) CheckDirectory(item);
        }
    }
}
