using System;
using System.IO;

namespace LinxLauncher.Services;

/// <summary>简单文件日志，出问题时先看 %APPDATA%\LinxLauncher\launcher.log</summary>
public static class Log
{
    private static readonly object Gate = new();

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LinxLauncher");

    public static string LogPath => Path.Combine(Dir, "launcher.log");

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string tag, Exception? ex) => Write("ERROR", $"{tag}: {ex}");
    public static void Fatal(string tag, Exception? ex) => Write("FATAL", $"{tag}: {ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var file = LogPath;
                if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024)
                    File.Delete(file);
                File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss.fff}] {level} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
