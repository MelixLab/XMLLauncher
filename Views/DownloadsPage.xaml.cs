using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using LinxLauncher.Services;

namespace LinxLauncher.Views;

public sealed partial class DownloadsPage : Page
{
    public DownloadsPage()
    {
        InitializeComponent();
        AppState.Downloads.ActiveChanged += OnActiveChanged;
        Loaded += (_, _) => Refresh();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh();
    }

    private void OnActiveChanged() => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        var active = AppState.Downloads.Active;

        if (active is null)
        {
            ActivePanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ActivePanel.Visibility = Visibility.Visible;
            EmptyPanel.Visibility = Visibility.Collapsed;
            JobTitle.Text = active.Title;
            Steps.Job = active;
        }

        var history = AppState.Downloads.Jobs.Where(j => !ReferenceEquals(j, active)).ToList();
        UiList.Fill(HistoryList, history);
        HistoryTitle.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = AppState.Downloads.Jobs.Any(j => j.IsFinished)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        AppState.Downloads.ClearFinished();
        Refresh();
    }

    private void GoVersions_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("versions");
}
