using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>
/// 自定义链接下载。只负责「地址 → 任务」的整理，
/// 真正的下载交给内置引擎（单文件分块并发），进度上报走 DownloadJob，
/// 和模组下载、游戏安装完全同一套，界面直接复用下载中心。
/// </summary>
public static class LinkDownloader
{
    /// <summary>一条任务：一个链接一个文件。</summary>
    public sealed record Job(string Url, string Dest, string Label, long Size);

    /// <summary>把链接按行拆开（也认逗号、空格、制表符分隔）。</summary>
    public static IEnumerable<string> SplitLinks(string? text) =>
        (text ?? "")
            .Split(['\r', '\n', ',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>把链接整理成任务：跳过无效地址、去重、按路径猜文件名。</summary>
    public static List<Job> BuildJobs(IEnumerable<string> rawLinks, string destDir)
    {
        var jobs = new List<Job>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in rawLinks)
        {
            var url = line.Trim();
            if (url.Length == 0) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme is not ("http" or "https")) continue;
            if (!seen.Add(url)) continue;

            var name = GuessFileName(uri);
            var dest = Path.Combine(destDir, name);

            // 同名文件加序号，避免后下的覆盖先下的
            var index = 2;
            while (jobs.Any(j => j.Dest.Equals(dest, StringComparison.OrdinalIgnoreCase)))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                dest = Path.Combine(destDir, $"{stem} ({index++}){ext}");
            }

            jobs.Add(new Job(url, dest, name, 0));
        }

        return jobs;
    }

    /// <summary>
    /// 从地址里猜文件名。带查询参数的（CDN 签名链接）只看路径部分，
    /// 猜不出来就退回随机名，保证磁盘上一定有个能用的名字。
    /// </summary>
    private static string GuessFileName(Uri uri)
    {
        var last = uri.AbsolutePath.TrimEnd('/');
        var name = last.Length > 0 ? last[(last.LastIndexOf('/') + 1)..] : "";

        try { name = Uri.UnescapeDataString(name); } catch { }

        if (name.Length == 0 || name.Contains('/') || name.Contains('\\'))
            return "download-" + Guid.NewGuid().ToString("N")[..8];

        foreach (var bad in new[] { '<', '>', ':', '"', '|', '?', '*' })
            name = name.Replace(bad, '_');

        return name;
    }

    /// <summary>
    /// 把一批链接下载到目录，进度直接写进 <paramref name="job"/>（下载中心的那个任务）。
    ///
    /// 单个文件分块并发，多个文件按线程数排队；两者都吃满用户设的线程数。
    /// 返回成功与失败数量。
    /// </summary>
    public static async Task<(int ok, int failed, string error)> RunAsync(
        IReadOnlyList<Job> jobs,
        int threads,
        DownloadJob job,
        CancellationToken ct)
    {
        if (jobs.Count == 0) return (0, 0, "没有可下载的链接");

        var threadCount = Math.Clamp(threads, 1, 32);
        var failed = new List<string>();
        long doneBytes = 0;
        int doneFiles = 0;
        long totalBytes = 0;

        // ---- 预探测大小，进度条才有分母 ----
        job.Apply(new InstallProgress
        {
            Stage = "准备",
            Indeterminate = true,
            Message = "正在查询文件大小…",
        });

        foreach (var item in jobs)
        {
            try
            {
                using var probe = new HttpRequestMessage(HttpMethod.Head, item.Url);
                using var resp = await Http.Client.SendAsync(probe, ct);
                totalBytes += resp.Content.Headers.ContentLength ?? 0;
            }
            catch { /* 探不到就按已下载字节显示，不阻断 */ }
        }

        void Report(string message, string stage = "下载")
        {
            job.Apply(new InstallProgress
            {
                Stage = stage,
                Message = message,
                DoneBytes = doneBytes,
                TotalBytes = Math.Max(totalBytes, 1),
                DoneFiles = doneFiles,
                TotalFiles = jobs.Count,
            });
        }

        Report(jobs.Count == 1 ? jobs[0].Label : $"{jobs.Count} 个文件");

        // 文件级并发限制在线程数内；单文件内部还会再分块，块数同样按线程数给
        using var gate = new SemaphoreSlim(Math.Max(1, threadCount));

        var tasks = jobs.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                var (ok, bytes, note) = await ChunkedDownloader.DownloadAsync(
                    item.Url, item.Dest, threadCount,
                    n =>
                    {
                        Interlocked.Add(ref doneBytes, n);
                        Report(item.Label);
                    },
                    ct);

                if (ok)
                {
                    Interlocked.Increment(ref doneFiles);
                    Report(item.Label);
                }
                else
                {
                    lock (failed) failed.Add($"{item.Label}：{note}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (failed) failed.Add($"{item.Label}：{ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        return (doneFiles, jobs.Count - doneFiles,
            failed.Count > 0 ? string.Join("；", failed.Take(3)) : "");
    }
}
