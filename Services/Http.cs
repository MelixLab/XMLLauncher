using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(AppJsonContext.Default.Options)
    {
        // 源生成上下文优先（Native AOT 靠它）；没登记的类型再退回反射，
        // 这样非 AOT 版行为不变，AOT 版缺登记时会明确报错而不是静默写坏文件。
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            AppJsonContext.Default,
            new DefaultJsonTypeInfoResolver())
    };

    public static T? Parse<T>(string text) => JsonSerializer.Deserialize<T>(text, Options);
    public static string Write<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(Options) { WriteIndented = indented });
}

/// <summary>一个待下载的文件</summary>
public sealed class DownloadItem
{
    public string Url { get; init; } = "";
    public string Dest { get; init; } = "";
    /// <summary>期望字节数，0 表示未知</summary>
    public long Size { get; init; }
    public string? Sha1 { get; init; }
    /// <summary>展示用名称（相对路径）</summary>
    public string Label { get; init; } = "";
    /// <summary>所属阶段：客户端 / 依赖库 / 资源文件</summary>
    public string Stage { get; init; } = "";
    /// <summary>下载后解压到该目录（natives）</summary>
    public string? ExtractTo { get; init; }
    public List<string>? ExcludeFromExtract { get; init; }
}

public readonly record struct DownloadTick(
    long DoneBytes,
    long TotalBytes,
    int DoneFiles,
    int TotalFiles,
    string CurrentFile,
    string Stage);

public static class Http
{
    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxConnectionsPerServer = 64,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(6),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
        };

        // ★ 本机 IPv6 通不了，而 DNS 会同时返回 A/AAAA。.NET 默认挨个试，
        //   卡在 IPv6 上要等十几秒才失败 —— 客户端表现就是「登录超时 / A task was canceled」，
        //   但同一台机器用浏览器（自动回退 IPv4）却秒开。这里强制先走 IPv4。
        handler.ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var ordered = addresses
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .ToArray();

            Exception? last = null;
            foreach (var address in ordered)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    last = ex;
                    socket.Dispose();
                }
            }

            throw last ?? new IOException("连不上 " + context.DnsEndPoint);
        };

        HttpMessageHandler pipeline = handler;
#if DEBUG
        pipeline = new LocalDevelopmentHandler { InnerHandler = handler };
#endif

        var client = new HttpClient(pipeline)
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XMLLauncher/1.0 (+WinUI)");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

#if DEBUG
    private sealed class LocalDevelopmentHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // 在 DNS 之前拦住私有服务器请求，其他下载源仍走原来的连接和重试逻辑。
            if (RemoteService.IsLocalDevelopment
                && request.RequestUri is { } target
                && Uri.TryCreate(RemoteService.BaseUrl, UriKind.Absolute, out var server)
                && Uri.Compare(target, server, UriComponents.SchemeAndServer,
                    UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                    ReasonPhrase = "Private server disabled for local development",
                    Content = new StringContent(
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["error"] = "本地开发模式已停用私有服务器，请使用离线账号和本地功能。"
                        }.ToJsonString(),
                        System.Text.Encoding.UTF8, "application/json")
                });
            }

            return base.SendAsync(request, ct);
        }
    }
#endif

    /// <summary>GET 文本，自动走镜像并重试</summary>
    public static async Task<string> GetStringAsync(string url, CancellationToken ct, int retries = 3)
    {
        var target = Endpoints.Mirror(url);
        Exception? last = null;
        for (int attempt = 0; attempt < retries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await Client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                await Task.Delay(500 * (attempt + 1), ct);
            }
        }
        throw new IOException($"请求失败：{target}\n{last?.Message}", last);
    }

    public static async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        var text = await GetStringAsync(url, ct);
        return Json.Parse<T>(text);
    }

    /// <summary>
    /// 下载单个文件到 dest，onBytes 报告本次已写入字节。
    /// 先走镜像；镜像连不上/太慢就自动换回官方源 —— 之前只认镜像，
    /// 镜像一抽风整个安装就卡在某个文件上不动。
    /// </summary>
    public static async Task DownloadToFileAsync(string url, string dest, Action<long>? onBytes, CancellationToken ct, int retries = 4)
    {
        var targets = new List<string> { Endpoints.Mirror(url) };
        if (Endpoints.UseMirror && !string.Equals(targets[0], url, StringComparison.OrdinalIgnoreCase))
            targets.Add(url);

        Exception? last = null;

        for (int attempt = 0; attempt < retries; attempt++)
        {
            var target = targets[Math.Min(attempt, targets.Count - 1)];
            ct.ThrowIfCancellationRequested();
            try
            {
                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var tmp = dest + ".part";

                // 断点续传：上次留下的 .part 直接接着下，别再从头来一遍
                long have = 0;
                try { if (File.Exists(tmp)) have = new FileInfo(tmp).Length; } catch { have = 0; }

                using (var request = new HttpRequestMessage(HttpMethod.Get, target))
                {
                    if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);

                    using var resp = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                    // 服务器不认 Range（返回 200）就老实从头下
                    if (resp.StatusCode == HttpStatusCode.OK && have > 0)
                    {
                        have = 0;
                        try { File.Delete(tmp); } catch { }
                    }
                    else if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
                    {
                        // 已经下完了，只是没改名
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(tmp, dest);
                        return;
                    }

                    resp.EnsureSuccessStatusCode();

                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    await using var fs = new FileStream(tmp, have > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.None, 65536, true);

                    var buffer = new byte[65536];
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                        onBytes?.Invoke(read);
                    }
                }

                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                // ★ 不删 .part：留着下次接着下，几百 MB 的包断一次不至于白下
                await Task.Delay(250 * (attempt + 1), ct);
            }
        }

        throw new IOException($"下载失败：{Path.GetFileName(dest)}\n{last?.Message}", last);
    }

    public static string ComputeSha1(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}

/// <summary>并发批量下载器：去重、跳过已存在文件、实时字节进度</summary>
public sealed class BatchDownloader
{
    public int Parallelism { get; set; } = 8;
    /// <summary>已存在文件是否重新校验 SHA1</summary>
    public bool VerifyExisting { get; set; }

    public async Task RunAsync(
        IReadOnlyList<DownloadItem> items,
        IProgress<DownloadTick>? progress,
        CancellationToken ct,
        Action<string>? onFileDone = null)
    {
        // 去重：同一目标只下一次
        var unique = new List<DownloadItem>(items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            if (string.IsNullOrEmpty(it.Dest) || !seen.Add(it.Dest)) continue;
            unique.Add(it);
        }

        long totalBytes = 0;
        var todo = new List<DownloadItem>(unique.Count);

        foreach (var it in unique)
        {
            bool ok = false;
            try
            {
                var fi = new FileInfo(it.Dest);
                if (fi.Exists && (it.Size <= 0 || fi.Length == it.Size))
                {
                    ok = true;
                    if (VerifyExisting && !string.IsNullOrEmpty(it.Sha1) && fi.Length == it.Size)
                        ok = string.Equals(Http.ComputeSha1(it.Dest), it.Sha1, StringComparison.OrdinalIgnoreCase);
                }
                else if (fi.Exists)
                {
                    fi.Delete();
                }
            }
            catch { ok = false; }

            if (ok) continue;

            todo.Add(it);
            totalBytes += Math.Max(0, it.Size);
        }

        int totalFiles = todo.Count;
        int doneFiles = 0;
        long doneBytes = 0;
        string current = todo.Count > 0 ? todo[0].Label : "";
        string currentStage = todo.Count > 0 ? todo[0].Stage : "";

        var sw = Stopwatch.StartNew();
        long lastReport = 0;

        if (totalFiles == 0)
        {
            progress?.Report(new DownloadTick(0, 0, 0, 0, "", ""));
            return;
        }

        // 首帧立刻上报，让界面马上有反应
        progress?.Report(new DownloadTick(0, totalBytes, 0, totalFiles, current, currentStage));

        using var gate = new SemaphoreSlim(Math.Max(1, Parallelism));
        var errors = new List<Exception>();
        var errLock = new object();

        var tasks = todo.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                Volatile.Write(ref current, item.Label);
                Volatile.Write(ref currentStage, item.Stage);

                await Http.DownloadToFileAsync(item.Url, item.Dest, n =>
                {
                    Interlocked.Add(ref doneBytes, n);
                    var now = sw.ElapsedMilliseconds;
                    if (now - lastReport >= 90)
                    {
                        lastReport = now;
                        progress?.Report(new DownloadTick(
                            Interlocked.Read(ref doneBytes), totalBytes, Volatile.Read(ref doneFiles), totalFiles,
                            Volatile.Read(ref current), Volatile.Read(ref currentStage)));
                    }
                }, ct).ConfigureAwait(false);

                if (item.ExtractTo is not null)
                {
                    try
                    {
                        NativeExtractor.Extract(item.Dest, item.ExtractTo, item.ExcludeFromExtract);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"解压 natives 失败 {item.Dest}: {ex.Message}");
                    }
                }

                var done = Interlocked.Increment(ref doneFiles);
                onFileDone?.Invoke(item.Label);
                progress?.Report(new DownloadTick(Interlocked.Read(ref doneBytes), totalBytes, done, totalFiles, item.Label, item.Stage));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (errLock) errors.Add(ex);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (errors.Count > 0)
            throw new AggregateException($"{errors.Count} 个文件下载失败，首个错误：{errors[0].Message}", errors);

        progress?.Report(new DownloadTick(doneBytes, totalBytes, doneFiles, totalFiles, "", ""));
    }
}

public static class NativeExtractor
{
    public static void Extract(string zipPath, string targetDir, List<string>? excludes)
    {
        Directory.CreateDirectory(targetDir);
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // 目录
            if (excludes is not null)
            {
                bool skip = excludes.Any(e => e.EndsWith("/") && entry.FullName.StartsWith(e, StringComparison.OrdinalIgnoreCase));
                if (skip) continue;
            }
            var name = Path.GetFileName(entry.FullName);
            if (name.Contains("META-INF", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(targetDir, name);
            if (File.Exists(dest)) continue;
            entry.ExtractToFile(dest, true);
        }
    }
}
