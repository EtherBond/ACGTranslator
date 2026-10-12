using ACGTranslator.Models;

namespace ACGTranslator.Services;

public sealed record AsrPlan(string Name, string ModelPath, string Device, IReadOnlyList<string> DllDirectories)
{
    public string Summary => $"{Name}（{(Device == "cuda" ? "显卡" : "CPU")}）";
}

public sealed record TranslatorPlan(CatalogItem Item, IReadOnlyList<string> Arguments, string Device)
{
    public string Summary => $"{Item.Name}（{(Device == "cpu" ? "CPU" : "显卡")}）";
}

public sealed record Recommendation(CatalogItem Asr, CatalogItem Translator, IReadOnlyList<CatalogItem> Runtimes)
{
    public IEnumerable<CatalogItem> Items => [Asr, Translator, .. Runtimes];
}

// Turns settings plus what is installed into concrete worker arguments, and picks a recommended
// download set for the detected hardware. Rankings come from the catalog (measured, see
// scripts/translator-comparison.md and scripts/asr-comparison.md).
public static class EngineResolver
{
    // Left free for the desktop, games and the video player.
    private const int ReservedVramMb = 1000;

    public static AsrPlan ResolveAsr(AppSettings settings, ModelStore store, GpuInfo? gpu)
    {
        var cublas = store.InstalledRuntime("whisper-cuda");
        var device = settings.AsrDevice switch
        {
            "cpu" => "cpu",
            "cuda" => "cuda",
            _ => gpu is not null && cublas is not null ? "cuda" : "cpu"
        };
        if (device == "cuda" && gpu is null) throw new InvalidOperationException("没有检测到 NVIDIA 显卡，请在设置中把识别设备改为 CPU");
        if (device == "cuda" && cublas is null) throw new InvalidOperationException("使用显卡识别需要先在“模型”中下载“CUDA 识别加速”");
        string name, path;
        if (!string.IsNullOrWhiteSpace(settings.AsrModelPath))
        {
            path = settings.AsrModelPath;
            name = Path.GetFileName(path.TrimEnd('\\', '/'));
        }
        else
        {
            var item = SelectInstalled(store, CatalogKind.Asr, settings.AsrModelId, device == "cuda")
                ?? throw new InvalidOperationException("还没有安装识别模型，请点击“模型”下载");
            path = store.GetDirectory(item);
            name = item.Name;
        }
        return new AsrPlan(name, path, device, device == "cuda" ? [store.GetDirectory(cublas!)] : []);
    }

    public static TranslatorPlan ResolveTranslator(AppSettings settings, ModelStore store, GpuInfo? gpu)
    {
        var cpuOnly = settings.TranslatorDevice == "cpu";
        var item = SelectInstalled(store, CatalogKind.Translator, settings.TranslatorId, gpu is not null && !cpuOnly)
            ?? throw new InvalidOperationException("还没有安装翻译引擎，请点击“模型”下载");
        var args = new List<string> { "--engine", item.Engine ?? "", "--languages", string.Join(",", item.Languages) };
        var device = "cpu";
        switch (item.Engine)
        {
            case "argos":
                args.AddRange(["--model", store.GetModelPath(item)]);
                break;
            case "nllb":
                var cublas = store.InstalledRuntime("whisper-cuda");
                device = !cpuOnly && gpu is not null && cublas is not null ? "cuda" : "cpu";
                args.AddRange(["--model", store.GetModelPath(item), "--device", device]);
                if (device == "cuda") args.AddRange(["--dll-dir", store.GetDirectory(cublas!)]);
                break;
            case "llm":
                var runtime = SelectLlamaRuntime(store, gpu, cpuOnly)
                    ?? throw new InvalidOperationException("运行大模型翻译需要先在“模型”中下载 llama.cpp 运行组件");
                // A Vulkan runtime can still provide the CPU backend. Do not request GPU
                // layers unless hardware was actually detected and the user allowed it.
                device = !cpuOnly && gpu is not null && runtime.Gpu is not null ? "gpu" : "cpu";
                args.AddRange(["--model", store.GetModelPath(item), "--style", item.Style ?? "qwen", "--device", device,
                    "--llama-server", Path.Combine(store.GetDirectory(runtime), "llama-server.exe")]);
                break;
            default:
                throw new InvalidDataException($"未知的翻译引擎类型：{item.Engine}");
        }
        return new TranslatorPlan(item, args, device);
    }

    // The selected item if installed, otherwise the best installed one.
    public static CatalogItem? SelectInstalled(ModelStore store, string kind, string? id, bool gpu)
    {
        var installed = store.Installed(kind).ToList();
        return installed.FirstOrDefault(i => i.Id == id)
            ?? installed.OrderByDescending(i => gpu ? i.Priority : i.CpuPriority).ThenByDescending(i => i.Priority).FirstOrDefault();
    }

    private static CatalogItem? SelectLlamaRuntime(ModelStore store, GpuInfo? gpu, bool cpuOnly) => store.Catalog.Items
        .Where(i => i.Provides == "llama" && store.IsInstalled(i))
        .Where(i => i.Gpu != "cuda" || (gpu is not null && gpu.DriverMajor >= i.MinDriver))
        // For CPU inference prefer builds without CUDA; every build includes the CPU backend.
        .OrderByDescending(i => cpuOnly ? (i.Gpu is null ? 2 : i.Gpu == "vulkan" ? 1 : 0) : i.Priority)
        .FirstOrDefault();

    public static Recommendation Recommend(ModelCatalog catalog, GpuInfo? gpu)
    {
        var onGpu = gpu is not null;
        int Rank(CatalogItem item) => onGpu ? item.Priority : item.CpuPriority;
        var budget = (gpu?.MemoryMb ?? 0) - ReservedVramMb;
        var asrs = catalog.Items.Where(i => i.Kind == CatalogKind.Asr && Rank(i) > 0).ToList();
        // The default must handle both Japanese and English.
        var translators = catalog.Items.Where(i => i.Kind == CatalogKind.Translator && Rank(i) > 0 && i.Languages.Contains("ja") && i.Languages.Contains("en")).ToList();
        var pair = (from asr in asrs
                    from translator in translators
                    where !onGpu || asr.VramMb + translator.VramMb <= budget
                    orderby Rank(asr) + Rank(translator) descending, asr.VramMb + translator.VramMb
                    select (Asr: asr, Translator: translator)).FirstOrDefault();
        if (pair.Asr is null) pair = (asrs.MinBy(i => i.VramMb)!, translators.MinBy(i => i.VramMb)!);

        var runtimes = new List<CatalogItem>();
        if (onGpu && catalog.Items.FirstOrDefault(i => i.Provides == "whisper-cuda") is { } cublas) runtimes.Add(cublas);
        if (pair.Translator.Engine == "llm")
        {
            var llama = catalog.Items
                .Where(i => i.Provides == "llama" && (onGpu ? i.Gpu == "cuda" && gpu!.DriverMajor >= i.MinDriver : i.Gpu != "cuda"))
                .MaxBy(i => i.Priority);
            if (llama is not null) runtimes.Add(llama);
        }
        return new Recommendation(pair.Asr, pair.Translator, runtimes);
    }
}
