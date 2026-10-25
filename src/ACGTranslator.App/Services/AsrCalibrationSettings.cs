using System.Text.Json;
using ACGTranslator.Models;

namespace ACGTranslator.Services;

public sealed record AsrCalibrationValues(string ModelId, string ModelPath, string Device, int Beam)
{
    public static AsrCalibrationValues From(AppSettings s) => new(s.AsrModelId, s.AsrModelPath, s.AsrDevice, s.AsrBeamSize);
    public void CopyTo(AppSettings s)
    {
        if (Device is not ("auto" or "cpu" or "cuda") || Beam is not (0 or 1 or 2 or 5)) throw new InvalidDataException("校准设置无效");
        s.AsrModelId = ModelId; s.AsrModelPath = ModelPath; s.AsrDevice = Device; s.AsrBeamSize = Beam;
    }
    public static string Context(AppSettings s) => JsonSerializer.Serialize(new { values = From(s), s.Language, s.TurnEnhancement, s.PythonExecutable, s.SpeechSegmentation, s.AdaptiveAsr, s.AsrComputeType, s.AsrHotwords, s.PreserveAsrContext });
}

public static class AsrCalibrationSettings
{
    public static string Root => Path.Combine(AppPaths.Root, "data", "calibration");
    public static void Apply(AppSettings active, AppSettings original, JsonElement candidate, string folder)
    {
        var current = App.Settings.Load();
        if (AsrCalibrationValues.Context(current) != AsrCalibrationValues.Context(original))
            throw new InvalidOperationException("校准期间识别设置已发生变化，未自动覆盖。请以当前设置重新校准。");
        string model = Path.GetFullPath(candidate.GetProperty("model").GetString()!);
        if (!File.Exists(Path.Combine(model, "model.bin"))) throw new IOException("获选模型已经不存在，未应用设置");
        var next = new AsrCalibrationValues("", model, candidate.GetProperty("device").GetString()!, candidate.GetProperty("beam").GetInt32());
        // Preserve unrelated settings loaded immediately before the write.
        next.CopyTo(current);
        File.WriteAllText(Path.Combine(folder, "planned-values.json"), JsonSerializer.Serialize(next));
        App.Settings.Save(current);
        next.CopyTo(active);
        try { File.WriteAllText(Path.Combine(folder, "applied-values.json"), JsonSerializer.Serialize(next)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Settings were already atomically saved; do not report them as unchanged. */ }
    }
    public static void Restore(AppSettings active, string folder)
    {
        var previous = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(folder, "settings-before.json")))
            ?? throw new InvalidDataException("设置备份无效");
        var current = App.Settings.Load();
        var old = AsrCalibrationValues.From(previous);
        if (old.ModelPath.Length > 0 && !File.Exists(Path.Combine(old.ModelPath, "model.bin"))) throw new IOException("备份中的模型已被删除，请先恢复该模型");
        old.CopyTo(current); App.Settings.Save(current); old.CopyTo(active);
        try { File.WriteAllText(Path.Combine(folder, "restored.txt"), DateTime.UtcNow.ToString("O")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Restoration already succeeded. */ }
    }
}
