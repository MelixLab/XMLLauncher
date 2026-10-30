using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LinxLauncher.Services;

namespace LinxLauncher.Views;

public sealed partial class SettingsAdvancedPage : Page
{
    private bool _loading = true;
    private string _savedJvmArgs = "";
    private string _savedGameArgs = "";

    public SettingsAdvancedPage()
    {
        InitializeComponent();
        var s = AppState.Settings;
        JvmArgsBox.Text = s.ExtraJvmArgs ?? "";
        GameArgsBox.Text = s.ExtraGameArgs ?? "";
        _savedJvmArgs = NormalizeArgs(s.ExtraJvmArgs);
        _savedGameArgs = NormalizeArgs(s.ExtraGameArgs);
        _loading = false;
        UpdateSaveButtons();
        var version = typeof(App).Assembly.GetName().Version;
        AboutText.Text = $"XML Minecraft Launcher {version?.ToString(3) ?? "1.0.0"} · WinUI 3\n配置目录：{Paths.AppData}";
    }

    private static string NormalizeArgs(string? text) => text?.Trim() ?? "";

    private void Args_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) UpdateSaveButtons();
    }

    private void UpdateSaveButtons()
    {
        JvmSaveButton.IsEnabled = NormalizeArgs(JvmArgsBox.Text) != _savedJvmArgs;
        GameSaveButton.IsEnabled = NormalizeArgs(GameArgsBox.Text) != _savedGameArgs;
    }

    private void SaveJvm_Click(object sender, RoutedEventArgs e) => SaveArgs(jvm: true);
    private void SaveGame_Click(object sender, RoutedEventArgs e) => SaveArgs(jvm: false);

    private void SaveArgs(bool jvm)
    {
        var settings = AppState.Settings;
        string text = NormalizeArgs(jvm ? JvmArgsBox.Text : GameArgsBox.Text);
        string? previous = jvm ? settings.ExtraJvmArgs : settings.ExtraGameArgs;
        string? value = text.Length == 0 ? null : text;
        if (jvm) settings.ExtraJvmArgs = value;
        else settings.ExtraGameArgs = value;

        if (!SettingsService.TrySave())
        {
            if (jvm) settings.ExtraJvmArgs = previous;
            else settings.ExtraGameArgs = previous;
            MainWindow.Instance?.Toast("保存失败，请检查配置目录权限后重试。", true);
            return;
        }
        if (jvm) _savedJvmArgs = text;
        else _savedGameArgs = text;
        UpdateSaveButtons();
        MainWindow.Instance?.Toast("已保存");
    }

    private void RestartWizard_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Instance?.ShowOnboarding(fromSettings: true);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(Log.LogPath)!;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.Toast("打开失败：" + ex.Message, true);
        }
    }
}
