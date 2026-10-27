using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinxLauncher.Models;

namespace LinxLauncher.Services;

/// <summary>
/// 加载器列表接口的 DTO。
/// 注意：必须是 internal/public 的顶层类型 —— 源生成器不给 private 嵌套类型生成元数据，
/// 而 AOT 下没有反射兜底，之前 Quilt 列表就是这样在运行时炸掉的
/// （Deserialization of types without a parameterless constructor…）。
/// </summary>
internal sealed class MetaEntry
{
    public string Version { get; set; } = "";
    public bool Stable { get; set; }
}

internal sealed class ForgeEntry
{
    public string Version { get; set; } = "";
    public string Mcversion { get; set; } = "";
    public long Build { get; set; }
    public List<ForgeFile>? Files { get; set; }
}

internal sealed class ForgeFile
{
    public string? Format { get; set; }
    public string? Category { get; set; }
}

internal sealed class NeoEntry
{
    public string Version { get; set; } = "";
    public string Mcversion { get; set; } = "";
    public string? InstallerPath { get; set; }
    public string? RawVersion { get; set; }
}

public sealed class LoaderService
{
    // ---------- 列表 ----------
    public async Task<List<LoaderVersion>> GetAvailableAsync(LoaderKind kind, string gameVersion, CancellationToken ct)
    {
        var list = new List<LoaderVersion>();

        switch (kind)
        {
            case LoaderKind.Fabric:
            {
                var arr = await Http.GetJsonAsync<List<MetaEntry>>(Endpoints.FabricLoaderList, ct) ?? new();
                foreach (var e in arr)
                    list.Add(new LoaderVersion
                    {
                        Name = e.Version,
                        LoaderKind = "fabric",
                        GameVersion = gameVersion,
                        IsStable = e.Stable
                    });
                break;
            }
            case LoaderKind.Quilt:
            {
                var arr = await Http.GetJsonAsync<List<MetaEntry>>($"{Endpoints.Bmcl}/quilt-meta/v3/versions/loader", ct) ?? new();
                foreach (var e in arr)
                    list.Add(new LoaderVersion
                    {
                        Name = e.Version,
                        LoaderKind = "quilt",
                        GameVersion = gameVersion,
                        IsStable = e.Stable
                    });
                break;
            }
            case LoaderKind.Forge:
            {
                var arr = await Http.GetJsonAsync<List<ForgeEntry>>(Endpoints.ForgeVersionList(gameVersion), ct) ?? new();
                foreach (var e in arr)
                {
                    bool hasInstaller = e.Files?.Any(f => string.Equals(f.Category, "installer", StringComparison.OrdinalIgnoreCase)) ?? true;
                    list.Add(new LoaderVersion
                    {
                        Name = e.Version,
                        LoaderKind = "forge",
                        GameVersion = e.Mcversion.Length > 0 ? e.Mcversion : gameVersion,
                        IsStable = true,
                        InstallerUrl = hasInstaller
                            ? $"{Endpoints.Bmcl}/maven/net/minecraftforge/forge/{gameVersion}-{e.Version}/forge-{gameVersion}-{e.Version}-installer.jar"
                            : null,
                        Description = e.Version.Contains("beta", StringComparison.OrdinalIgnoreCase) ? "测试版" : null
                    });
                }
                break;
            }
            case LoaderKind.NeoForge:
            {
                var arr = await Http.GetJsonAsync<List<NeoEntry>>($"{Endpoints.Bmcl}/neoforge/list/{gameVersion}", ct) ?? new();
                foreach (var e in arr)
                    list.Add(new LoaderVersion
                    {
                        Name = e.Version,
                        LoaderKind = "neoforge",
                        GameVersion = e.Mcversion.Length > 0 ? e.Mcversion : gameVersion,
                        IsStable = !e.Version.Contains("beta", StringComparison.OrdinalIgnoreCase),
                        InstallerUrl = e.InstallerPath is null ? null : Endpoints.Bmcl + e.InstallerPath
                    });
                break;
            }
        }

        // 稳定版在前，版本号倒序
        return list
            .OrderByDescending(v => v.IsStable)
            .ThenByDescending(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------- 安装：Fabric / Quilt（纯文件，无需运行器） ----------
    public async Task<string> InstallFabricLikeAsync(
        LoaderKind kind, string game, string loader, IProgress<InstallProgress>? p, CancellationToken ct)
    {
        Paths.EnsureCore();
        p?.Report(new InstallProgress { Stage = "配置", Indeterminate = true, Message = "生成加载器配置…" });

        var url = kind == LoaderKind.Quilt
            ? Endpoints.QuiltProfile(game, loader)
            : Endpoints.FabricProfile(game, loader);

        var json = await Http.GetStringAsync(url, ct);
        var detail = Json.Parse<VersionDetail>(json)
                     ?? throw new IOException("加载器配置解析失败");

        var id = !string.IsNullOrEmpty(detail.Id)
            ? detail.Id
            : $"{(kind == LoaderKind.Quilt ? "quilt-loader" : "fabric-loader")}-{loader}-{game}";

        var dir = Paths.VersionDir(id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, id + ".json"), json, ct);

        var items = GameInstaller.BuildLibraryItems(detail, id);
        var total = items.Sum(i => Math.Max(0, i.Size));

        p?.Report(new InstallProgress
        {
            Stage = "依赖库",
            TotalBytes = total,
            TotalFiles = items.Count,
            Message = $"{items.Count} 个文件"
        });

        var prog = new Progress<DownloadTick>(t => p?.Report(new InstallProgress
        {
            Stage = string.IsNullOrEmpty(t.Stage) ? "依赖库" : t.Stage,
            CurrentFile = t.CurrentFile,
            DoneBytes = t.DoneBytes,
            TotalBytes = Math.Max(t.TotalBytes, 1),
            DoneFiles = t.DoneFiles,
            TotalFiles = t.TotalFiles
        }));

        await new BatchDownloader { Parallelism = 8 }.RunAsync(items, prog, ct);

        p?.Report(new InstallProgress { Stage = "完成", DoneBytes = 1, TotalBytes = 1, Message = "安装完成" });
        return id;
    }

    // ---------- 安装：Forge / NeoForge（运行官方 installer） ----------
    public async Task<string> InstallForgeLikeAsync(
        LoaderKind kind, string game, string loaderVersion, string? installerUrl,
        IProgress<InstallProgress>? p, CancellationToken ct)
    {
        Paths.EnsureCore();

        var fileName = kind == LoaderKind.NeoForge
            ? $"neoforge-{loaderVersion}-installer.jar"
            : $"forge-{game}-{loaderVersion}-installer.jar";

        var cache = Path.Combine(Paths.CacheDir, fileName);
        installerUrl ??= kind == LoaderKind.NeoForge
            ? $"{Endpoints.Bmcl}/maven/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar"
            : $"{Endpoints.Bmcl}/maven/net/minecraftforge/forge/{game}-{loaderVersion}/forge-{game}-{loaderVersion}-installer.jar";

        Directory.CreateDirectory(Paths.CacheDir);

        if (!File.Exists(cache) || new FileInfo(cache).Length < 4096)
        {
            p?.Report(new InstallProgress
            {
                Stage = "安装器",
                CurrentFile = fileName,
                Indeterminate = true,
                Message = "下载安装器…"
            });
            await Http.DownloadToFileAsync(installerUrl, cache, null, ct);
        }

        return await RunInstallerAsync(cache, kind, p, ct);
    }

    private async Task<string> RunInstallerAsync(string jarPath, LoaderKind kind, IProgress<InstallProgress>? p, CancellationToken ct)
    {
        var java = JavaService.Pick(21) ?? JavaService.Detect().FirstOrDefault();
        if (java is null)
            throw new IOException("未检测到 Java 运行时，无法运行安装器。请先在「设置」里指定 Java 路径。");

        var console = Path.Combine(java.Home, "bin", "java.exe");
        if (!File.Exists(console)) console = java.Exe;

        p?.Report(new InstallProgress
        {
            Stage = "安装器",
            Indeterminate = true,
            Message = $"正在调用 Java {java.Major} 运行安装器（需要联网补全依赖，请稍候）…"
        });

        var psi = new ProcessStartInfo(console)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Paths.GameDir
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(jarPath);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(Paths.GameDir);

        var tail = new Queue<string>();
        var gate = new object();

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => Push(tail, gate, e.Data);
        proc.ErrorDataReceived += (_, e) => Push(tail, gate, e.Data);

        if (!proc.Start()) throw new IOException("安装器启动失败");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var ticker = Task.Run(async () =>
        {
            while (!proc.HasExited)
            {
                ct.ThrowIfCancellationRequested();
                string last;
                lock (gate) last = tail.Count > 0 ? tail.Last() : "安装器运行中…";
                p?.Report(new InstallProgress { Stage = "安装器", Indeterminate = true, Message = last });
                try { await Task.Delay(700, ct); } catch { break; }
            }
        }, ct);

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw;
        }

        if (proc.ExitCode != 0)
        {
            string detailText;
            lock (gate) detailText = string.Join("\n", tail);
            throw new IOException($"安装器退出码 {proc.ExitCode}\n{detailText}");
        }

        await Task.WhenAny(ticker, Task.Delay(200, ct));

        // 找出刚装好的版本目录
        var prefix = kind == LoaderKind.NeoForge ? "neoforge" : "forge";
        var installed = Directory.Exists(Paths.VersionsDir)
            ? Directory.GetDirectories(Paths.VersionsDir)
                .Select(Path.GetFileName)
                .Where(n => n is not null && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => Directory.GetLastWriteTime(Path.Combine(Paths.VersionsDir, n!)))
                .FirstOrDefault()
            : null;

        if (installed is null)
            throw new IOException("安装器已结束，但没有找到生成的版本目录。请检查日志。");

        p?.Report(new InstallProgress { Stage = "完成", DoneBytes = 1, TotalBytes = 1, Message = "安装完成" });
        return installed!;
    }

    private static void Push(Queue<string> tail, object gate, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (gate)
        {
            tail.Enqueue(line.Trim());
            while (tail.Count > 12) tail.Dequeue();
        }
        Log.Info("[installer] " + line.Trim());
    }
}
