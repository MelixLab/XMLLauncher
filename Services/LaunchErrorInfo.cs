using System;

namespace LinxLauncher.Services;

/// <summary>启动失败 / 游戏异常退出时给独立报错窗口用的信息。</summary>
public sealed class LaunchErrorInfo
{
    /// <summary>窗口标题，例如「启动失败」「游戏异常退出」。</summary>
    public string Title { get; init; } = "启动失败";

    /// <summary>一句话结论，直接展示在标题下面。</summary>
    public string Summary { get; init; } = "";

    /// <summary>从 crash-report / 日志里解析出来的具体崩溃原因（异常类名 + 消息 + 第一处 Caused by）。</summary>
    public string? CrashDetail { get; init; }

    /// <summary>可操作的建议（从日志里认出来的常见原因），没有就留空。</summary>
    public string? Advice { get; init; }

    public string? VersionId { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>本次游戏的日志文件（%APPDATA%\LinxLauncher\logs\game-*.log）。</summary>
    public string? LogFile { get; init; }

    /// <summary>日志尾部若干行，给用户直接看个大概。</summary>
    public string? LogTail { get; init; }

    public DateTime At { get; } = DateTime.Now;
}
