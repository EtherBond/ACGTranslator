using System.Text.Json;

namespace ACGTranslator.Models;

public static class CatalogKind
{
    public const string Asr = "asr";
    public const string Translator = "translator";
    public const string Runtime = "runtime";
}

// Downloadable models and runtime components, pinned to exact revisions and hashes.
// Lives in Resources/model-catalog.json so new models can be added without code changes.
public sealed class ModelCatalog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public int Version { get; init; }
    // Source name → base URLs tried in order (mirror first).
    public Dictionary<string, string[]> Sources { get; init; } = [];
    public List<CatalogItem> Items { get; init; } = [];

    public static ModelCatalog Load(string path) =>
        JsonSerializer.Deserialize<ModelCatalog>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("模型目录文件为空");

    public static ModelCatalog LoadDefault() => Load(Path.Combine(AppContext.BaseDirectory, "Resources", "model-catalog.json"));

    public CatalogItem? Find(string? id) => string.IsNullOrEmpty(id) ? null : Items.FirstOrDefault(i => i.Id == id);
}

public sealed class CatalogItem
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    // Preference when GPU acceleration is available, and without it (0 = too slow on CPU).
    public int Priority { get; init; }
    public int CpuPriority { get; init; }
    // "cuda" or "vulkan" for GPU runtimes.
    public string? Gpu { get; init; }
    public int MinDriver { get; init; }
    // Translators: "argos", "nllb" or "llm"; llm prompt style "hymt", "qwen" or "sakura".
    public string? Engine { get; init; }
    public string? Style { get; init; }
    // Runtimes: capability provided, "whisper-cuda" or "llama".
    public string? Provides { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string License { get; init; } = "";
    public int VramMb { get; init; }
    public string[] Languages { get; init; } = [];
    public List<CatalogFile> Files { get; init; } = [];

    public long TotalSize => Files.Sum(f => f.Size);
    public bool IsArchive => Files.Any(f => f.Extract is not null);
    public override string ToString() => Name;
}

public sealed class CatalogFile
{
    public string Source { get; init; } = "";
    // Appended to the source base URL.
    public string Path { get; init; } = "";
    // File name inside the item directory; defaults to the last path segment.
    public string? Name { get; init; }
    public long Size { get; init; }
    public string? Sha256 { get; init; }
    // Git blob SHA-1, for small files that Hugging Face does not store in LFS.
    public string? GitSha1 { get; init; }
    // "zip" to extract into the item directory; Include limits extraction to one folder (flattened).
    public string? Extract { get; init; }
    public string? Include { get; init; }

    public string FileName => Name ?? System.IO.Path.GetFileName(Path);
}
