using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ACGTranslator.Services;

// An immutable, portable evidence snapshot. Raw OCR never becomes a training label here.
public static class CaptureHandoff
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string Export(CaptureSession session, string? sourceDirectory = null)
    {
        if (session.State == "采集中") throw new InvalidOperationException("请先停止并保存采集");
        string source = Path.GetFullPath(sourceDirectory ?? CaptureStore.Folder(session.Id));
        if (!File.Exists(Path.Combine(source, "capture.wav"))) throw new FileNotFoundException("缺少原始录音，无法生成整理包");
        string folder = Path.Combine(source, "handoffs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        string Safe(string relative)
        {
            if (Path.IsPathRooted(relative)) throw new InvalidDataException("素材路径必须相对会话目录");
            var path = Path.GetFullPath(Path.Combine(source, relative));
            if (!path.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("素材路径超出采集目录");
            for (var current = new FileInfo(path) as FileSystemInfo; current is not null; current = current is FileInfo f ? f.Directory : ((DirectoryInfo)current).Parent)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("整理包不读取链接文件或目录");
                if (string.Equals(current.FullName.TrimEnd('\\'), source.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) break;
            }
            return path;
        }
        void Hash(string relative)
        {
            using var stream = File.OpenRead(Path.Combine(folder, relative));
            hashes[relative.Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        void Copy(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return;
            if (hashes.ContainsKey(relative.Replace('\\', '/'))) return;
            string path = Safe(relative);
            if (!File.Exists(path)) { missing.Add(relative); return; }
            string target = Path.Combine(folder, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, false); Hash(relative);
        }
        Copy("capture.wav");
        File.WriteAllText(Path.Combine(folder, "session.json"), JsonSerializer.Serialize(session, Json), Encoding.UTF8); Hash("session.json");
        Copy("observations.jsonl"); Copy("audio-timing.jsonl"); Copy("frame-index.jsonl"); Copy("audio-health.json");
        var frames = Path.Combine(source, "frames");
        if (Directory.Exists(frames))
            foreach (var path in Directory.EnumerateFiles(frames, "*.png")) Copy(Path.GetRelativePath(source, path));
        foreach (var entry in session.Entries) Copy(entry.Screenshot);
        var rows = session.Entries.Select(entry => new
        {
            id = session.Id + "-" + entry.Id, session_id = session.Id, game = session.Game,
            recording = "capture.wav", screenshot = entry.Screenshot.Replace('\\', '/'),
            original_ocr = entry.Japanese, translation_ocr = entry.Chinese, speaker_ocr = entry.Speaker,
            proposed_start_s = entry.StartSeconds, proposed_end_s = entry.EndSeconds,
            visual_start_s = entry.VisualStart, visual_end_s = entry.VisualEnd, capture_block = entry.Block,
            previous_review = entry.Review, status = "raw", verified_original = false, entry.Note
        });
        File.WriteAllLines(Path.Combine(folder, "candidates.jsonl"), rows.Select(row => JsonSerializer.Serialize(row)), Encoding.UTF8); Hash("candidates.jsonl");
        File.WriteAllText(Path.Combine(folder, "README.txt"),
            "这是供助手整理的原始采集包，不是可直接训练的标准答案。\n" +
            "包含全部候选（包括过去被排除和未确认的条目）、连续录音、截图、OCR 观察和时间记录。\n" +
            "请结合截图纠正日文，结合录音核对配音和边界，使用 capture_dataset.py 导出。\n" +
            "不要用 ASR 输出直接替代原文，不要把没有配音或无法确认的条目混入训练集。\n" +
            "保留 decisions.jsonl 修改记录，将不确定项列入 pending.jsonl；不随机拆相邻句子成训练/测试集。\n" +
            "本包不会自动调用聊天助手，也不会训练模型或调整翻译器设置。\n", Encoding.UTF8); Hash("README.txt");
        // Written last: absence of this marker means a failed/incomplete snapshot.
        File.WriteAllText(Path.Combine(folder, "bundle.json"), JsonSerializer.Serialize(new
        {
            schema = "acg.capture-handoff.v1", session_id = session.Id, created_utc = DateTime.UtcNow,
            status = "awaiting_assistant", candidate_count = session.Entries.Count,
            duration_s = session.DurationSeconds, files = hashes, missing_files = missing.Distinct().ToArray()
        }, Json), Encoding.UTF8);
        return folder;
    }
    public static string? Latest(string sessionId)
    {
        string root = Path.Combine(CaptureStore.Folder(sessionId), "handoffs");
        return Directory.Exists(root) ? Directory.EnumerateDirectories(root).Where(p => File.Exists(Path.Combine(p, "bundle.json"))).OrderDescending().FirstOrDefault() : null;
    }
}
