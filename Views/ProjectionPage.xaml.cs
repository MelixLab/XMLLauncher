using LinxLauncher.Services;
using RZeroF1.Minecraft.Projection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace LinxLauncher.Views;

public sealed partial class ProjectionPage : Page
{
    private enum SliceMode
    {
        Full,
        Single,
        Range
    }

    private readonly ProjectionSession _projection = new();
    private readonly ViewTransition _sideToolTransition;
    private readonly UISettings _uiSettings = new();
    private readonly HashSet<VirtualKey> _keys = [];
    private readonly HashSet<string> _expandedMaterials = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ProjectionClientSource> _clientSources = [];
    private ClientResourceInfo? _selectedClient;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _viewInitialization;
    private CancellationTokenSource? _sliceUpdateCancellation;
    private VirtualSurfaceImageSource? _viewportSurface;
    private XamlRoot? _viewportRoot;
    private bool _active;
    private bool _busy;
    private bool _firstFrameRendered;
    private Storyboard? _sliceToolTransition;
    private bool _pointerCaptured;
    private bool _updatingSliceControls;
    private bool _materialGridView = true;
    private string? _selectedMaterialBlock;
    private int _sliceRequestVersion;
    private Point _lastPointer;
    private int _dragMode;
    private int _axis = -1;
    private SliceMode _sliceMode = SliceMode.Full;
    private int _sliceMin;
    private int _sliceMax;
    private int _materialRenderVersion;
    private int _selectedSideToolIndex;
    private ProjectionMemorySnapshot _lastMemory = new(0, 0, 0, 0, 0, 0, 0, 0);
    private RZeroF1.Minecraft.Projection.ProjectionDocument? _document => _projection.Document;

    private SplitView Workspace => WorkspaceTemplate.Workspace;

    public ProjectionPage()
    {
        InitializeComponent();
        WorkspaceTemplate.PaneNavigationView.SelectionChanged += SideTool_SelectionChanged;
        _sideToolTransition = new ViewTransition(
            this,
            WorkspaceTemplate.PaneFrame,
            SideToolPanelHost,
            MaterialsPanel,
            InfoPanel);
        SliceTool.RenderTransform = new TranslateTransform();
        SliceModeBox.SelectedIndex = 0;
        MoveSpeedBox.SelectedIndex = 1;
        MaterialSortBox.SelectedIndex = 0;
        MaterialScopeBox.SelectedIndex = 0;
        SliceAxisYButton.IsChecked = true;
        MaterialGridButton.IsChecked = true;
        Workspace.RegisterPropertyChangedCallback(
            SplitView.IsPaneOpenProperty,
            (_, _) => PanelButton.IsChecked = Workspace.IsPaneOpen);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_active)
            return;
        _active = true;
        _viewInitialization?.Cancel();
        _viewInitialization?.Dispose();
        _viewInitialization = new CancellationTokenSource();
        var initializationToken = _viewInitialization.Token;
        _firstFrameRendered = false;
        ViewportLoadingRing.Visibility = Visibility.Visible;
        ViewportLoadingRing.IsActive = true;

        try
        {
            if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
                await Task.Delay(TimeSpan.FromMilliseconds(400), initializationToken);
            initializationToken.ThrowIfCancellationRequested();
            if (!_active || !IsLoaded)
                return;

            var pixelSize = ProjectionSurfaceAdapter.GetPixelSize(ViewportPanel);
            _viewportSurface = new VirtualSurfaceImageSource(
                (int)pixelSize.Width, (int)pixelSize.Height, isOpaque: true);
            ViewportBrush.ImageSource = _viewportSurface;
            _projection.AttachSurfaceRenderer(
                ProjectionSurfaceAdapter.GetNativeHandle(_viewportSurface),
                pixelSize.Width,
                pixelSize.Height);
            _viewportRoot = XamlRoot;
            _viewportRoot.Changed += ViewportRoot_Changed;
            _loadCancellation = new CancellationTokenSource();
            CaptureMemory("page-loaded");
            _clientSources = await DiscoverProjectionClientsAsync(_loadCancellation.Token);
            var clients = await _projection.DiscoverClientsAsync(_clientSources, _loadCancellation.Token);
            _selectedClient = clients.FirstOrDefault();
            ShowConfiguredClients();
            CompositionTarget.Rendering += Render;
            UpdateResponsiveLayout();
            UpdateCameraStatus();
            ViewportPanel.Focus(FocusState.Programmatic);
        }
        catch (OperationCanceledException) when (initializationToken.IsCancellationRequested || !_active)
        {
        }
        catch (Exception exception)
        {
            DetachViewport();
            HideViewportLoadingRing();
            SetError("初始化原生 3D 视口失败：" + exception.Message);
            Log.Error("ProjectionPage.Start", exception);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_active)
            return;
        _active = false;
        SetSliceToolVisibility(false, animate: false);
        _viewInitialization?.Cancel();
        _viewInitialization?.Dispose();
        _viewInitialization = null;
        HideViewportLoadingRing();
        CompositionTarget.Rendering -= Render;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _sliceUpdateCancellation?.Cancel();
        _sliceUpdateCancellation?.Dispose();
        _sliceUpdateCancellation = null;
        ClearScene();
        CaptureMemory("scene-cleared");
        _projection.Clear();
        DetachViewport();
        _keys.Clear();
        CaptureMemory("page-unloaded");
    }

    private void ShowConfiguredClients()
    {
        ClientActionPanel.Visibility = _selectedClient is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClientText.Text = _selectedClient is null
            ? "未发现已配置的 Minecraft 版本。请先在设置中配置游戏目录，或在版本库安装一个版本。"
            : $"自动匹配：{_selectedClient.Label} · {_selectedClient.DataVersionLabel}";
    }

    private async void OpenProjection_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));
        picker.FileTypeFilter.Add(".litematic");
        picker.FileTypeFilter.Add(".schem");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            await LoadProjectionAsync(file.Path);
    }

    private async void PickPack_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));
        picker.FileTypeFilter.Add(".zip");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;
        ResourceText.Text = $"资源包：{file.Name}";
        if (_document is not null)
            await ReloadCurrentProjectionAsync(file.Path);
    }

    private async Task LoadProjectionAsync(string path)
    {
        if (!File.Exists(path))
            return;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;
        _busy = true;
        CaptureButton.IsEnabled = false;
        ClearScene();
        CaptureMemory("load-start");
        FileText.Text = Path.GetFileName(path);
        SetStatus($"正在读取 {Path.GetFileName(path)}…");

        try
        {
            var progress = new Progress<ProjectionLoadProgress>(value =>
            {
                LoadStatusText.Text = value.Total > 0
                    ? $"{value.Message} · {value.Completed:N0}/{value.Total:N0}"
                    : value.Message;
            });
            _selectedMaterialBlock = null;
            _axis = -1;
            SetSliceMode(SliceMode.Full);
            if (_clientSources.Count == 0)
                _clientSources = await DiscoverProjectionClientsAsync(cancellationToken);
            await _projection.LoadAsync(path, _clientSources, progress: progress, cancellationToken: cancellationToken);
            var document = _document!;
            _selectedClient = _projection.SelectedClient;
            ClientText.Text = $"自动匹配：{_selectedClient?.Label} · {_selectedClient?.DataVersionLabel}";
            UpdateCamera();
            CaptureMemory("projection-loaded");
            await RefreshMaterialListAsync();
            UpdateInfo(document, document.Blocks.Count);
            SliceToggleButton.IsEnabled = true;
            SetStatus($"已显示 {document.Name} · {document.Format} · {document.Blocks.Count:N0} 个非空气方块");
            CaptureButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("已取消载入。");
        }
        catch (Exception exception)
        {
            SliceToggleButton.IsChecked = false;
            SliceToggleButton.IsEnabled = false;
            SetSliceToolVisibility(false, animate: false);
            SetError("载入失败：" + exception.Message);
            Log.Error("ProjectionPage.Load", exception);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ReloadCurrentProjectionAsync(string? resourcePack = null)
    {
        if (_document is null)
            return;
        if (resourcePack is null)
        {
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = new CancellationTokenSource();
            var reloadToken = _loadCancellation.Token;
            _busy = true;
            try
            {
                await _projection.ReloadAsync(
                    new Progress<ProjectionLoadProgress>(value => LoadStatusText.Text = value.Message),
                    reloadToken);
                await RefreshMaterialListAsync();
                UpdateInfo(_document!, VisibleBlockCount(_document!));
                SetStatus("已重新生成网格。");
            }
            finally
            {
                _busy = false;
            }
            return;
        }

        _loadCancellation?.Cancel();
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;
        _busy = true;
        try
        {
            var progress = new Progress<ProjectionLoadProgress>(value => LoadStatusText.Text = value.Message);
            await _projection.ApplyResourcePackAsync(resourcePack, progress, cancellationToken);
            await ApplyNativeSliceAsync(cancellationToken, rebuildBoundary: true);
            await RefreshMaterialListAsync();
            UpdateInfo(_document, VisibleBlockCount(_document));
            SetStatus("已应用资源包并重新生成网格。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("已取消重载。");
        }
        catch (Exception exception)
        {
            SetError("资源包载入失败：" + exception.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private static async Task<IReadOnlyList<ProjectionClientSource>> DiscoverProjectionClientsAsync(
        CancellationToken cancellationToken)
    {
        AppState.RefreshInstalled();
        var clients = new List<ProjectionClientSource>();
        var versions = AppState.Installed
            .Where(static version => version.IsComplete)
            .OrderByDescending(version => version.Id.Equals(AppState.SelectedVersion, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static version => version.LastWrite)
            .ToArray();
        foreach (var version in versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = await AppState.Versions.ResolveAsync(version.Id, cancellationToken);
            if (resolved.ClientJar is { } jar)
                clients.Add(new ProjectionClientSource(jar, version.Id));
        }
        return clients;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance?.NavigateTo("settings");

    private void OpenVersions_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance?.NavigateTo("versions", "installed");

    private async void ClearSlice_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || _busy)
            return;
        _sliceRequestVersion++;
        _sliceUpdateCancellation?.Cancel();
        SetSliceState(SliceMode.Full, -1, int.MinValue, int.MaxValue);
        await ApplyNativeSliceAsync(
            _loadCancellation?.Token ?? CancellationToken.None,
            rebuildBoundary: false);
        await RefreshMaterialListAsync();
        UpdateInfo(_document, _document.Blocks.Count);
        SetStatus("已恢复完整模型。");
    }

    private void SliceCommand_Click(object sender, RoutedEventArgs e)
    {
        bool show = _document is not null && !_busy && SliceToggleButton.IsChecked == true;
        SliceToggleButton.IsChecked = show;
        SetSliceToolVisibility(show);
        if (show)
            SliceModeBox.Focus(FocusState.Programmatic);
    }

    private void SetSliceToolVisibility(bool visible, bool animate = true)
    {
        _sliceToolTransition?.Stop();
        _sliceToolTransition = null;

        var offset = (TranslateTransform)SliceTool.RenderTransform;
        if (!animate || !_uiSettings.AnimationsEnabled)
        {
            SliceTool.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            SliceTool.IsHitTestVisible = visible;
            SliceTool.Opacity = 1;
            offset.Y = 0;
            return;
        }

        if (visible)
        {
            SliceTool.Visibility = Visibility.Visible;
            SliceTool.IsHitTestVisible = true;
            SliceTool.Opacity = 0;
            offset.Y = 24;
        }
        else
        {
            SliceTool.IsHitTestVisible = false;
        }

        var transition = new Storyboard();
        var opacity = new DoubleAnimation
        {
            To = visible ? 1 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        Storyboard.SetTarget(opacity, SliceTool);
        Storyboard.SetTargetProperty(opacity, nameof(UIElement.Opacity));
        transition.Children.Add(opacity);

        var slide = new DoubleAnimation
        {
            To = visible ? 0 : 24,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        Storyboard.SetTarget(slide, offset);
        Storyboard.SetTargetProperty(slide, nameof(TranslateTransform.Y));
        transition.Children.Add(slide);

        transition.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_sliceToolTransition, transition))
                return;
            if (!visible)
                SliceTool.Visibility = Visibility.Collapsed;
            SliceTool.Opacity = 1;
            offset.Y = 0;
            _sliceToolTransition = null;
        };
        _sliceToolTransition = transition;
        transition.Begin();
    }

    private void SliceAxis_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || _updatingSliceControls || sender is not ToggleButton button)
            return;
        var axis = SliceAxisButtonIndex(button);
        if (axis < 0)
            return;
        SetSliceAxisButton(axis);
        UpdateSliceRange(axis);
        if (_sliceMode == SliceMode.Single)
        {
            var coordinate = Math.Clamp(
                _sliceMin == int.MinValue ? _document.Min.GetAxis(axis) : _sliceMin,
                _document.Min.GetAxis(axis),
                _document.Max.GetAxis(axis));
            SetSliceState(SliceMode.Single, axis, coordinate, coordinate);
            ScheduleSlicePreview();
        }
        else if (_sliceMode == SliceMode.Range)
        {
            var minimum = Math.Clamp(
                _sliceMin == int.MinValue ? _document.Min.GetAxis(axis) : _sliceMin,
                _document.Min.GetAxis(axis),
                _document.Max.GetAxis(axis));
            var maximum = Math.Clamp(
                _sliceMax == int.MaxValue ? _document.Max.GetAxis(axis) : _sliceMax,
                _document.Min.GetAxis(axis),
                _document.Max.GetAxis(axis));
            if (minimum > maximum)
                (minimum, maximum) = (maximum, minimum);
            SetSliceState(SliceMode.Range, axis, minimum, maximum);
            ScheduleSlicePreview();
        }
    }

    private void SliceMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_document is null || _updatingSliceControls)
            return;

        var mode = ReadSliceMode();
        if (mode == SliceMode.Full)
        {
            ClearSlice_Click(sender, new RoutedEventArgs());
            return;
        }

        if (!TryReadAxis(out var axis))
            return;
        var minimum = _document.Min.GetAxis(axis);
        var maximum = _document.Max.GetAxis(axis);
        if (mode == SliceMode.Single)
        {
            var coordinate = Math.Clamp(
                _sliceMin == int.MinValue ? minimum : _sliceMin,
                minimum,
                maximum);
            SetSliceState(SliceMode.Single, axis, coordinate, coordinate);
        }
        else
        {
            var rangeMinimum = Math.Clamp(
                _sliceMin == int.MinValue ? minimum : _sliceMin,
                minimum,
                maximum);
            var rangeMaximum = Math.Clamp(
                _sliceMax == int.MaxValue ? maximum : _sliceMax,
                minimum,
                maximum);
            if (rangeMinimum > rangeMaximum)
                (rangeMinimum, rangeMaximum) = (rangeMaximum, rangeMinimum);
            SetSliceState(SliceMode.Range, axis, rangeMinimum, rangeMaximum);
        }
        UpdateSliceControls();
        ScheduleSlicePreview();
    }

    private void SliceSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_document is null || _updatingSliceControls || ReadSliceMode() != SliceMode.Single)
            return;
        var coordinate = ClampCoordinate((int)Math.Round(e.NewValue));
        _updatingSliceControls = true;
        SliceValueBox.Value = coordinate;
        _updatingSliceControls = false;
        if (TryReadAxis(out var axis))
        {
            SetSliceState(SliceMode.Single, axis, coordinate, coordinate);
            ScheduleSlicePreview();
        }
    }

    private void SliceValue_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_document is null || _updatingSliceControls || ReadSliceMode() != SliceMode.Single)
            return;
        if (!TryReadAxis(out var axis))
            return;
        var coordinate = Math.Clamp(
            ReadNumber(args.NewValue, _document.Min.GetAxis(axis)),
            _document.Min.GetAxis(axis),
            _document.Max.GetAxis(axis));
        _updatingSliceControls = true;
        SliceSlider.Value = coordinate;
        _updatingSliceControls = false;
        SetSliceState(SliceMode.Single, axis, coordinate, coordinate);
        ScheduleSlicePreview();
    }

    private void SliceRange_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_document is null || _updatingSliceControls || ReadSliceMode() != SliceMode.Range)
            return;
        if (!TryReadAxis(out var axis))
            return;
        var minimum = Math.Clamp(
            ReadNumber(SliceMinBox.Value, _document.Min.GetAxis(axis)),
            _document.Min.GetAxis(axis),
            _document.Max.GetAxis(axis));
        var maximum = Math.Clamp(
            ReadNumber(SliceMaxBox.Value, _document.Max.GetAxis(axis)),
            _document.Min.GetAxis(axis),
            _document.Max.GetAxis(axis));
        if (minimum > maximum)
            (minimum, maximum) = (maximum, minimum);
        SetSliceState(SliceMode.Range, axis, minimum, maximum);
        ScheduleSlicePreview();
    }

    private async void SliceStep_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || ReadSliceMode() != SliceMode.Single ||
            !TryReadAxis(out var axis) ||
            sender is not Button button ||
            !int.TryParse(button.Tag?.ToString(), out var step))
            return;

        var current = ClampCoordinate(ReadNumber(SliceValueBox.Value, _document.Min.GetAxis(axis)));
        var next = Math.Clamp(
            current + step,
            _document.Min.GetAxis(axis),
            _document.Max.GetAxis(axis));
        _updatingSliceControls = true;
        SliceSlider.Value = next;
        SliceValueBox.Value = next;
        _updatingSliceControls = false;
        SetSliceState(SliceMode.Single, axis, next, next);
        _sliceRequestVersion++;
        _sliceUpdateCancellation?.Cancel();
        await ApplyNativeSliceAsync(
            _loadCancellation?.Token ?? CancellationToken.None,
            rebuildBoundary: true);
        await RefreshMaterialListAsync();
        UpdateInfo(_document, VisibleBlockCount(_document));
    }

    private void UpdateSliceRange(int axis)
    {
        if (_document is null)
            return;
        var minimum = _document.Min.GetAxis(axis);
        var maximum = _document.Max.GetAxis(axis);
        _updatingSliceControls = true;
        SliceSlider.Minimum = minimum;
        SliceSlider.Maximum = maximum;
        SliceSlider.StepFrequency = 1;
        SliceSlider.Value = Math.Clamp(
            _sliceMode == SliceMode.Single ? _sliceMin : minimum,
            minimum,
            maximum);
        SliceValueBox.Minimum = minimum;
        SliceValueBox.Maximum = maximum;
        SliceMinBox.Minimum = minimum;
        SliceMinBox.Maximum = maximum;
        SliceMaxBox.Minimum = minimum;
        SliceMaxBox.Maximum = maximum;
        SliceValueBox.Value = Math.Clamp(
            _sliceMode == SliceMode.Single ? _sliceMin : minimum,
            minimum,
            maximum);
        SliceMinBox.Value = Math.Clamp(
            _sliceMin == int.MinValue ? minimum : _sliceMin,
            minimum,
            maximum);
        SliceMaxBox.Value = Math.Clamp(
            _sliceMax == int.MaxValue ? maximum : _sliceMax,
            minimum,
            maximum);
        _updatingSliceControls = false;
        UpdateSliceControls();
    }

    private void SetSliceMode(SliceMode mode)
    {
        _sliceMode = mode;
        _updatingSliceControls = true;
        SliceModeBox.SelectedIndex = (int)mode;
        if (_document is not null && TryReadAxis(out var axis))
            UpdateSliceRange(axis);
        _updatingSliceControls = false;
        UpdateSliceControls();
    }

    private void SetSliceState(SliceMode mode, int axis, int minimum, int maximum)
    {
        _sliceMode = mode;
        _axis = mode == SliceMode.Full ? -1 : axis;
        _sliceMin = mode == SliceMode.Full ? int.MinValue : minimum;
        _sliceMax = mode == SliceMode.Full ? int.MaxValue : maximum;
        _updatingSliceControls = true;
        SliceModeBox.SelectedIndex = (int)mode;
        if (axis is >= 0 and <= 2)
            SetSliceAxisButton(axis);
        _updatingSliceControls = false;
        if (_document is not null && axis is >= 0 and <= 2)
            UpdateSliceRange(axis);
        UpdateSliceControls();
        UpdateSliceSummary();
    }

    private void UpdateSliceControls()
    {
        var full = _sliceMode == SliceMode.Full;
        var single = _sliceMode == SliceMode.Single;
        SliceAxisBar.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        SingleSlicePanel.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        SingleSliceValuePanel.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        SliceRangeExpander.Visibility = _sliceMode == SliceMode.Range
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_sliceMode == SliceMode.Range)
            SliceRangeExpander.IsExpanded = true;
        UpdateSliceSummary();
    }

    private SliceMode ReadSliceMode() => SliceModeBox.SelectedIndex switch
    {
        1 => SliceMode.Single,
        2 => SliceMode.Range,
        _ => SliceMode.Full
    };

    private int SliceAxisButtonIndex(ToggleButton button) => button switch
    {
        _ when ReferenceEquals(button, SliceAxisXButton) => 0,
        _ when ReferenceEquals(button, SliceAxisYButton) => 1,
        _ when ReferenceEquals(button, SliceAxisZButton) => 2,
        _ => -1
    };

    private bool TryReadAxis(out int axis)
    {
        axis = SliceAxisXButton.IsChecked == true
            ? 0
            : SliceAxisYButton.IsChecked == true
                ? 1
                : SliceAxisZButton.IsChecked == true
                    ? 2
                    : -1;
        return axis is >= 0 and <= 2;
    }

    private void SetSliceAxisButton(int axis)
    {
        _updatingSliceControls = true;
        SliceAxisXButton.IsChecked = axis == 0;
        SliceAxisYButton.IsChecked = axis == 1;
        SliceAxisZButton.IsChecked = axis == 2;
        _updatingSliceControls = false;
    }

    private static int ReadNumber(double value, int fallback) =>
        double.IsFinite(value) ? (int)Math.Round(value) : fallback;

    private int ClampCoordinate(int coordinate)
    {
        if (_document is null || !TryReadAxis(out var axis))
            return coordinate;
        return Math.Clamp(
            coordinate,
            _document.Min.GetAxis(axis),
            _document.Max.GetAxis(axis));
    }

    private void UpdateSliceSummary()
    {
        if (_document is null)
            return;
        if (_sliceMode == SliceMode.Full || _axis < 0)
        {
            SliceAppliedText.Text = "完整模型";
            SliceRangeText.Text = "范围：完整模型";
            SlicePendingText.Text = "拖动滑块可逐层查看";
            return;
        }

        var axisName = AxisName(_axis);
        SliceAppliedText.Text = _sliceMode == SliceMode.Single
            ? $"{axisName}={_sliceMin}"
            : $"{axisName}={_sliceMin}–{_sliceMax}";
        SliceRangeText.Text = $"模型范围：{_document.Min.GetAxis(_axis)}–{_document.Max.GetAxis(_axis)}";
        SlicePendingText.Text = $"{axisName} 轴 · 当前显示 {VisibleBlockCount(_document):N0} 个方块";
    }

    private static string AxisName(int axis) => axis switch
    {
        0 => "X",
        1 => "Y",
        _ => "Z"
    };

    private void ScheduleSlicePreview()
    {
        if (_document is null || _busy || _sliceMode == SliceMode.Full)
            return;

        var request = ++_sliceRequestVersion;
        _sliceUpdateCancellation?.Cancel();
        _sliceUpdateCancellation?.Dispose();
        _sliceUpdateCancellation = new CancellationTokenSource();
        var cancellationToken = _sliceUpdateCancellation.Token;
        SlicePendingText.Text = "正在更新剖切…";
        _ = ApplySlicePreviewAsync(request, cancellationToken);
    }

    private async Task ApplySlicePreviewAsync(int request, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(90, cancellationToken);
            if (_document is null || request != _sliceRequestVersion)
                return;
            await ApplyNativeSliceAsync(cancellationToken, rebuildBoundary: true);
            if (request != _sliceRequestVersion)
                return;
            await RefreshMaterialListAsync();
            UpdateInfo(_document, VisibleBlockCount(_document));
            SlicePendingText.Text = $"{AxisName(_axis)} 轴 · 当前显示 {VisibleBlockCount(_document):N0} 个方块";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError("剖切失败：" + exception.Message);
        }
    }

    private async Task ApplyNativeSliceAsync(
        CancellationToken cancellationToken,
        bool rebuildBoundary)
    {
        if (_document is null)
            return;

        if (_sliceMode == SliceMode.Full || _axis < 0)
        {
            await _projection.ApplySliceAsync(-1, int.MinValue, int.MaxValue, cancellationToken, rebuildBoundary: false);
            SlicePendingText.Text = "拖动滑块可逐层查看";
            return;
        }
        await _projection.ApplySliceAsync(_axis, _sliceMin, _sliceMax, cancellationToken, rebuildBoundary);
    }

    private async Task RefreshMaterialListAsync()
    {
        if (_document is null)
        {
            MaterialsList.Items.Clear();
            MaterialsGrid.Items.Clear();
            MaterialSummaryText.Text = "等待载入投影";
            return;
        }

        var generation = ++_materialRenderVersion;
        var axis = MaterialScopeBox.SelectedIndex == 1 ? _axis : -1;
        var minimum = axis < 0 ? int.MinValue : _sliceMin;
        var maximum = axis < 0 ? int.MaxValue : _sliceMax;
        var groups = GetMaterialGroups(axis, minimum, maximum);
        var query = MaterialSearchBox.Text.Trim();
        if (query.Length > 0)
        {
            groups = groups
                .Where(group =>
                    group.BlockName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    _projection.GetDisplayName(group.BlockName).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    group.Variants.Any(variant => variant.StateKey.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }

        groups = MaterialSortBox.SelectedIndex == 1
            ? groups.OrderBy(group => _projection.GetDisplayName(group.BlockName), StringComparer.CurrentCultureIgnoreCase).ToArray()
            : groups.OrderByDescending(static group => group.Count)
                .ThenBy(group => group.BlockName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        MaterialsList.Items.Clear();
        MaterialsGrid.Items.Clear();
        var scopeText = axis < 0 ? "完整投影" : "当前剖切";
        MaterialSummaryText.Text = $"{scopeText} · {groups.Count:N0} 种类型 · {groups.Sum(static group => group.Count):N0} 个方块";
        if (_materialGridView)
        {
            foreach (var group in groups)
                MaterialsGrid.Items.Add(CreateMaterialGridCard(group, generation));
        }
        else
        {
            foreach (var group in groups)
            {
                var first = group.Variants[0];
                MaterialsList.Items.Add(CreateMaterialGroupRow(group, first, generation));

                if (_expandedMaterials.Contains(group.BlockName))
                {
                    foreach (var variant in group.Variants)
                        MaterialsList.Items.Add(CreateMaterialVariantRow(variant, generation));
                }
            }
        }

        await Task.CompletedTask;
    }

    private IReadOnlyList<RZeroF1.Minecraft.Projection.ProjectionMaterialGroup> GetMaterialGroups(
        int axis,
        int minimum,
        int maximum)
    {
        return _projection.GetMaterialGroups(axis, minimum, maximum);
    }

    private Border CreateMaterialGroupRow(
        RZeroF1.Minecraft.Projection.ProjectionMaterialGroup group,
        RZeroF1.Minecraft.Projection.ProjectionMaterialVariant first,
        int generation)
    {
        var icon = CreateMaterialIcon(first.StateKey, generation);
        var name = _projection.GetDisplayName(group.BlockName);
        var content = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(32) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children =
            {
                icon,
                new StackPanel
                {
                    Margin = new Thickness(10, 0, 8, 0),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = name,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            FontFamily = FontService.BodyFont
                        },
                        new TextBlock
                        {
                            Text = group.BlockName,
                            FontSize = 11,
                            Foreground = ThemeService.Brush("SecondaryTextBrush"),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            FontFamily = FontService.BodyFont
                        }
                    }
                },
                new TextBlock
                {
                    Text = group.Count.ToString("N0"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    FontFamily = FontService.BodyFont
                }
            }
        };
        Grid.SetColumn((FrameworkElement)content.Children[1], 1);
        Grid.SetColumn((FrameworkElement)content.Children[2], 2);
        var button = new Button
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 7, 8, 7),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Tag = group.BlockName,
            Content = content
        };
        button.Click += MaterialGroup_Click;
        ToolTipService.SetToolTip(button, $"{name}\n{group.BlockName}\n{group.Count:N0} 个");
        return new Border
        {
            Padding = new Thickness(0, 1, 0, 1),
            Child = button
        };
    }

    private Border CreateMaterialVariantRow(RZeroF1.Minecraft.Projection.ProjectionMaterialVariant variant, int generation)
    {
        var icon = CreateMaterialIcon(variant.StateKey, generation);
        var properties = variant.Properties.Count == 0
            ? "默认状态"
            : string.Join(", ", variant.Properties.Select(static pair => $"{pair.Key}={pair.Value}"));
        var grid = new Grid
        {
            Margin = new Thickness(24, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(32) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        grid.Children.Add(icon);
        var details = new StackPanel
        {
            Margin = new Thickness(10, 0, 8, 0),
            Children =
            {
                new TextBlock
                {
                    Text = properties,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontFamily = FontService.BodyFont
                },
                new TextBlock
                {
                    Text = variant.StateKey,
                    FontSize = 10.5,
                    Foreground = ThemeService.Brush("SecondaryTextBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontFamily = FontService.BodyFont
                }
            }
        };
        grid.Children.Add(details);
        grid.Children.Add(new TextBlock
        {
            Text = variant.Count.ToString("N0"),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = FontService.BodyFont
        });
        Grid.SetColumn(details, 1);
        Grid.SetColumn((FrameworkElement)grid.Children[2], 2);
        return new Border { Child = grid };
    }

    private Button CreateMaterialGridCard(
        RZeroF1.Minecraft.Projection.ProjectionMaterialGroup group,
        int generation)
    {
        var first = group.Variants[0];
        var name = _projection.GetDisplayName(group.BlockName);
        var image = CreateMaterialIcon(first.StateKey, generation);
        image.Width = 42;
        image.Height = 42;

        var card = new Grid
        {
            Width = 92,
            Height = 94,
            Margin = new Thickness(2),
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(58) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };
        var iconHost = new Border
        {
            Width = 58,
            Height = 58,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeService.Brush("CardHoverBackgroundBrush"),
            BorderBrush = ThemeService.Brush("DividerBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = image
        };
        card.Children.Add(iconHost);
        var countBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(4, 1, 4, 1),
            Background = ThemeService.Brush("BrandSoftBrush"),
            CornerRadius = new CornerRadius(7),
            Child = new TextBlock
            {
                Text = group.Count.ToString("N0"),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeService.Brush("PrimaryTextBrush")
            }
        };
        card.Children.Add(countBadge);
        var caption = new TextBlock
        {
            Text = name,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = FontService.BodyFont
        };
        Grid.SetRow(caption, 1);
        card.Children.Add(caption);

        var button = new Button
        {
            Width = 98,
            Height = 100,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Tag = group.BlockName,
            Content = card
        };
        button.Click += MaterialCard_Click;
        ToolTipService.SetToolTip(button, $"{name}\n{group.BlockName}\n{group.Count:N0} 个");
        return button;
    }

    private void MaterialCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string blockName })
        {
            _selectedMaterialBlock = blockName;
            ShowMaterialDetails(blockName);
        }
    }

    private void ShowMaterialDetails(string blockName)
    {
        if (_document is null)
            return;
        var axis = MaterialScopeBox.SelectedIndex == 1 ? _axis : -1;
        var minimum = axis < 0 ? int.MinValue : _sliceMin;
        var maximum = axis < 0 ? int.MaxValue : _sliceMax;
        var group = GetMaterialGroups(axis, minimum, maximum)
            .FirstOrDefault(item => item.BlockName.Equals(blockName, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            MaterialDetailExpander.Visibility = Visibility.Collapsed;
            return;
        }

        var name = _projection.GetDisplayName(group.BlockName);
        MaterialDetailContent.Children.Clear();
        MaterialDetailContent.Children.Add(new TextBlock
        {
            Text = $"{name} · {group.Count:N0} 个",
            Style = (Style)Application.Current.Resources["BodyStrongTextStyle"]
        });
        MaterialDetailContent.Children.Add(new TextBlock
        {
            Text = group.BlockName,
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        foreach (var variant in group.Variants)
        {
            var properties = variant.Properties.Count == 0
                ? "默认状态"
                : string.Join(", ", variant.Properties.Select(static pair => $"{pair.Key}={pair.Value}"));
            MaterialDetailContent.Children.Add(new TextBlock
            {
                Text = $"{properties} · {variant.Count:N0}",
                Style = (Style)Application.Current.Resources["CaptionTextStyle"],
                TextWrapping = TextWrapping.Wrap
            });
        }
        MaterialDetailExpander.Header = name;
        MaterialDetailExpander.Visibility = Visibility.Visible;
        MaterialDetailExpander.IsExpanded = true;
    }

    private Image CreateMaterialIcon(string stateKey, int generation)
    {
        var image = new Image
        {
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            Source = new BitmapImage(new Uri("ms-appx:///Assets/Blocks/vanilla.png"))
        };
        if (_projection.MaterialIcons.TryGetValue(stateKey, out var png) && png is { Length: > 0 })
            _ = SetIconSourceAsync(image, png, generation);
        return image;
    }

    private async Task SetIconSourceAsync(Image image, byte[] png, int generation)
    {
        try
        {
            var bitmap = new BitmapImage();
            using var source = new MemoryStream(png, writable: false);
            using var random = source.AsRandomAccessStream();
            await bitmap.SetSourceAsync(random);
            if (generation == _materialRenderVersion)
                image.Source = bitmap;
        }
        catch
        {
            // 已有 vanilla 图标作为明确的本地缺失回退。
        }
    }

    private async void MaterialGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string blockName })
        {
            _selectedMaterialBlock = blockName;
            if (!_expandedMaterials.Add(blockName))
                _expandedMaterials.Remove(blockName);
            await RefreshMaterialListAsync();
            ShowMaterialDetails(blockName);
        }
    }

    private async void MaterialSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        await RefreshMaterialListAsync();

    private async void MaterialSort_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await RefreshMaterialListAsync();

    private async void MaterialScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedMaterialBlock = null;
        MaterialDetailExpander.Visibility = Visibility.Collapsed;
        await RefreshMaterialListAsync();
    }

    private async void MaterialView_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button)
            return;
        _materialGridView = ReferenceEquals(button, MaterialGridButton);
        _updatingSliceControls = true;
        MaterialGridButton.IsChecked = _materialGridView;
        MaterialListButton.IsChecked = !_materialGridView;
        _updatingSliceControls = false;
        MaterialsGrid.Visibility = _materialGridView ? Visibility.Visible : Visibility.Collapsed;
        MaterialsList.Visibility = _materialGridView ? Visibility.Collapsed : Visibility.Visible;
        await RefreshMaterialListAsync();
    }

    private void SideTool_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        int selectedIndex = ((sender.SelectedItem as NavigationViewItem)?.Tag as string) == "Info" ? 1 : 0;
        if (!_active || selectedIndex == _selectedSideToolIndex)
            return;
        int previousIndex = _selectedSideToolIndex;
        _selectedSideToolIndex = selectedIndex;
        FrameworkElement selected = selectedIndex == 1 ? InfoPanel : MaterialsPanel;
        _sideToolTransition.Show(selected, selectedIndex > previousIndex);
        FontService.Apply(selected);
    }

    private void Panel_Click(object sender, RoutedEventArgs e)
    {
        if (PanelButton.IsChecked == true)
        {
            Workspace.IsPaneOpen = true;
        }
        else
        {
            Workspace.IsPaneOpen = false;
            ViewportPanel.Focus(FocusState.Programmatic);
        }
    }

    private void UpdateInfo(RZeroF1.Minecraft.Projection.ProjectionDocument document, int visibleBlocks)
    {
        FileSummaryText.Text =
            $"{document.Name}\n" +
            $"{document.Format} · DataVersion {document.DataVersion?.ToString() ?? "未知"}\n" +
            $"{_selectedClient?.Label ?? "未匹配客户端"}";
        ResourceSummaryText.Text = ResourceText.Text;
        InfoText.Text =
            $"尺寸：{document.Width} × {document.Height} × {document.Length}\n" +
            $"非空气方块：{visibleBlocks:N0} / {document.Blocks.Count:N0}\n" +
            $"当前范围：{(_axis < 0 ? "完整模型" : $"{AxisName(_axis)} {_sliceMin}–{_sliceMax}")}";
        DetailsText.Text =
            $"DataVersion：{document.DataVersion?.ToString() ?? "未知"}\n" +
            $"区域：{document.Regions.Count}\n" +
            $"Palette：{document.Palette.Count}\n" +
            string.Join(
                "\n",
                document.Regions.Select(region =>
                    $"· {region.Name}：{region.Size.X} × {region.Size.Y} × {region.Size.Z} · {region.Volume:N0}"));
        MemoryText.Text = _lastMemory.PrivateBytes > 0
            ? MemorySummary()
            : "本进程内存：等待采样";
        var warnings = _projection.Warnings.Distinct().Take(5).ToArray();
        WarningText.Text = warnings.Length == 0
            ? string.Empty
            : "诊断：\n" + string.Join("\n", warnings);
        WarningText.Visibility = warnings.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DiagnosticsExpander.Header = warnings.Length == 0
            ? "技术诊断"
            : $"技术诊断 · {warnings.Length} 条提示";
    }

    private int VisibleBlockCount(RZeroF1.Minecraft.Projection.ProjectionDocument document)
    {
        return _projection.VisibleBlockCount(_axis, _sliceMin, _sliceMax);
    }

    private void CaptureMemory(string phase)
    {
        try
        {
            _lastMemory = _projection.CaptureDiagnostics();
            Log.Info(
                $"ProjectionMemory phase={phase} private={_lastMemory.PrivateBytes / 1024d / 1024d:F1}MB " +
                $"working={_lastMemory.WorkingSet / 1024d / 1024d:F1}MB " +
                $"managed={_lastMemory.ManagedBytes / 1024d / 1024d:F1}MB " +
                $"document={_lastMemory.DocumentBytes / 1024d / 1024d:F1}MB " +
                $"mesh={_lastMemory.MeshBytes / 1024d / 1024d:F1}MB " +
                $"gpu={_lastMemory.GpuBytes / 1024d / 1024d:F1}MB " +
                $"resources={_lastMemory.ResourceBytes / 1024d / 1024d:F1}MB " +
                $"sliceCache={_lastMemory.CachedSliceBytes / 1024d / 1024d:F1}MB");
            if (_document is not null)
                MemoryText.Text = MemorySummary();
        }
        catch (Exception exception)
        {
            Log.Warn($"ProjectionMemory phase={phase} 采样失败：{exception.Message}");
        }
    }

    private string MemorySummary() =>
        $"本进程：Private {_lastMemory.PrivateBytes / 1024d / 1024d:F0} MB · Working Set {_lastMemory.WorkingSet / 1024d / 1024d:F0} MB · Managed {_lastMemory.ManagedBytes / 1024d / 1024d:F0} MB\n" +
        $"估算：文档 {_lastMemory.DocumentBytes / 1024d / 1024d:F1} MB · 网格/纹理 {_lastMemory.MeshBytes / 1024d / 1024d:F1} MB · D3D 网格/纹理 {_lastMemory.GpuBytes / 1024d / 1024d:F1} MB";

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (!_projection.IsRendererCreated || _document is null)
            return;
        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));
        picker.FileTypeChoices.Add("PNG 图片", [".png"]);
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_document.SourcePath) + "-preview";
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;
        try
        {
            var bytes = await _projection.CapturePngAsync();
            await FileIO.WriteBytesAsync(file, bytes);
            SetStatus($"已导出 {file.Name}。");
        }
        catch (Exception exception)
        {
            SetError("导出失败：" + exception.Message);
        }
    }

    private void FreeCamera_Click(object sender, RoutedEventArgs e)
    {
        _projection.SetCameraMode(FreeCameraButton.IsChecked == true
            ? ProjectionCameraMode.Free
            : ProjectionCameraMode.Orbit);
        UpdateCamera();
        ViewportPanel.Focus(FocusState.Programmatic);
    }

    private void ResetCamera_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
            return;
        _projection.ResetCamera();
        UpdateSliceRange(1);
        SetStatus("已重置视角。");
    }

    private void FocusModel_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
            return;
        _projection.FocusCamera();
        SetStatus("已聚焦模型。");
    }

    private void Viewport_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Workspace.DisplayMode == SplitViewDisplayMode.Overlay && Workspace.IsPaneOpen)
        {
            Workspace.IsPaneOpen = false;
            PanelButton.IsChecked = false;
        }
        var point = e.GetCurrentPoint(ViewportPanel);
        _lastPointer = point.Position;
        _dragMode = point.Properties.IsRightButtonPressed ? 2 : 1;
        _pointerCaptured = ViewportPanel.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Viewport_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerCaptured)
            return;
        var point = e.GetCurrentPoint(ViewportPanel).Position;
        var dx = point.X - _lastPointer.X;
        var dy = point.Y - _lastPointer.Y;
        _lastPointer = point;
        if (_dragMode == 1)
        {
            _projection.RotateCamera((float)dx, (float)dy);
        }
        else
        {
            _projection.PanCamera((float)dx, (float)dy, (float)ViewportPanel.ActualHeight);
        }
        UpdateCamera();
    }

    private void Viewport_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerCaptured)
            ViewportPanel.ReleasePointerCapture(e.Pointer);
        _pointerCaptured = false;
        e.Handled = true;
    }

    private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(ViewportPanel).Properties.MouseWheelDelta;
        _projection.ZoomCamera(delta / 120f);
        UpdateCameraStatus();
        e.Handled = true;
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
        => ResizeViewport();

    private void ViewportRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
        => ResizeViewport();

    private void ResizeViewport()
    {
        if (!_projection.IsRendererCreated)
            return;
        try
        {
            var size = ProjectionSurfaceAdapter.GetPixelSize(ViewportPanel);
            _projection.ResizeRenderer(size.Width, size.Height);
        }
        catch (Exception exception)
        {
            StopViewport(exception);
        }
    }

    private void StopViewport(Exception exception)
    {
        DetachViewport();
        HideViewportLoadingRing();
        SetError("3D 视口绘制失败，请重新打开页面：" + exception.Message);
        Log.Error("ProjectionPage.Render", exception);
    }

    private void DetachViewport()
    {
        CompositionTarget.Rendering -= Render;
        if (_viewportRoot is not null)
            _viewportRoot.Changed -= ViewportRoot_Changed;
        _viewportRoot = null;
        _projection.DetachRenderer();
        ViewportBrush.ImageSource = null;
        _viewportSurface = null;
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "导入投影";
        e.Handled = true;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<StorageFile>().FirstOrDefault(item =>
            item.Name.EndsWith(".litematic", StringComparison.OrdinalIgnoreCase) ||
            item.Name.EndsWith(".schem", StringComparison.OrdinalIgnoreCase));
        if (file is not null)
            await LoadProjectionAsync(file.Path);
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape &&
            Workspace.DisplayMode == SplitViewDisplayMode.Overlay &&
            Workspace.IsPaneOpen)
        {
            Workspace.IsPaneOpen = false;
            PanelButton.IsChecked = false;
            ViewportPanel.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }
        _keys.Add(e.Key);
        if (e.Key == VirtualKey.F)
        {
            FreeCameraButton.IsChecked = FreeCameraButton.IsChecked != true;
            FreeCamera_Click(FreeCameraButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void Page_KeyUp(object sender, KeyRoutedEventArgs e) => _keys.Remove(e.Key);

    private void Render(object? sender, object args)
    {
        if (!_active || !_projection.IsRendererCreated)
            return;
        var input = ProjectionMovementInput.None;
        if (_keys.Contains(VirtualKey.W)) input |= ProjectionMovementInput.Forward;
        if (_keys.Contains(VirtualKey.S)) input |= ProjectionMovementInput.Backward;
        if (_keys.Contains(VirtualKey.D)) input |= ProjectionMovementInput.Right;
        if (_keys.Contains(VirtualKey.A)) input |= ProjectionMovementInput.Left;
        if (_keys.Contains(VirtualKey.Space)) input |= ProjectionMovementInput.Up;
        if (_keys.Contains(VirtualKey.Control)) input |= ProjectionMovementInput.Down;
        if (_keys.Contains(VirtualKey.Shift)) input |= ProjectionMovementInput.Fast;
        if (_keys.Contains(VirtualKey.Menu)) input |= ProjectionMovementInput.Precise;
        var speedScale = MoveSpeedBox.SelectedIndex switch
        {
            0 => .25f,
            2 => 3f,
            _ => 1f
        };
        try
        {
            if (_projection.RenderFrame(input, speedScale, _busy) && !_firstFrameRendered)
            {
                _firstFrameRendered = true;
                HideViewportLoadingRing();
            }
        }
        catch (Exception exception)
        {
            StopViewport(exception);
        }
    }

    private void HideViewportLoadingRing()
    {
        ViewportLoadingRing.IsActive = false;
        ViewportLoadingRing.Visibility = Visibility.Collapsed;
    }

    private void UpdateCamera()
    {
        if (!_projection.IsRendererCreated)
            return;
        _projection.UpdateCamera();
        UpdateCameraStatus();
    }

    private void UpdateCameraStatus()
    {
        if (CameraModeText is null)
            return;
        var mode = FreeCameraButton.IsChecked == true ? "探索模式" : "环绕模式";
        var speed = FreeCameraButton.IsChecked == true
            ? $" · {SpeedLabel()}"
            : string.Empty;
        CameraModeText.Text = mode + speed;
        CameraHelpText.Text = FreeCameraButton.IsChecked == true
            ? "WASD 移动 · Space/Ctrl 上下 · Shift 加速 · Alt 精细移动 · 左键转向"
            : "左键旋转 · 右键平移 · 滚轮缩放";
    }

    private string SpeedLabel() => MoveSpeedBox.SelectedIndex switch
    {
        0 => "慢速",
        2 => "加速",
        _ => "正常速度"
    };

    private void MoveSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateCameraStatus();

    private void ResetSpeed_Click(object sender, RoutedEventArgs e)
    {
        MoveSpeedBox.SelectedIndex = 1;
        UpdateCameraStatus();
    }

    private void ClearScene()
    {
        _projection.Clear();
        _selectedClient = null;
        _selectedMaterialBlock = null;
        _expandedMaterials.Clear();
        MaterialsList.Items.Clear();
        MaterialsGrid.Items.Clear();
        MaterialDetailContent.Children.Clear();
        MaterialDetailExpander.Visibility = Visibility.Collapsed;
        MaterialSummaryText.Text = "等待载入投影";
        InfoText.Text = "尺寸：—\n非空气方块：—\nDataVersion：—";
        DetailsText.Text = "Region 和 Palette 详情将在载入后显示。";
        MemoryText.Text = "本进程内存：等待采样";
        WarningText.Visibility = Visibility.Collapsed;
        DiagnosticsExpander.Header = "技术诊断";
        FileText.Text = "请选择一个投影文件";
        FileSummaryText.Text = "未载入文件";
        ResourceSummaryText.Text = ResourceText.Text;
        SetSliceToolVisibility(false, animate: false);
        SliceToggleButton.IsChecked = false;
        SliceToggleButton.IsEnabled = false;
        ViewportStatusText.Text = "请选择一个投影文件。";
        ViewportHeaderStatusText.Text = "等待载入";
        LoadStatusText.Text = "等待载入";
        SetSliceState(SliceMode.Full, -1, int.MinValue, int.MaxValue);
        CaptureButton.IsEnabled = false;
        _materialRenderVersion++;
    }

    private void SetStatus(string text)
    {
        ViewportStatusText.Text = text;
        ViewportHeaderStatusText.Text = text;
        LoadStatusText.Text = text;
    }

    private void SetError(string text)
    {
        ViewportStatusText.Text = text;
        ViewportHeaderStatusText.Text = text;
        LoadStatusText.Text = text;
        WarningText.Text = text;
        WarningText.Visibility = Visibility.Visible;
        DiagnosticsExpander.IsExpanded = true;
    }

    private void UpdateResponsiveLayout()
    {
        WorkspaceTemplate.Adapt();
        PanelButton.IsChecked = Workspace.IsPaneOpen;
    }
}
