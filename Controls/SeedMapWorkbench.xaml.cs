using System.Globalization;
using LinxLauncher.Models;
using LinxLauncher.Services;
using SeedMap = RZeroF1.Minecraft.SeedMap.SeedMap;
using SeedMapGeometry = RZeroF1.Minecraft.SeedMap.SeedMapGeometry;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace LinxLauncher.Controls;

/// <summary>The same complete workbench is hosted by SeedMapPage and SeedMapWindow.</summary>
public sealed partial class SeedMapWorkbench : UserControl
{
    private readonly SeedMapSession _session = SeedMapSession.Current;
    private DispatcherQueueTimer? _save;
    private readonly Dictionary<int, SeedMap.BiomeInfo> _biomes = new();
    private readonly Dictionary<int, SeedMap.StructureInfo> _types = new();
    private readonly Dictionary<int, ListViewItem> _biomeRows = new();
    private readonly List<ListViewItem> _resultRows = new();
    private readonly Dictionary<int, CheckBox> _structureRows = new();
    private (int Version, int Dimension)? _capabilities;
    private bool _filling, _active, _narrow, _showNotice;
    private bool _immersive;
    private readonly TranslateTransform _toolbarOffset = new();
    private bool _syncingPin;
    private (SeedMapProfile Profile, SeedMapWaypoint Waypoint, int Index)? _deletedWaypoint;
    private Storyboard? _panelAnimation;
    private CancellationTokenSource? _operation;
    public Window? OwnerWindow { get; set; }
    public event Action? DetachRequested;
    public SeedMapViewport Map => Viewport;

    public SeedMapWorkbench()
    {
        InitializeComponent();
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => { if (!IsLoaded) Stop(); };
        SizeChanged += (_, _) => Adapt();
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(DismissStructureResults), true);
        Viewport.SizeChanged += (_, _) => StatusBar.Width = Math.Min(260, Math.Max(80, Viewport.ActualWidth - 80));
        Workspace.RegisterPropertyChangedCallback(SplitView.IsPaneOpenProperty,
            (_, _) => PanelButton.IsChecked = Workspace.IsPaneOpen);
        Viewport.StatusChanged += text => { _showNotice = true; StatusText.Text = text; };
        Viewport.LoadStatusChanged += text => { if (!_showNotice && _operation is null) StatusText.Text = text; };
        Viewport.HoverChanged += text => HoverText.Text = text;
        Viewport.CameraChanged += () => { UpdateCamera(); Changed(); };
        Viewport.WaypointRequested += async (x, z) => await EditWaypointAsync(null, x, z);
        Viewport.WaypointContextRequested += (waypoint, at) => WaypointMenu(waypoint).ShowAt(Viewport, at);
        Viewport.StructuresChanged += FillResults;
    }

    private void Start()
    {
        if (_active) return;
        _active = true;
        _save = DispatcherQueue.CreateTimer();
        _save.Interval = TimeSpan.FromMilliseconds(600); _save.IsRepeating = false;
        _save.Tick += Save_Tick;
        try
        {
            _session.Load(); _session.Changed += Session_Changed;
            ThemeService.AppearanceChanged += AppearanceChanged;
            ConfigurePresentation();
            _filling = true;
            if (VersionBox.Items.Count == 0)
                UiList.Fill(VersionBox, SeedMap.Versions.Select(v => new ComboBoxItem { Content = v.Key, Tag = v.Key }));
            _filling = false;
            Restore();
            Adapt(); AppearanceChanged();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; Log.Error("SeedMapStart", ex); }
    }

    public void Stop()
    {
        if (!_active) return;
        _active = false;
        if (_save is not null) { _save.Stop(); _save.Tick -= Save_Tick; _save = null; }
        _operation?.Cancel(); _operation = null;
        _panelAnimation?.Stop(); _panelAnimation = null;
        ResultsExpander.IsExpanded = false;
        WorldFlyout.Hide(); JumpFlyout.Hide(); HistoryButton.Flyout.Hide();
        _session.Changed -= Session_Changed;
        ThemeService.AppearanceChanged -= AppearanceChanged;
        Viewport.Release();
        Save();
        UiList.Clear(StructureResults);
        foreach (var row in _resultRows) { row.Tag = null; row.Content = null; }
    }
    public void Resume() => Start();
    private void Save_Tick(DispatcherQueueTimer sender, object args) => Save();

    private void Session_Changed(object? source)
    {
        if (!ReferenceEquals(source, this) && _active) Restore();
    }

    private void AppearanceChanged()
    {
        if (!_active) return;
        RequestedTheme = ThemeService.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        FontService.Apply(this);
        Viewport.Recolor();
        StatusBar.Background = SeedMapViewport.CreateChromeBrush();
        if (_immersive) SetChromeMaterial();
        UpdateFavorite();
    }

    private void Restore()
    {
        _operation?.Cancel();
        _showNotice = false;
        WaypointUndo.IsOpen = false; _deletedWaypoint = null;
        var p = _session.Profile;
        if (!SeedMap.Versions.Any(v => v.Key == p.Version))
            throw new InvalidOperationException($"不支持 Java {p.Version}；请选择能力表内的版本。");
        _filling = true;
        try
        {
            SeedBox.Text = p.SeedText;
            VersionBox.SelectedItem = VersionBox.Items.Cast<ComboBoxItem>().First(v => (string)v.Tag == p.Version);
            DimensionBox.SelectedIndex = p.Dimension switch { -1 => 1, 1 => 2, _ => 0 };
            HeightBox.Value = p.Y;
            GridButton.IsChecked = p.Grid; SlimeButton.IsChecked = p.Slime; SlimeButton.IsEnabled = p.Dimension == 0;
            FavoriteButton.IsChecked = p.Favorite; HideOthersBox.IsChecked = p.HideOtherBiomes;
            int version = SeedMap.Versions.First(v => v.Key == p.Version).Id;
            bool capabilitiesChanged = _capabilities != (version, p.Dimension);
            if (capabilitiesChanged)
            {
                _capabilities = (version, p.Dimension);
                _biomes.Clear(); _types.Clear(); _biomeRows.Clear(); _structureRows.Clear();
                foreach (var b in SeedMap.Biomes(version, p.Dimension)) _biomes[b.Id] = b;
                foreach (var t in SeedMap.Structures(version, p.Dimension)) _types[t.Id] = t;
                UiList.Fill(LocateBiome, _biomes.Values.Select(b => new ComboBoxItem { Content = b.Name, Tag = b.Id }));
                if (LocateBiome.Items.Count > 0) LocateBiome.SelectedIndex = 0;
            }
            FillBiomes();
            SelectedStructuresBox.IsChecked = p.OnlySelectedStructures;
            FillStructureTypes();
            FillWaypoints();
            Viewport.Configure(p); UpdateCamera();
        }
        finally { _filling = false; }
        bool auxiliary = OwnerWindow is Views.SeedMapWindow;
        DetachLabel.Text = auxiliary ? "返回启动器窗口" : "在新窗口打开";
        DetachIcon.Glyph = auxiliary ? "\uE72B" : "\uE8A7";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DetachButton, DetachLabel.Text);
        ToolTipService.SetToolTip(DetachButton, DetachLabel.Text);
        UpdateFavorite();
    }

    private void Adapt()
    {
        bool narrow = ActualWidth < 900;
        Workspace.DisplayMode = narrow || _immersive ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        Workspace.OpenPaneLength = Math.Min(320, Math.Max(260, ActualWidth - 32));
        StructureResults.MaxHeight = Math.Clamp(ActualHeight * .25, 64, 180);
        if (narrow != _narrow)
        {
            if (!_immersive) Workspace.IsPaneOpen = !narrow;
            _narrow = narrow;
        }
        bool toolbarNarrow = ActualWidth < 620 || _immersive;
        Grid.SetRow(SeedHost, 0); Grid.SetColumnSpan(SeedHost, toolbarNarrow ? 4 : 1);
        Grid.SetRow(VersionBox, toolbarNarrow ? 1 : 0); Grid.SetColumn(VersionBox, toolbarNarrow ? 0 : 1);
        Grid.SetRow(DimensionBox, toolbarNarrow ? 1 : 0); Grid.SetColumn(DimensionBox, toolbarNarrow ? 1 : 2);
        Grid.SetRow(HeightBox, toolbarNarrow ? 1 : 0); Grid.SetColumn(HeightBox, toolbarNarrow ? 2 : 3);
        DetachLabel.Visibility = ActualWidth < 540 || _immersive ? Visibility.Collapsed : Visibility.Visible;
        if (_immersive)
        {
            WorldBar.Width = Math.Clamp(ActualWidth - 80, 280, 760);
            CommandSurface.MaxWidth = Math.Max(0, ActualWidth - 24);
            MapCommands.DefaultLabelPosition = ActualWidth < 1100
                ? CommandBarDefaultLabelPosition.Collapsed : CommandBarDefaultLabelPosition.Right;
            ClampToolbar();
        }
        UpdateCamera();
    }

    private void ConfigurePresentation()
    {
        if (_immersive || OwnerWindow is not Views.SeedMapWindow) return;
        _immersive = true;
        Root.Children.Remove(WorldBar);
        WorldFlyout.Content = WorldBar;
        WorldBar.ColumnDefinitions[0].Width = WorldBar.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        WorldBar.ColumnDefinitions[2].Width = new GridLength(84);
        WorldBar.ColumnDefinitions[3].Width = new GridLength(0);
        WorldSettingsButton.Visibility = PinButton.Visibility = Visibility.Visible;
        DetachButton.Visibility = Visibility.Collapsed;
        ToolbarGrip.Visibility = Visibility.Visible;
        Root.Children.Remove(Workspace); Root.Children.Insert(0, Workspace);
        Root.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        for (int i = 1; i < Root.RowDefinitions.Count; i++) Root.RowDefinitions[i].Height = new GridLength(0);
        Root.RowSpacing = 0;
        Grid.SetRow(Workspace, 0);
        Grid.SetRow(CommandSurface, 0);
        CommandSurface.HorizontalAlignment = HorizontalAlignment.Right;
        CommandSurface.VerticalAlignment = VerticalAlignment.Top;
        CommandSurface.Margin = new Thickness(12, 56, 12, 0);
        CommandSurface.CornerRadius = new CornerRadius(8);
        CommandSurface.RenderTransform = _toolbarOffset;
        CommandSurface.SizeChanged += (_, _) => ClampToolbar();
        CommandSurface.BorderThickness = new Thickness(1);
        CommandSurface.BorderBrush = ThemeService.Brush("CardStrokeColorDefaultBrush");
        MapCommands.HorizontalAlignment = HorizontalAlignment.Right;
        Workspace.IsPaneOpen = false;
        if (Workspace.Pane is Grid pane) pane.Padding = new Thickness(12, 112, 12, 48);
        MapBorder.Margin = new Thickness(0); MapBorder.CornerRadius = new CornerRadius(0);
        MapBorder.BorderThickness = new Thickness(0);
        SetChromeMaterial();
    }

    private void DismissStructureResults(object sender, PointerRoutedEventArgs e)
    {
        if (!ResultsExpander.IsExpanded) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ResultsExpander)) return;
        ResultsExpander.IsExpanded = false;
    }

    internal void SetChromeMaterial()
    {
        CommandSurface.Background = SeedMapViewport.CreateChromeBrush();
        StatusBar.Background = SeedMapViewport.CreateChromeBrush();
        Viewport.RefreshChromeMaterial();
    }

    private void ClampToolbar()
    {
        if (!_immersive) return;
        double left = Math.Max(12, ActualWidth - 12 - CommandSurface.ActualWidth);
        _toolbarOffset.X = Math.Clamp(_toolbarOffset.X, 12 - left, 0);
        _toolbarOffset.Y = Math.Clamp(_toolbarOffset.Y, 0, Math.Max(0, ActualHeight - CommandSurface.ActualHeight - 68));
    }

    private void ToolbarGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _toolbarOffset.X += e.HorizontalChange;
        _toolbarOffset.Y += e.VerticalChange;
        ClampToolbar();
    }

    private void ToolbarGrip_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left: _toolbarOffset.X -= 16; break;
            case Windows.System.VirtualKey.Right: _toolbarOffset.X += 16; break;
            case Windows.System.VirtualKey.Up: _toolbarOffset.Y -= 16; break;
            case Windows.System.VirtualKey.Down: _toolbarOffset.Y += 16; break;
            case Windows.System.VirtualKey.Home: _toolbarOffset.X = _toolbarOffset.Y = 0; break;
            default: return;
        }
        ClampToolbar(); e.Handled = true;
    }

    private void Pin_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingPin || OwnerWindow is not Views.SeedMapWindow window) return;
        try { window.SetAlwaysOnTop(PinButton.IsChecked == true); }
        catch (Exception ex) { StatusText.Text = "置顶失败：" + ex.Message; }
        finally
        {
            _syncingPin = true;
            PinButton.IsChecked = window.IsAlwaysOnTop;
            _syncingPin = false;
            ToolTipService.SetToolTip(PinButton, window.IsAlwaysOnTop ? "取消置顶" : "置于其他窗口上方");
        }
    }

    private void ApplyWorld()
    {
        if (_filling || !_active) return;
        try
        {
            var version = (VersionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "1.20.6";
            int dim = int.Parse((DimensionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "0");
            _session.Select(SeedBox.Text, version, dim, double.IsFinite(HeightBox.Value) ? (int)HeightBox.Value : 64, this);
            Restore(); Changed();
            StatusText.Text = $"种子 {_session.Profile.Seed}";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void World_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyWorld();
    private void Height_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e) => ApplyWorld();
    private void Seed_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e) => ApplyWorld();
    private void Seed_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        sender.Items.Clear();
        foreach (var p in _session.Workspace.Profiles.Where(p => (p.Favorite || p.InHistory) &&
            p.SeedText.Contains(sender.Text, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Favorite).Take(12))
            sender.Items.Add(new ListViewItem { Content = $"{(p.Favorite ? "★ " : "")}{p.SeedText} · {p.Version}", Tag = p });
    }
    private void Seed_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs e)
    {
        if (e.SelectedItem is ListViewItem { Tag: SeedMapProfile p }) { _session.Restore(p, this); Restore(); Changed(); }
    }
    private void History_Click(object sender, RoutedEventArgs e)
    {
        FillHistory();
    }
    private void HistoryTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_active) FillHistory();
    }
    private void FillHistory()
    {
        bool favorites = HistoryTabs.SelectedItem == FavoritesTab;
        UiList.Fill(HistoryList, _session.Workspace.Profiles.Where(p => favorites ? p.Favorite : p.InHistory)
            .OrderByDescending(p => p.LastUsedUtc));
        ClearHistoryButton.IsEnabled = HistoryList.Items.Count > 0;
        HistoryEmpty.Text = favorites ? "暂无收藏" : "暂无历史";
        HistoryEmpty.Visibility = HistoryList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    public static string FormatHistory(string seed, string version, int dimension) =>
        $"{seed} · {version} · {dimension switch { -1 => "下界", 1 => "末地", _ => "主世界" }}";

    private void HistoryItem_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SeedMapProfile profile) OpenHistory(profile);
    }
    private void OpenHistory(SeedMapProfile profile)
    {
        HistoryFlyout.Hide();
        WorldFlyout.Hide();
        _session.Restore(profile, this); Restore(); Changed(); Save();
    }
    private void HistoryList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var row = args.ItemContainer;
        row.ContextFlyout = null;
        if (args.InRecycleQueue || args.Item is not SeedMapProfile profile) return;
        bool favorites = HistoryTabs.SelectedItem == FavoritesTab;
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = (Style)Resources["SeedMapMenuStyle"] };
        var open = new MenuFlyoutItem { Text = "打开", Icon = new SymbolIcon(Symbol.OpenFile) };
        open.Click += (_, _) => OpenHistory(profile);
        menu.Items.Add(open);
        if (!favorites)
        {
            var favorite = new MenuFlyoutItem
            {
                Text = profile.Favorite ? "取消收藏" : "收藏",
                Icon = new SymbolIcon(profile.Favorite ? Symbol.UnFavorite : Symbol.Favorite)
            };
            favorite.Click += (_, _) =>
            {
                profile.Favorite = !profile.Favorite;
                SaveHistoryChange();
            };
            menu.Items.Add(favorite);
        }
        var remove = new MenuFlyoutItem { Text = "移除", Icon = new SymbolIcon(Symbol.Delete) };
        remove.Click += (_, _) =>
        {
            if (favorites) profile.Favorite = false;
            else profile.InHistory = false;
            SaveHistoryChange();
        };
        menu.Items.Add(remove);
        row.ContextFlyout = menu;
        ToolTipService.SetToolTip(row, FormatHistory(profile.SeedText, profile.Version, profile.Dimension));
    }
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        bool favorites = HistoryTabs.SelectedItem == FavoritesTab;
        foreach (var profile in _session.Workspace.Profiles)
            if (favorites) profile.Favorite = false;
            else profile.InHistory = false;
        SaveHistoryChange();
    }
    private void SaveHistoryChange()
    {
        UpdateFavorite();
        Changed(); Save(); FillHistory();
    }
    private async void Jump_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadCoordinate(JumpX, out double x) || !TryReadCoordinate(JumpZ, out double z))
        {
            JumpError.Text = "请输入 −29,999,999 至 29,999,999 范围内的 X/Z 坐标。";
            JumpError.Visibility = Visibility.Visible;
            return;
        }
        JumpFlyout.Hide();
        await Viewport.NavigateToAsync(x, z, "定位");
    }
    private void JumpFlyout_Opening(object sender, object e)
    {
        JumpX.Text = Math.Round(_session.Profile.CenterX).ToString("0", CultureInfo.InvariantCulture);
        JumpZ.Text = Math.Round(_session.Profile.CenterZ).ToString("0", CultureInfo.InvariantCulture);
        JumpX.Select(0, 0);
        JumpZ.Select(0, 0);
        JumpError.Visibility = Visibility.Collapsed;
    }
    private void JumpFlyout_Opened(object sender, object e)
    {
        // AppBarButton defaults AllowFocusOnInteraction to false. Override it
        // on the entry and editors, then start on the presenter, not an editor.
        for (DependencyObject? node = JumpX; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FlyoutPresenter presenter)
            {
                presenter.Focus(FocusState.Programmatic);
                break;
            }
        JumpX.Select(0, 0);
        JumpZ.Select(0, 0);
    }
    private void JumpCoordinate_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        Jump_Click(sender, e);
        e.Handled = true;
    }
    private static bool TryReadCoordinate(TextBox box, out double value) =>
        double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value) && value >= -SeedMap.WorldLimit + 1 && value <= SeedMap.WorldLimit - 1;

    private static bool TryReadNumber(NumberBox box, out double value)
    {
        // Waypoint and export dialogs still use NumberBox. Read its current
        // editor text before it commits Value on focus loss.
        static TextBox? Editor(DependencyObject node)
        {
            if (node is TextBox textBox) return textBox;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                if (Editor(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
            return null;
        }
        string text = Editor(box)?.Text ?? box.Text;
        value = (box.NumberFormatter as Windows.Globalization.NumberFormatting.INumberParser)?.ParseDouble(text) ?? double.NaN;
        return double.IsFinite(value) && value >= box.Minimum && value <= box.Maximum;
    }
    private void UpdateCamera()
    {
        var p = _session.Profile;
        CameraText.Text = $"{p.CenterX:N0}, {p.CenterZ:N0} · {p.PixelsPerBlock * 100:F1}%";
    }
    private void Changed()
    {
        if (_filling || !_active) return;
        _session.Notify(this); _save?.Stop(); _save?.Start();
    }
    private void Save()
    {
        try { _session.Save(); } catch (Exception ex) { StatusText.Text = "保存失败：" + ex.Message; Log.Error("SeedMapSave", ex); }
    }
    private void Panel_Click(object sender, RoutedEventArgs e) => Workspace.IsPaneOpen = PanelButton.IsChecked == true;
    private void Detach_Click(object sender, RoutedEventArgs e)
    {
        if (OwnerWindow is Views.SeedMapWindow window) window.ReturnToLauncher();
        else DetachRequested?.Invoke();
    }
    private void Layers_Click(object sender, RoutedEventArgs e)
    {
        _session.Profile.Grid = GridButton.IsChecked == true;
        _session.Profile.Slime = SlimeButton.IsChecked == true;
        Viewport.RefreshLayers(); Changed();
        if ((_session.Profile.Grid || _session.Profile.Slime) && _session.Profile.PixelsPerBlock < .5)
            StatusText.Text = "放大至 50% 后显示区块覆盖层。";
    }
    private void Measure_Click(object sender, RoutedEventArgs e)
    {
        Viewport.SetMeasuring(MeasureButton.IsChecked == true);
        _showNotice = true;
        StatusText.Text = Viewport.Measuring ? "点击地图添加测量点；Esc 清除。" : "已退出测量";
    }
    private void ClearMeasurement_Click(object sender, RoutedEventArgs e) => Viewport.ClearMeasurement();
    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        _session.Profile.Favorite = FavoriteButton.IsChecked == true;
        UpdateFavorite(); Changed(); Save();
    }
    private void UpdateFavorite()
    {
        FavoriteIcon.Glyph = _session.Profile.Favorite ? "\uE735" : "\uE734";
        // This is a semantic map/favorite color, independent of the app accent.
        FavoriteIcon.Foreground = _session.Profile.Favorite
            ? new SolidColorBrush(Microsoft.UI.Colors.Gold) : ThemeService.Brush("PrimaryTextBrush");
        ToolTipService.SetToolTip(FavoriteButton, _session.Profile.Favorite ? "取消收藏" : "收藏种子");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FavoriteButton,
            _session.Profile.Favorite ? "取消收藏" : "收藏种子");
    }

    private void Tools_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        if (BiomePanel is null || StructurePanel is null || MarkerPanel is null) return;
        Grid selected = sender.SelectedItem == StructuresTab ? StructurePanel : sender.SelectedItem == MarkersTab ? MarkerPanel : BiomePanel;
        _panelAnimation?.Stop();
        foreach (var panel in new[] { BiomePanel, StructurePanel, MarkerPanel })
        {
            panel.Opacity = 1;
            panel.Visibility = panel == selected ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_active && new UISettings().AnimationsEnabled)
        {
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(160) };
            Storyboard.SetTarget(fade, selected); Storyboard.SetTargetProperty(fade, "Opacity");
            _panelAnimation = new Storyboard(); _panelAnimation.Children.Add(fade); _panelAnimation.Begin();
        }
        if (_active) FontService.Apply(selected);
    }

    private void FillBiomes()
    {
        bool previous = _filling; _filling = true;
        try
        {
            string text = BiomeFilter.Text?.Trim() ?? "";
            var rows = _biomes.Values.Where(b => b.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || b.Key.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Select(b =>
                {
                    if (!_biomeRows.TryGetValue(b.Id, out var item))
                    {
                        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                        row.Children.Add(new Border
                        {
                            Width = 12,
                            Height = 12,
                            CornerRadius = new CornerRadius(2),
                            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(b.Color >> 16), (byte)(b.Color >> 8), (byte)b.Color))
                        });
                        row.Children.Add(new TextBlock { Text = b.Name, FontFamily = FontService.BodyFont });
                        item = new ListViewItem { Content = row, Tag = b.Id };
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, b.Name);
                        _biomeRows.Add(b.Id, item);
                    }
                    item.IsSelected = _session.Profile.Biomes.Contains(b.Id);
                    return item;
                }).ToArray();
            UiList.Fill(BiomeList, rows);
            foreach (var item in rows) item.IsSelected = _session.Profile.Biomes.Contains((int)item.Tag);
        }
        finally { _filling = previous; }
    }
    private void BiomeFilter_Changed(object sender, TextChangedEventArgs e) { if (_active) FillBiomes(); }
    private void BiomeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        foreach (ListViewItem row in e.AddedItems) if (!_session.Profile.Biomes.Contains((int)row.Tag)) _session.Profile.Biomes.Add((int)row.Tag);
        foreach (ListViewItem row in e.RemovedItems) _session.Profile.Biomes.Remove((int)row.Tag);
        Viewport.Recolor(); Changed();
    }
    private void ClearBiomes_Click(object sender, RoutedEventArgs e) { _session.Profile.Biomes.Clear(); FillBiomes(); Viewport.Recolor(); Changed(); }
    private void SelectAllBiomes_Click(object sender, RoutedEventArgs e)
    {
        foreach (ListViewItem row in BiomeList.Items)
            if (!_session.Profile.Biomes.Contains((int)row.Tag)) _session.Profile.Biomes.Add((int)row.Tag);
        FillBiomes(); Viewport.Recolor(); Changed();
    }
    private void HideOthers_Click(object sender, RoutedEventArgs e) { _session.Profile.HideOtherBiomes = HideOthersBox.IsChecked == true; Viewport.Recolor(); Changed(); }
    private void StructureType_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || _filling) return;
        int id = (int)box.Tag;
        if (box.IsChecked == true) { if (!_session.Profile.Structures.Contains(id)) _session.Profile.Structures.Add(id); }
        else _session.Profile.Structures.Remove(id);
        FillStructureTypes(); Viewport.RefreshLayers(); Changed();
    }
    private void FillStructureTypes()
    {
        var p = _session.Profile;
        string query = StructureFilter.Text?.Trim() ?? "";
        var rows = _types.Values.Where(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                (!p.OnlySelectedStructures || p.Structures.Contains(t.Id))).Select(t =>
            {
                if (!_structureRows.TryGetValue(t.Id, out var box))
                {
                    box = new CheckBox { Content = t.Name, Tag = t.Id };
                    box.Click += StructureType_Click; _structureRows.Add(t.Id, box);
                }
                box.IsChecked = p.Structures.Contains(t.Id);
                return box;
            }).ToArray();
        UiList.Fill(StructureTypes, rows);
    }
    private void StructureFilter_Changed(object sender, TextChangedEventArgs e) { if (_active) FillStructureTypes(); }
    private void SelectedStructures_Click(object sender, RoutedEventArgs e)
    {
        _session.Profile.OnlySelectedStructures = SelectedStructuresBox.IsChecked == true;
        FillStructureTypes(); Changed();
    }
    private void SelectAllStructures_Click(object sender, RoutedEventArgs e)
    {
        foreach (var type in _types.Values)
            if (!_session.Profile.Structures.Contains(type.Id)) _session.Profile.Structures.Add(type.Id);
        FillStructureTypes(); Viewport.RefreshLayers(); Changed();
    }
    private void ClearStructures_Click(object sender, RoutedEventArgs e)
    {
        _session.Profile.Structures.Clear();
        FillStructureTypes(); Viewport.RefreshLayers(); Changed();
    }
    private void FillResults(IReadOnlyList<SeedMap.StructureHit> hits)
    {
        var p = _session.Profile;
        var ordered = hits.OrderBy(h => SeedMapGeometry.Distance(h.X, h.Z, p.CenterX, p.CenterZ)).Take(300).ToArray();
        ResultsExpander.Header = $"地图中的结构 · {ordered.Length}";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ResultsExpander, (string)ResultsExpander.Header);
        while (_resultRows.Count < ordered.Length)
        {
            var row = new ListViewItem();
            row.RightTapped += StructureResult_RightTapped;
            var menu = new MenuFlyout { MenuFlyoutPresenterStyle = (Style)Resources["SeedMapMenuStyle"] };
            var locate = new MenuFlyoutItem { Text = "导航到此处", Icon = new SymbolIcon(Symbol.MapPin) };
            locate.Click += async (_, _) => { if (row.Tag is SeedMap.StructureHit h) await NavigateStructureAsync(h); };
            var copy = new MenuFlyoutItem { Text = "复制坐标", Icon = new SymbolIcon(Symbol.Copy) };
            copy.Click += (_, _) => { if (row.Tag is SeedMap.StructureHit h) SeedMapViewport.Copy(h.X, h.Z); };
            var mark = new MenuFlyoutItem { Text = "添加路径点", Icon = new SymbolIcon(Symbol.MapPin) };
            mark.Click += async (_, _) => { if (row.Tag is SeedMap.StructureHit h) await EditWaypointAsync(null, h.X, h.Z); };
            menu.Items.Add(locate); menu.Items.Add(copy); menu.Items.Add(mark); row.ContextFlyout = menu;
            _resultRows.Add(row);
        }
        for (int i = 0; i < _resultRows.Count; i++)
        {
            var row = _resultRows[i];
            if (i < ordered.Length)
            {
                var hit = ordered[i];
                row.Tag = hit;
                row.Content = $"{_types.GetValueOrDefault(hit.Type)?.Name ?? "结构"}  ·  {hit.X}, {hit.Z}";
            }
            else { row.Tag = null; row.Content = null; }
        }
        if (StructureResults.Items.Count != ordered.Length)
            UiList.Fill(StructureResults, _resultRows.Take(ordered.Length));
    }
    private void StructureResult_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is ListViewItem row) StructureResults.SelectedItem = row;
    }
    private async void StructureResult_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ListViewItem { Tag: SeedMap.StructureHit h }) await NavigateStructureAsync(h);
    }
    private async Task NavigateStructureAsync(SeedMap.StructureHit hit)
    {
        ResultsExpander.IsExpanded = false;
        if (Workspace.DisplayMode == SplitViewDisplayMode.Overlay) Workspace.IsPaneOpen = false;
        await Viewport.NavigateToAsync(hit.X, hit.Z, _types.GetValueOrDefault(hit.Type)?.Name ?? "结构");
    }
    private CancellationTokenSource BeginOperation()
    {
        _operation?.Cancel(); _operation = new(); return _operation;
    }
    private async void FindBiome_Click(object sender, RoutedEventArgs e)
    {
        if (LocateBiome.SelectedItem is not ComboBoxItem { Tag: int id } || Viewport.Engine is not { } engine) return;
        using var cts = BeginOperation();
        var p = _session.Profile;
        try
        {
            _showNotice = true; StatusText.Text = "查找群系中…";
            var found = await engine.FindBiomeAsync(id, (int)p.CenterX, (int)p.CenterZ, 1024, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (found is { } at)
            {
                StatusText.Text = $"找到 {_biomes[id].Name} · X {at.X} · Z {at.Z}";
                if (Workspace.DisplayMode == SplitViewDisplayMode.Overlay) Workspace.IsPaneOpen = false;
                await Viewport.ShowSearchResultAsync(at.X, at.Z, _biomes[id].Name, cts.Token);
            }
            else StatusText.Text = "1024 格范围、32 格采样内未找到；较小群系可能漏过。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { if (ReferenceEquals(_operation, cts)) _operation = null; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel(); Viewport.CancelFlight(); _showNotice = false; StatusText.Text = "已取消";
    }

    private void FillWaypoints() => UiList.Fill(WaypointList, _session.Profile.Waypoints.Where(w => w.Dimension == _session.Profile.Dimension)
        .Select(w =>
        {
            var row = new ListViewItem { Content = $"{w.Name}  ·  {w.X}, {w.Z}", Tag = w, ContextFlyout = WaypointMenu(w) };
            row.RightTapped += (_, _) => WaypointList.SelectedItem = row;
            return row;
        }));
    private MenuFlyout WaypointMenu(SeedMapWaypoint waypoint)
    {
        var menu = new MenuFlyout();
        void Add(string text, Symbol icon, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon) };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Add("定位", Symbol.MapPin, async () =>
        {
            if (Workspace.DisplayMode == SplitViewDisplayMode.Overlay) Workspace.IsPaneOpen = false;
            await Viewport.NavigateToAsync(waypoint.X, waypoint.Z, waypoint.Name);
        });
        Add("复制坐标", Symbol.Copy, () => SeedMapViewport.Copy(waypoint.X, waypoint.Z));
        Add("编辑", Symbol.Edit, async () => await EditWaypointAsync(waypoint, waypoint.X, waypoint.Z));
        Add("从此处测距", Symbol.Edit, async () =>
        {
            MeasureButton.IsChecked = true; Viewport.SetMeasuring(true); Viewport.ClearMeasurement();
            Viewport.AddMeasurementPoint(waypoint.X, waypoint.Z);
            if (Workspace.DisplayMode == SplitViewDisplayMode.Overlay) Workspace.IsPaneOpen = false;
            await Viewport.NavigateToAsync(waypoint.X, waypoint.Z, waypoint.Name);
        });
        Add("测量到此处", Symbol.Edit, () => Viewport.AddMeasurementPoint(waypoint.X, waypoint.Z));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("删除", Symbol.Delete, () => DeleteWaypoint(waypoint));
        menu.Opening += (_, _) =>
        {
            // Menu content is refreshed on open so measurement actions follow current state.
            var to = menu.Items.OfType<MenuFlyoutItem>().FirstOrDefault(i => i.Text == "测量到此处");
            if (to is not null) to.Visibility = Viewport.Measuring && Viewport.MeasurementPointCount > 0
                ? Visibility.Visible : Visibility.Collapsed;
        };
        return menu;
    }
    private async void Waypoint_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ListViewItem { Tag: SeedMapWaypoint w }) return;
        if (Workspace.DisplayMode == SplitViewDisplayMode.Overlay) Workspace.IsPaneOpen = false;
        await Viewport.NavigateToAsync(w.X, w.Z, w.Name);
    }
    private async void AddWaypoint_Click(object sender, RoutedEventArgs e) => await EditWaypointAsync(null, (int)_session.Profile.CenterX, (int)_session.Profile.CenterZ);
    private async void EditWaypoint_Click(object sender, RoutedEventArgs e)
    {
        if (WaypointList.SelectedItem is ListViewItem { Tag: SeedMapWaypoint w }) await EditWaypointAsync(w, w.X, w.Z);
    }
    private async Task EditWaypointAsync(SeedMapWaypoint? existing, int x, int z)
    {
        if (XamlRoot is null) return;
        var p = _session.Profile;
        var name = new TextBox { Header = "名称", Text = existing?.Name ?? "路径点" };
        var xb = new NumberBox { Header = "X", Value = x, Minimum = -29999999, Maximum = 29999999 };
        var yb = new NumberBox { Header = "Y", Value = existing?.Y ?? p.Y, Minimum = -64, Maximum = 320 };
        var zb = new NumberBox { Header = "Z", Value = z, Minimum = -29999999, Maximum = 29999999 };
        var note = new TextBox { Header = "备注", Text = existing?.Note ?? "", MaxLength = 1000 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = existing is null ? "添加路径点" : "编辑路径点",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            Content = new StackPanel { Spacing = 8, MinWidth = 240, Children = { name, xb, yb, zb, note } }
        };
        FontService.ApplyToDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !_active || !ReferenceEquals(p, _session.Profile)) return;
        if (string.IsNullOrWhiteSpace(name.Text) || !TryReadNumber(xb, out double wx) ||
            !TryReadNumber(yb, out double wy) || !TryReadNumber(zb, out double wz)) { StatusText.Text = "名称和坐标无效。"; return; }
        var waypoint = existing ?? new SeedMapWaypoint { Dimension = p.Dimension };
        waypoint.Name = name.Text.Trim(); waypoint.X = (int)wx; waypoint.Y = (int)wy; waypoint.Z = (int)wz; waypoint.Note = note.Text;
        if (existing is null) p.Waypoints.Add(waypoint);
        FillWaypoints(); Viewport.RefreshLayers(); Changed(); Save();
    }
    private void DeleteWaypoint_Click(object sender, RoutedEventArgs e)
    {
        if (WaypointList.SelectedItem is not ListViewItem { Tag: SeedMapWaypoint w }) return;
        DeleteWaypoint(w);
    }
    private void DeleteWaypoint(SeedMapWaypoint waypoint)
    {
        var p = _session.Profile;
        int index = p.Waypoints.IndexOf(waypoint);
        if (index < 0) return;
        _deletedWaypoint = (p, waypoint, index);
        p.Waypoints.RemoveAt(index);
        WaypointUndo.Message = $"已删除“{waypoint.Name}”"; WaypointUndo.IsOpen = true;
        FillWaypoints(); Viewport.RefreshLayers(); Changed(); Save();
    }
    private void UndoWaypoint_Click(object sender, RoutedEventArgs e)
    {
        if (_deletedWaypoint is not { } deleted || !ReferenceEquals(deleted.Profile, _session.Profile)) return;
        deleted.Profile.Waypoints.Insert(Math.Min(deleted.Index, deleted.Profile.Waypoints.Count), deleted.Waypoint);
        _deletedWaypoint = null; WaypointUndo.IsOpen = false;
        FillWaypoints(); Viewport.RefreshLayers(); Changed(); Save();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Viewport.Engine is null || OwnerWindow is null) return;
        var bounds = Viewport.VisibleBounds();
        NumberBox x0 = new() { Header = "最小 X", Value = bounds.MinX }, z0 = new() { Header = "最小 Z", Value = bounds.MinZ };
        NumberBox x1 = new() { Header = "最大 X", Value = bounds.MaxX }, z1 = new() { Header = "最大 Z", Value = bounds.MaxZ };
        var resolution = new ComboBox { Header = "每像素方块数", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (int step in new[] { 1, 4, 16, 64, 256 }) resolution.Items.Add(new ComboBoxItem { Content = step.ToString(), Tag = step });
        resolution.SelectedIndex = _session.Profile.PixelsPerBlock >= 1 ? 0 : _session.Profile.PixelsPerBlock >= .25 ? 1 : 2;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导出 PNG",
            PrimaryButtonText = "导出",
            CloseButtonText = "取消",
            Content = new StackPanel { Spacing = 8, MinWidth = 260, Children = { x0, z0, x1, z1, resolution } }
        };
        FontService.ApplyToDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !_active) return;
        if (!TryReadNumber(x0, out double minX) || !TryReadNumber(z0, out double minZ) ||
            !TryReadNumber(x1, out double maxX) || !TryReadNumber(z1, out double maxZ) ||
            new[] { minX, minZ, maxX, maxZ }.Any(v => v < -SeedMap.WorldLimit || v > SeedMap.WorldLimit)) { StatusText.Text = "导出坐标无效。"; return; }
        bounds = new((int)minX, (int)minZ, (int)maxX, (int)maxZ);
        int sample = (int)((ComboBoxItem)resolution.SelectedItem).Tag;
        if (bounds.MaxX <= bounds.MinX || bounds.MaxZ <= bounds.MinZ ||
            (long)Math.Ceiling((bounds.MaxX - bounds.MinX) / (double)sample) * Math.Ceiling((bounds.MaxZ - bounds.MinZ) / (double)sample) > 16_777_216) { StatusText.Text = "导出范围无效或超过 1600 万像素，请缩小范围或分辨率。"; return; }
        var picker = new FileSavePicker { SuggestedFileName = $"seed-{_session.Profile.Seed}-{bounds.MinX}-{bounds.MinZ}" };
        picker.FileTypeChoices.Add("PNG", new List<string> { ".png" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(OwnerWindow));
        var file = await picker.PickSaveFileAsync(); if (file is null || !_active) return;
        using var cts = BeginOperation();
        try
        {
            StatusText.Text = "导出中…";
            var world = Viewport.Engine!.World;
            // Capture the palette/filter now; UI changes cannot alter a running export.
            var colors = Enumerable.Range(0, 256).Select(Viewport.PixelColor).ToArray();
            var image = await SeedMapExport.RenderAsync(world, bounds, sample, colors, _session.Profile, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            stream.Size = 0;
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)image.Width, (uint)image.Height, 96, 96, image.Pixels);
            await encoder.FlushAsync();
            StatusText.Text = $"已导出 {image.Width} × {image.Height}" + (image.StructuresLimited ? " · 部分密集结构已省略" : "");
        }
        catch (OperationCanceledException) { StatusText.Text = "导出已取消"; }
        catch (Exception ex) { StatusText.Text = "导出失败：" + ex.Message; }
        finally { if (ReferenceEquals(_operation, cts)) _operation = null; }
    }
    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "种子地图",
            CloseButtonText = "关闭",
            Content = new TextBlock { Text = "Java 1.16.5 – 1.21.3 · Cubiomes\n固定 Y 高度的群系切片；低倍率为群系采样网格。\n结构为算法预测，不能证明完整方块世界中的生成结果。\n史莱姆区块不代表已满足 Y、光照等生成条件。\n超出 64 位范围的数字按 Java 文本种子处理。\n\nMIT · Cubitect\n" + SeedMap.Revision, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }
        };
        FontService.ApplyToDialog(dialog); await dialog.ShowAsync();
    }
}
