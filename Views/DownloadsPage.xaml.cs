using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LinxLauncher.Services;

namespace LinxLauncher.Views;

public sealed partial class DownloadsPage : Page
{
    private bool _isLoaded;
    private int _refreshQueued;

    public DownloadsPage()
    {
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) return;
        _isLoaded = true;
        AppState.Downloads.ActiveChanged += OnActiveChanged;
        Refresh();
        FontService.Apply(this);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        AppState.Downloads.ActiveChanged -= OnActiveChanged;
    }

    private void OnActiveChanged()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_isLoaded) Refresh();
        })) Interlocked.Exchange(ref _refreshQueued, 0);
    }

    private void Refresh()
    {
        var running = AppState.Downloads.Jobs.Where(job => job.IsRunning).ToList();
        var history = AppState.Downloads.Jobs.Where(job => job.IsFinished).ToList();

        UiList.Fill(RunningList, running);
        UiList.Fill(HistoryList, history);
        RunningTitle.Text = $"正在进行（{running.Count}）";
        RunningPanel.Visibility = running.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = running.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ContentGrid is not null)
            ContentGrid.Padding = e.NewSize.Width < 720
                ? new Thickness(16, 20, 16, 24)
                : new Thickness(32, 28, 32, 24);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => AppState.Downloads.ClearFinished();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DownloadJob job }) job.Cancel();
    }

    private void GoVersions_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("versions");
}
