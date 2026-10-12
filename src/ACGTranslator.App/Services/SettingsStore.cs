using System.Text.Json;
using ACGTranslator.Models;
namespace ACGTranslator.Services;
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path = Path.Combine(AppPaths.Data, "settings.json");
    private readonly string _legacyPath = Path.Combine(AppPaths.LegacyData, "settings.json");
    public AppSettings Load()
    {
        try
        {
            var path = File.Exists(_path) ? _path : _legacyPath;
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new() : new();
        }
        catch { return new(); }
    }
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, _path, true);
    }
}
