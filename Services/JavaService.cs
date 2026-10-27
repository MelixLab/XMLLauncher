using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LinxLauncher.Services;

public sealed class JavaInfo
{
    public string Home { get; set; } = "";
    public string Exe { get; set; } = "";
    public int Major { get; set; }
    public string Version { get; set; } = "";
    public string Arch { get; set; } = "";
    public bool Is64 { get; set; }

    public string Label => $"Java {Major} · {Arch} · {Short(Home)}";

    private static string Short(string p)
    {
        try
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (p.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) return "…" + p[pf.Length..];
        }
        catch { }
        return p;
    }

    public override string ToString() => Label;
}

public static class JavaService
{
    private static List<JavaInfo>? _cache;

    public static List<JavaInfo> Detect(bool force = false)
    {
        if (_cache is not null && !force) return _cache;

        var found = new Dictionary<string, JavaInfo>(StringComparer.OrdinalIgnoreCase);

        void Add(string? home)
        {
            if (string.IsNullOrWhiteSpace(home)) return;
            try
            {
                home = home.TrimEnd('"', '\\', '/');
                var exe = Path.Combine(home, "bin", "javaw.exe");
                var exeConsole = Path.Combine(home, "bin", "java.exe");
                if (!File.Exists(exe) && !File.Exists(exeConsole)) return;

                var key = Path.GetFullPath(home);
                if (found.ContainsKey(key)) return;

                var info = Probe(Path.GetFullPath(home), File.Exists(exe) ? exe : exeConsole);
                if (info is not null) found[key] = info;
            }
            catch { }
        }

        // 1) 启动器自管理运行时
        try
        {
            if (Directory.Exists(Paths.RuntimeDir))
                foreach (var d in Directory.GetDirectories(Paths.RuntimeDir))
                    Add(d);
        }
        catch { }

        // 2) JAVA_HOME
        Add(Environment.GetEnvironmentVariable("JAVA_HOME"));

        // 3) PATH 里的 java
        try
        {
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                var exe = Path.Combine(p.Trim(), "javaw.exe");
                if (File.Exists(exe))
                    Add(Path.GetDirectoryName(p.Trim())!);
            }
        }
        catch { }

        // 4) 常见安装目录
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Foundation"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BellSoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Semeru"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"),
            Path.Combine(Paths.GameDir, "runtime"),
        };

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var d in Directory.GetDirectories(root))
                {
                    Add(d);
                    // 运行时目录常见两层：runtime/java-runtime-gamma/windows-x64/java-runtime-gamma
                    foreach (var nested in SafeDirs(d))
                    {
                        Add(nested);
                        foreach (var deep in SafeDirs(nested)) Add(deep);
                    }
                }
            }
            catch { }
        }

        _cache = found.Values.OrderByDescending(j => j.Major).ThenBy(j => j.Home).ToList();
        return _cache;
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static JavaInfo? Probe(string home, string exe)
    {
        string version = "";
        string arch = "";

        var release = Path.Combine(home, "release");
        if (File.Exists(release))
        {
            try
            {
                foreach (var line in File.ReadAllLines(release))
                {
                    if (line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
                        version = line["JAVA_VERSION=".Length..].Trim('"', ' ');
                    else if (line.StartsWith("OS_ARCH=", StringComparison.Ordinal))
                        arch = line["OS_ARCH=".Length..].Trim('"', ' ');
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(version)) return null; // 不跑进程，探测不到就跳过

        var major = ParseMajor(version);
        if (major <= 0) return null;

        return new JavaInfo
        {
            Home = home,
            Exe = exe,
            Major = major,
            Version = version,
            Arch = arch.Length > 0 ? arch : (Environment.Is64BitOperatingSystem ? "x64" : "x86"),
            Is64 = !arch.Contains("86", StringComparison.Ordinal) || arch.Contains("64", StringComparison.Ordinal)
        };
    }

    private static int ParseMajor(string v)
    {
        if (v.StartsWith("1."))
        {
            var rest = v[2..];
            var dot = rest.IndexOf('.');
            return int.TryParse(dot > 0 ? rest[..dot] : rest, out var m8) ? m8 : 0;
        }
        var m = Regex.Match(v, @"^(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>为所需主版本挑一个：优先精确匹配，其次更高版本（向下兼容）</summary>
    public static JavaInfo? Pick(int requiredMajor, string? preferredHome = null)
    {
        var all = Detect();
        if (all.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredHome))
        {
            var pref = all.FirstOrDefault(j => j.Home.Equals(preferredHome, StringComparison.OrdinalIgnoreCase));
            if (pref is not null) return pref;
        }

        if (requiredMajor <= 0) return all.FirstOrDefault(j => j.Major >= 17) ?? all[0];

        var exact = all.FirstOrDefault(j => j.Major == requiredMajor);
        if (exact is not null) return exact;

        var higher = all.Where(j => j.Major > requiredMajor).OrderBy(j => j.Major).FirstOrDefault();
        if (higher is not null) return higher;

        // 达不到要求的版本就返回 null：宁可让上层明确提示"需要 Java N"，
        // 也不要拿低版本硬启动，最后死在 UnsupportedClassVersionError 上。
        return null;
    }
}
