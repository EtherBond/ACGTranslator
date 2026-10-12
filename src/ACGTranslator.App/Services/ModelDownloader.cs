using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ACGTranslator.Models;

namespace ACGTranslator.Services;

public sealed record DownloadProgress(string Stage, long Received, long Total, double BytesPerSecond);

// Downloads catalog items with mirror fallback, resume and hash verification. Files are staged
// under models/.downloads/<id>; the item directory is only replaced once every file verified, so a
// cancelled or failed download never leaves a half-installed model behind.
public sealed class ModelDownloader(ModelStore store, HttpClient? http = null)
{
    private const int AttemptsPerSource = 2;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private readonly HttpClient _http = http ?? CreateClient();

    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20), AutomaticDecompression = DecompressionMethods.None })
        {
            // Large files take many minutes; stalls are detected per read instead.
            Timeout = Timeout.InfiniteTimeSpan
        };
        // hf-mirror.com rejects requests without a browser-like User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ACGTranslator/0.3");
        return client;
    }

    public async Task InstallAsync(CatalogItem item, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var staging = store.GetStagingDirectory(item);
        Directory.CreateDirectory(staging);
        var meter = new ProgressMeter(item.TotalSize, progress);
        var downloaded = new List<(CatalogFile File, string Path)>();
        foreach (var file in item.Files)
        {
            var path = Path.Combine(staging, file.FileName);
            await DownloadFileAsync(file, path, meter, cancellationToken);
            meter.FileDone(file.Size);
            downloaded.Add((file, path));
        }

        meter.Report("正在安装", force: true);
        var target = store.GetDirectory(item);
        var installing = target + ".installing";
        if (Directory.Exists(installing)) Directory.Delete(installing, true);
        Directory.CreateDirectory(installing);
        foreach (var (file, path) in downloaded)
        {
            if (file.Extract == "zip") Extract(path, installing, file.Include);
            else File.Move(path, Path.Combine(installing, file.FileName));
        }
        if (item.IsArchive) ModelStore.WriteMarker(installing, item);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(installing, target);
        Directory.Delete(staging, true);
    }

    private async Task DownloadFileAsync(CatalogFile file, string path, ProgressMeter meter, CancellationToken cancellationToken)
    {
        // Completed files are only renamed from .part after verification.
        if (File.Exists(path) && new FileInfo(path).Length == file.Size) return;
        if (!store.Catalog.Sources.TryGetValue(file.Source, out var sources) || sources.Length == 0)
            throw new InvalidDataException($"模型目录中没有下载源“{file.Source}”");
        var part = path + ".part";
        Exception? last = null;
        var fetched = false;
        foreach (var baseUrl in sources)
        {
            for (var attempt = 0; attempt < AttemptsPerSource && !fetched; attempt++)
            {
                try
                {
                    await FetchAsync(new Uri($"{baseUrl.TrimEnd('/')}/{file.Path}"), part, file.Size, meter, cancellationToken);
                    fetched = true;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    last = ex;
                }
            }
            if (fetched) break;
        }
        if (!fetched) throw new HttpRequestException($"所有下载源都失败了：{last?.Message}", last);

        meter.Report("正在校验", force: true);
        if (!await VerifyAsync(part, file, cancellationToken))
        {
            File.Delete(part);
            throw new InvalidDataException($"{file.FileName} 校验失败，已删除损坏的文件，请重新下载");
        }
        File.Move(part, path, true);
    }

    private async Task FetchAsync(Uri uri, string part, long size, ProgressMeter meter, CancellationToken cancellationToken)
    {
        long received = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (received > size)
        {
            File.Delete(part);
            received = 0;
        }
        meter.FileReceived(received);
        if (received == size) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (received > 0) request.Headers.Range = new RangeHeaderValue(received, null);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(part);
            throw new IOException("服务器不接受续传位置，将重新下载");
        }
        response.EnsureSuccessStatusCode();
        // A server that ignores Range sends the whole file again.
        var append = received > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append) received = 0;

        await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
        await using var target = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                var read = await source.ReadAsync(buffer.AsMemory(), stall.Token);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                if (received > size) throw new IOException("下载的数据比预期多");
                meter.FileReceived(received);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        if (received != size) throw new IOException($"连接中断（已下载 {received}/{size} 字节）");
    }

    public static async Task<bool> VerifyAsync(string path, CatalogFile file, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != file.Size) return false;
        var expected = file.Sha256 ?? file.GitSha1 ?? throw new InvalidDataException($"{file.FileName} 没有校验值");
        using var hash = IncrementalHash.CreateHash(file.Sha256 is not null ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        // Git hashes the blob header followed by the content.
        if (file.Sha256 is null) hash.AppendData(Encoding.ASCII.GetBytes($"blob {info.Length}\0"));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0) hash.AppendData(buffer, 0, read);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Extract(string archive, string destination, string? include)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (include is not null)
            {
                if (!name.StartsWith(include, StringComparison.Ordinal)) continue;
                name = name[include.Length..];
            }
            if (name.Length == 0 || name.EndsWith('/')) continue;
            var output = Path.GetFullPath(Path.Combine(destination, name));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"压缩包中包含非法路径：{entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            entry.ExtractToFile(output, overwrite: true);
        }
    }

    private sealed class ProgressMeter(long total, IProgress<DownloadProgress>? progress)
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _completed, _current, _lastBytes;
        private TimeSpan _lastTime;
        private double _speed;

        private long Received => _completed + _current;

        public void FileReceived(long bytes)
        {
            _current = bytes;
            Report("正在下载");
        }

        public void FileDone(long size)
        {
            _completed += size;
            _current = 0;
        }

        public void Report(string stage, bool force = false)
        {
            var now = _clock.Elapsed;
            if (!force && now - _lastTime < Interval) return;
            var seconds = (now - _lastTime).TotalSeconds;
            if (seconds > 0 && Received >= _lastBytes)
            {
                var instant = (Received - _lastBytes) / seconds;
                _speed = _speed == 0 ? instant : _speed * 0.7 + instant * 0.3;
            }
            _lastTime = now;
            _lastBytes = Received;
            progress?.Report(new(stage, Received, total, stage == "正在下载" ? _speed : 0));
        }
    }
}
