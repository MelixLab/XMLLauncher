using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>
/// 单文件多线程下载：把文件切成若干块，用 Range 请求并发拉，最后合并。
///
/// IDM 那类工具的核心就是这件事——一个 500 MB 的包，单线程只能吃满
/// 一条连接的速度；切成 8 块并发，通常能接近带宽上限。
///
/// 约束：
/// - 服务器必须支持 Range（返回 206）。不支持就退回单线程，不能硬拆。
/// - 拿不到 Content-Length 也不拆，否则块边界算不出来。
/// - 分块只在 .part 临时目录里拼，全下完才合并成最终文件，中途取消不留半成品。
/// </summary>
public static class ChunkedDownloader
{
    /// <summary>低于这个大小的文件不值得拆（拆分开销比收益大）。</summary>
    private const long MinChunkedSize = 1024 * 1024; // 1 MB

    /// <summary>单块最小字节数，避免切成几千个碎块把服务器打爆。</summary>
    private const long MinChunkBytes = 512 * 1024; // 512 KB

    private const int BufferSize = 65536;

    /// <summary>
    /// 下载一个文件。返回 (是否成功, 实际字节数, 说明)。
    /// <paramref name="chunks"/> 是期望的分块数；服务器不支持或文件太小时会自动降级。
    /// </summary>
    public static async Task<(bool ok, long bytes, string note)> DownloadAsync(
        string url,
        string dest,
        int chunks,
        Action<long>? onBytes,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // ---- 1. 探一下：能不能分块，总多大 ----
        long total = 0;
        bool rangeOk = false;

        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, url);
            probe.Headers.Range = new RangeHeaderValue(0, 0);

            using var resp = await Http.Client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                rangeOk = true;
                // Content-Range: bytes 0-0/12345
                total = resp.Content.Headers.ContentRange?.Length ?? 0;
            }
            else if (resp.StatusCode == System.Net.HttpStatusCode.OK)
            {
                // 服务器忽略 Range，整份都回来了
                total = resp.Content.Headers.ContentLength ?? 0;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, 0, "探测地址失败：" + ex.Message);
        }

        // ---- 2. 不能分块就走单线程 ----
        if (!rangeOk || total <= 0 || total < MinChunkedSize)
        {
            try
            {
                await Http.DownloadToFileAsync(url, dest, onBytes, ct);
                var size = File.Exists(dest) ? new FileInfo(dest).Length : 0;
                return (true, size, rangeOk ? "" : "服务器不支持分段，已用单线程下载");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, 0, ex.Message);
            }
        }

        // ---- 3. 分块并发 ----
        int want = Math.Max(1, Math.Min(chunks, (int)(total / MinChunkBytes)));
        long chunkSize = total / want;

        var partDir = dest + ".parts";
        try { if (Directory.Exists(partDir)) Directory.Delete(partDir, true); } catch { }
        Directory.CreateDirectory(partDir);

        long done = 0;
        var errors = new List<Exception>();
        var errLock = new object();

        using var gate = new SemaphoreSlim(Math.Max(1, want));

        var tasks = Enumerable.Range(0, want).Select(async i =>
        {
            long start = (long)i * chunkSize;
            long end = i == want - 1 ? total - 1 : start + chunkSize - 1;
            var partPath = Path.Combine(partDir, $"part{i:D3}");

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start, end);

                using var resp = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    // 探测时支持、真正下的时候不支持，只能整体回退
                    throw new IOException($"第 {i + 1} 块没有拿到分段响应");
                }

                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);

                var buffer = new byte[BufferSize];
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    var n = Interlocked.Add(ref done, read);
                    onBytes?.Invoke(read);
                }
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
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            try { Directory.Delete(partDir, true); } catch { }
            throw;
        }

        if (errors.Count > 0)
        {
            try { Directory.Delete(partDir, true); } catch { }
            return (false, 0, errors[0].Message);
        }

        // ---- 4. 合并 ----
        try
        {
            var tmp = dest + ".part";
            await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
            {
                for (int i = 0; i < want; i++)
                {
                    var partPath = Path.Combine(partDir, $"part{i:D3}");
                    if (!File.Exists(partPath)) throw new IOException($"第 {i + 1} 块缺失，下载不完整");

                    await using var part = File.OpenRead(partPath);
                    await part.CopyToAsync(output, ct);
                }
            }

            var actual = new FileInfo(tmp).Length;
            if (actual != total)
                return (false, actual, $"大小不符：期望 {total}，实际 {actual}");

            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
            Directory.Delete(partDir, true);

            return (true, actual, "");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }
}
