using System.Runtime.InteropServices;
using LinxLauncher.Controls;
using LinxLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace LinxLauncher.Views;

/// <summary>One native resizable window, hosting the same workbench and light session.</summary>
public sealed partial class SeedMapWindow : Window
{
    public new static SeedMapWindow? Current { get; private set; }
    public static event Action? StateChanged;
    private readonly SeedMapWorkbench _map;
    private readonly Grid _root;
    private readonly TitleBar _titleBar;
    private WindowBackdrop? _backdrop;
    private ThemeMaterial? _material;
    private bool _sized;
    private bool _closing;
    private bool _nativeClose;
    private bool _returnToLauncher;
    private bool _titleColorsQueued;
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private SeedMapWindow(SeedMapWorkbench map)
    {
        Trace("construct");
        Title = "种子地图 · XML Launcher";
        _map = map;
        _root = new Grid();
        _titleBar = new TitleBar
        {
            Title = "种子地图", Height = 48, MinHeight = 48,
            RequestedTheme = ElementTheme.Dark,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsBackButtonVisible = false, IsPaneToggleButtonVisible = false,
            LeftHeader = new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")), Width = 20, Height = 20, Margin = new Thickness(16, 0, 8, 0) }
        };
        _titleBar.Resources["TitleBarCompactHeight"] = 48.0;
        _titleBar.Resources["TitleBarExpandedHeight"] = 48.0;
        _titleBar.Resources["TitleBarDeactivatedOpacity"] = 1.0;
        _titleBar.Resources["TitleBarLeftPaddingWidth"] = 0.0;
        _titleBar.Resources["TitleBarLeftHeaderPaddingWidth"] = 0.0;
        _titleBar.Resources["TitleBarMinDragRegionWidth"] = 0.0;
        _titleBar.Resources["TitleBarRightHeaderVerticalAlignment"] = VerticalAlignment.Stretch;
        map.Margin = new Thickness(0);
        _root.Children.Add(map);
        _root.Children.Add(_titleBar);
        map.OwnerWindow = this;
        Content = _root;
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        SetTitleBar(_titleBar);
        _titleBar.SizeChanged += TitleBar_SizeChanged;
        _titleBar.Loaded += TitleBar_Loaded;
        _titleBar.ActualThemeChanged += TitleBar_ActualThemeChanged;
        Trace("content attached");
        WindowIcon.Apply(this);
        ThemeService.AppearanceChanged += ApplyAppearance;
        ApplyAppearance();
        Trace("appearance applied");
        Activated += Window_Activated;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true; presenter.IsMaximizable = true; presenter.IsMinimizable = true;
            presenter.PreferredMinimumWidth = 480; presenter.PreferredMinimumHeight = 560;
        }
        AppWindow.Closing += AppWindow_Closing;
        Closed += Window_Closed;
        Trace("constructed");
    }
    private void TitleBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_closing) return;
        _titleBar.RecomputeDragRegions();
    }
    private void TitleBar_Loaded(object sender, RoutedEventArgs e) => QueueTitleBarColors();
    private void TitleBar_ActualThemeChanged(FrameworkElement sender, object args) => QueueTitleBarColors();

    private void QueueTitleBarColors()
    {
        if (_closing || _titleColorsQueued) return;
        _titleColorsQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _titleColorsQueued = false;
                if (!_closing) ApplyTitleBarColors();
            }))
            _titleColorsQueued = false;
    }
    public bool IsAlwaysOnTop => (GetWindowLong(WinRT.Interop.WindowNative.GetWindowHandle(this), -20) & 0x8) != 0;
    public void SetAlwaysOnTop(bool value)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        presenter.IsAlwaysOnTop = value;
        // Apply Z order to the actual HWND as well. Readback alone cannot make
        // the presenter request take effect on a window already being activated.
        if (!SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(this),
                new IntPtr(value ? -1 : -2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Trace($"topmost requested={value} native={IsAlwaysOnTop}");
    }

    public void ReturnToLauncher()
    {
        _returnToLauncher = true;
        Close();
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        QueueTitleBarColors();
        if (_sized) return;
        _sized = true;
        double scale = _root.XamlRoot?.RasterizationScale ?? GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.ResizeClient(new SizeInt32(Math.Min((int)(1280 * scale), area.Width - 40), Math.Min((int)(820 * scale), area.Height - 60)));
        AppWindow.Move(new PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));
        FontService.Apply(_root);
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_nativeClose) return;
        args.Cancel = true;
        Close();
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        ThemeService.AppearanceChanged -= ApplyAppearance;
        Activated -= Window_Activated;
        AppWindow.Closing -= AppWindow_Closing;
        Closed -= Window_Closed;
        _titleBar.SizeChanged -= TitleBar_SizeChanged;
        _titleBar.Loaded -= TitleBar_Loaded;
        _titleBar.ActualThemeChanged -= TitleBar_ActualThemeChanged;
        // Content and backdrop are already detached before native destruction.
        _backdrop = null;
        Current = null;
        DispatcherQueue.TryEnqueue(() =>
        {
            StateChanged?.Invoke();
            if (_returnToLauncher && MainWindow.Instance is { } launcher)
            {
                launcher.NavigateTo("seedmap");
                if (launcher.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
                    presenter.Restore();
                launcher.Activate();
            }
        });
        Trace("closed");
    }

    public new void Close() => _ = CloseAsync();

    private async Task CloseAsync()
    {
        if (_closing) return;
        _closing = true;
        await Task.Yield();
        // Detach the XAML/composition tree while the HWND is still valid, before
        // WinUI begins destroying it. Allow the pending layout/input turn to drain.
        _map.Stop();
        _map.OwnerWindow = null;
        Content = null;
        SystemBackdrop = null;
        _root.Children.Clear();
        await Task.Delay(150);
        _nativeClose = true;
        base.Close();
    }

    private void ApplyAppearance()
    {
        _root.RequestedTheme = ThemeService.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        _titleBar.RequestedTheme = ElementTheme.Dark;
        var material = MicaController.IsSupported() ? ThemeMaterial.Mica : ThemeMaterial.Solid;
        _root.Background = material == ThemeMaterial.Solid
            ? ThemeService.Brush("WindowBackgroundBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (_material != material)
        {
            if (material == ThemeMaterial.Solid) SystemBackdrop = null;
            else
            {
                _backdrop ??= new WindowBackdrop(material);
                _backdrop.SetMaterial(material);
                SystemBackdrop = _backdrop;
            }
            _material = material;
        }
        _backdrop?.UpdateAcrylic();
        _map.SetChromeMaterial();
        ApplyTitleBarColors();
        QueueTitleBarColors();
        FontService.Apply(_root);
    }

    private void ApplyTitleBarColors()
    {
        // The map is independent of the launcher theme. Keep transparent
        // chrome on a fixed dark palette so title and captions stay white.
        var foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        _titleBar.Foreground = foreground;
        _titleBar.Resources["TitleBarForegroundBrush"] = foreground;
        _titleBar.Resources["TitleBarDeactivatedForegroundBrush"] = foreground;
        _titleBar.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var bar = AppWindow.TitleBar;
        int dark = 1;
        DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 20, ref dark, sizeof(int));
        bar.BackgroundColor = bar.InactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ForegroundColor = Microsoft.UI.Colors.White;
        bar.ButtonForegroundColor = Microsoft.UI.Colors.White;
        bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.White;
        bar.ButtonHoverForegroundColor = bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(34, 255, 255, 255);
        bar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(51, 255, 255, 255);
    }

    public static void Open(SeedMapWorkbench map)
    {
        if (Current is not null) { Current.Activate(); return; }
        Current = new SeedMapWindow(map);
        Trace("activating");
        Current.Activate();
        Trace("activated");
        StateChanged?.Invoke();
    }
    private static void Trace(string text)
    {
        if (SeedMapDiagnostics.TestRoot is not null) Log.Info("SeedMapWindow " + text);
    }
}
