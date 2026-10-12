namespace ACGTranslator.Services;

public static class AppPaths
{
    // Project root holding .venv and models; found by walking up from the executable.
    public static string Root { get; } = FindRoot();

    // Settings and history live per user, outside the repository.
    public static string Data { get; } = Environment.GetEnvironmentVariable("SUBTITLE_DATA_DIR") is { Length: > 0 } configured
        ? Path.GetFullPath(configured)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ACGTranslator");

    // Kept only to migrate settings and history created by pre-rename builds.
    public static string LegacyData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfflineSubtitleTranslator");

    public static string Models { get; } = Environment.GetEnvironmentVariable("SUBTITLE_MODELS_DIR") is { Length: > 0 } models
        ? Path.GetFullPath(models)
        : Path.Combine(Root, "models");

    private static string FindRoot()
    {
        var configured = Environment.GetEnvironmentVariable("SUBTITLE_ROOT");
        if (!string.IsNullOrEmpty(configured)) return Path.GetFullPath(configured);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".venv"))) return dir.FullName;
        return AppContext.BaseDirectory;
    }

    public static string Python(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var venv = Path.Combine(Root, ".venv", "Scripts", "python.exe");
        return File.Exists(venv) ? venv : "python";
    }
}
