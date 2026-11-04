using LinxLauncher.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LinxLauncher.Views;

public sealed partial class SeedMapPage : Page
{
    private SeedMapWorkbench? _workbench;
    private Button? _activateWindow;
    private bool _navigated;
    public SeedMapWorkbench? Workbench => Host.Content as SeedMapWorkbench;
    public SeedMapPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (_navigated) ShowMap(); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _navigated = true;
        SeedMapWindow.StateChanged += ShowMap;
        DispatcherQueue.TryEnqueue(ShowMap);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _navigated = false;
        SeedMapWindow.StateChanged -= ShowMap;
        if (Host.Content is SeedMapWorkbench map) map.Stop();
        base.OnNavigatedFrom(e);
    }

    private void ShowMap()
    {
        if (!_navigated || !IsLoaded) return;
        if (SeedMapWindow.Current is not null)
        {
            if (_activateWindow is null)
            {
                _activateWindow = new Button { Content = "切换到地图窗口", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                _activateWindow.Click += (_, _) => SeedMapWindow.Current?.Activate();
            }
            _workbench?.Stop();
            Host.Content = _activateWindow;
        }
        else if (Host.Content is not SeedMapWorkbench)
        {
            if (_workbench is null)
            {
                _workbench = new SeedMapWorkbench { OwnerWindow = MainWindow.Instance };
                _workbench.DetachRequested += Detach;
            }
            Host.Content = _workbench;
            _workbench.Resume();
        }
        else if (Host.Content is SeedMapWorkbench existing) existing.Resume();
    }

    private void Detach()
    {
        if (Host.Content is not SeedMapWorkbench map) return;
        map.Stop();
        Host.Content = null;
        // XAML controls retain root-specific input/composition state. Share the
        // component implementation and session, create a fresh tree per window.
        SeedMapWindow.Open(new SeedMapWorkbench());
        ShowMap();
    }
    private void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("tools");
}
