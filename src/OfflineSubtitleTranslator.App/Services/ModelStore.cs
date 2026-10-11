using System.Text.Json;
using OfflineSubtitleTranslator.Models;

namespace OfflineSubtitleTranslator.Services;

// Where catalog items live on disk:
//   models/asr/<id>, models/translators/<id>, models/runtimes/<id>  installed items
//   models/.downloads/<id>                                          partial downloads (resumable)
public sealed class ModelStore(ModelCatalog catalog, string root)
{
    // Archives are extracted, so their files cannot be checked against the catalog; a marker records completion.
    public const string MarkerName = ".installed";

    public ModelCatalog Catalog { get; } = catalog;
    public string Root { get; } = root;

    public string GetDirectory(CatalogItem item) => Path.Combine(Root, item.Kind switch
    {
        CatalogKind.Asr => "asr",
        CatalogKind.Translator => "translators",
        _ => "runtimes"
    }, item.Id);

    public string GetStagingDirectory(CatalogItem item) => Path.Combine(Root, ".downloads", item.Id);

    // Path passed to the worker: the .gguf file for llama.cpp models, otherwise the directory.
    public string GetModelPath(CatalogItem item) =>
        item.Engine == "llm" ? Path.Combine(GetDirectory(item), item.Files[0].FileName) : GetDirectory(item);

    public bool IsInstalled(CatalogItem item)
    {
        var directory = GetDirectory(item);
        if (item.IsArchive) return File.Exists(Path.Combine(directory, MarkerName));
        // Hashes were checked before the files were moved here; a size check is enough at startup.
        return item.Files.All(f => new FileInfo(Path.Combine(directory, f.FileName)) is { Exists: true } info && info.Length == f.Size);
    }

    public IEnumerable<CatalogItem> Installed(string kind) => Catalog.Items.Where(i => i.Kind == kind && IsInstalled(i));

    public CatalogItem? InstalledRuntime(string provides) => Catalog.Items.FirstOrDefault(i => i.Provides == provides && IsInstalled(i));

    public long PartialBytes(CatalogItem item)
    {
        var staging = GetStagingDirectory(item);
        return Directory.Exists(staging) ? new DirectoryInfo(staging).EnumerateFiles().Sum(f => f.Length) : 0;
    }

    public void Delete(CatalogItem item)
    {
        foreach (var directory in new[] { GetDirectory(item), GetDirectory(item) + ".installing", GetStagingDirectory(item) })
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    internal static void WriteMarker(string directory, CatalogItem item) =>
        File.WriteAllText(Path.Combine(directory, MarkerName), JsonSerializer.Serialize(new { id = item.Id, installed = DateTimeOffset.Now }));
}
