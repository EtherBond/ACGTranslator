using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ACGTranslator.Services;

// One long-lived Python worker speaking JSON lines over stdin/stdout. Requests are serialized;
// any transport failure kills the process so the next request starts a fresh one.
public abstract class WorkerProcess : IDisposable
{
    private const int StderrLines = 20;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Queue<string> _stderr = new();
    private Process? _process;
    private bool _disposed;

    protected WorkerProcess(string pythonExecutable) => PythonExecutable = pythonExecutable;

    public string PythonExecutable { get; }
    protected abstract string Name { get; }
    protected abstract string ScriptName { get; }
    protected virtual IEnumerable<string> Arguments => [];
    protected virtual TimeSpan StartupTimeout => TimeSpan.FromMinutes(3);
    protected virtual TimeSpan RequestTimeout => TimeSpan.FromSeconds(90);
    private string ScriptPath => Path.Combine(AppContext.BaseDirectory, "Resources", ScriptName);

    // Throws when local prerequisites such as model folders are missing.
    protected virtual void Validate() { }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try { await EnsureStartedAsync(cancellationToken); }
        finally { _lock.Release(); }
    }

    protected async Task<JsonElement> SendAsync(object request, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var process = await EnsureStartedAsync(cancellationToken);
            JsonElement response;
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
                await process.StandardInput.FlushAsync();
                response = await ReadMessageAsync(process, RequestTimeout, cancellationToken);
            }
            catch
            {
                // The pipe is now out of step with the worker.
                Kill();
                throw;
            }
            if (response.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            return response;
        }
        finally { _lock.Release(); }
    }

    private async Task<Process> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is { HasExited: false }) return _process;
        Kill();
        Validate();
        if (!File.Exists(ScriptPath)) throw new FileNotFoundException($"找不到{Name}脚本", ScriptPath);
        var info = new ProcessStartInfo
        {
            FileName = PythonExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = Path.GetDirectoryName(ScriptPath)!
        };
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add(ScriptPath);
        foreach (var argument in Arguments) info.ArgumentList.Add(argument);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        // Runtime must never reach the network; models are provisioned by scripts/prepare_models.py.
        info.Environment["HF_HUB_OFFLINE"] = "1";
        lock (_stderr) _stderr.Clear();
        var process = new Process { StartInfo = info };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (_stderr)
            {
                _stderr.Enqueue(e.Data);
                while (_stderr.Count > StderrLines) _stderr.Dequeue();
            }
        };
        try { process.Start(); }
        catch (Win32Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException($"无法启动 Python：{PythonExecutable}（{ex.Message}）", ex);
        }
        _process = process;
        // Draining stderr keeps library warnings from filling the pipe and blocking the worker.
        process.BeginErrorReadLine();
        try
        {
            var ready = await ReadMessageAsync(process, StartupTimeout, cancellationToken);
            if (ready.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
        }
        catch
        {
            Kill();
            throw;
        }
        return process;
    }

    private async Task<JsonElement> ReadMessageAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string? line;
        try { line = await process.StandardOutput.ReadLineAsync().WaitAsync(timeout, cancellationToken); }
        catch (TimeoutException) { throw new TimeoutException($"{Name}在 {timeout.TotalSeconds:0} 秒内没有响应"); }
        if (line is null)
        {
            // Give the stderr reader a moment to collect the traceback.
            process.WaitForExit(500);
            throw new InvalidOperationException($"{Name}意外退出：{LastError()}");
        }
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private string LastError()
    {
        lock (_stderr) return _stderr.Count == 0 ? "没有错误输出" : _stderr.Last();
    }

    private void Kill()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_process is { HasExited: false } process)
        {
            try
            {
                process.StandardInput.WriteLine("{\"type\":\"shutdown\"}");
                process.StandardInput.Flush();
                process.WaitForExit(1500);
            }
            catch { }
        }
        Kill();
        GC.SuppressFinalize(this);
    }
}
