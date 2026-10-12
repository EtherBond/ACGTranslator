using System.Diagnostics;
using System.Globalization;

namespace ACGTranslator.Services;

public sealed record GpuInfo(string Name, int MemoryMb, string DriverVersion)
{
    public int DriverMajor => int.TryParse(DriverVersion.Split('.')[0], out var major) ? major : 0;
    public override string ToString() => $"{Name}（{MemoryMb / 1024.0:0.#} GB 显存，驱动 {DriverVersion}）";
}

public static class HardwareInfo
{
    // nvidia-smi ships with every NVIDIA driver; no GPU or no driver means CPU-only acceleration.
    public static GpuInfo? DetectNvidia()
    {
        try
        {
            var info = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 ? Parse(output.Result) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static GpuInfo? Parse(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var parts = line?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: >= 3 } || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var memory)) return null;
        return new GpuInfo(parts[0], memory, parts[2]);
    }
}
