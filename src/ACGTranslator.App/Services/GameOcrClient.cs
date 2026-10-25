using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ACGTranslator.Services;

public sealed record GameOcrResult(string Japanese, string Chinese, string Speaker);

public sealed class GameOcrClient : IDisposable
{
    private Process? _process;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _error = "";
    public async Task StartAsync(CancellationToken token)
    {
        if (_process is not null) return;
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "Resources", "ocr_worker.ps1") }) info.ArgumentList.Add(arg);
        _process = new Process { StartInfo = info };
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _error = e.Data; };
        try
        {
            _process.Start(); _process.BeginErrorReadLine();
            var ready = await ReadAsync(token);
            if (!ready.TryGetProperty("ready", out _)) throw new IOException("OCR 未就绪");
        }
        catch { Dispose(); throw; }
    }
    public async Task<GameOcrResult> ReadAsync(string ja, string zh, string? speaker, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await StartAsync(token);
            await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "ocr", japanese = ja, chinese = zh, speaker }));
            await _process.StandardInput.FlushAsync();
            var result = await ReadAsync(token);
            return new(result.GetProperty("japanese").GetString() ?? "", result.GetProperty("chinese").GetString() ?? "", result.GetProperty("speaker").GetString() ?? "");
        }
        catch { Dispose(); throw; }
        finally { _gate.Release(); }
    }
    private async Task<JsonElement> ReadAsync(CancellationToken token)
    {
        var line = await _process!.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20), token);
        if (line is null) throw new IOException("Windows OCR 已退出：" + _error);
        using var document = JsonDocument.Parse(line);
        var result = document.RootElement.Clone();
        if (result.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
        return result;
    }
    public void Dispose()
    {
        if (_process is null) return;
        try { if (!_process.HasExited) _process.Kill(true); } catch { }
        _process.Dispose(); _process = null;
    }
}
