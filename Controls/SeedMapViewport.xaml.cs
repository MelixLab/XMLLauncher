using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using LinxLauncher.Services;
using SeedMap = RZeroF1.Minecraft.SeedMap;
using SeedMapProfile = LinxLauncher.Models.SeedMapProfile;
using SeedMapWaypoint = LinxLauncher.Models.SeedMapWaypoint;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace LinxLauncher.Controls;

/// <summary>Shared page/window viewport; only this control owns map images and workers.</summary>
public sealed partial class SeedMapViewport : UserControl
{
    private sealed record VisualTile(int[] Ids, Image Image);
    // WinRT owns the pixel storage. Its small managed wrapper does not describe
    // that native allocation to GC; keep pressure until the wrapper is collected.
    private sealed class BitmapMemory
    {
        private const long Bytes = SeedMap.Engine.TileSize * SeedMap.Engine.TileSize * 4;
        public BitmapMemory() => GC.AddMemoryPressure(Bytes);
        ~BitmapMemory() => GC.RemoveMemoryPressure(Bytes);
    }
    private static readonly ConditionalWeakTable<WriteableBitmap, BitmapMemory> BitmapAllocations = new();
    private readonly Dictionary<SeedMap.Engine.TileKey, VisualTile> _tiles = new();
    private readonly Stack<Image> _imagePool = new();
    private readonly Stack<Rectangle> _slimePool = new();
    private readonly Stack<Line> _linePool = new();
    private readonly Stack<Ellipse> _dotPool = new();
    private readonly Stack<Border> _labelPool = new();
    private readonly List<SeedMap.StructureHit> _displayedStructures = new();
    private readonly Dictionary<SeedMap.Engine.TileKey, CancellationTokenSource> _pending = new();
    private HashSet<SeedMap.Engine.TileKey> _wanted = new();
    private HashSet<SeedMap.Engine.TileKey> _visible = new();
    private int _tileStep;
    private bool _pumping, _tilePumpQueued;
    private readonly Dictionary<int, SeedMap.BiomeInfo> _biomes = new();
    private readonly Dictionary<int, SeedMap.StructureInfo> _types = new();
    private DispatcherQueueTimer? _refresh;
    private DispatcherQueueTimer? _structureRefresh;
    private DispatcherQueueTimer? _zoomAnimation;
    private readonly Stopwatch _zoomClock = new();
    private double _zoomStart, _zoomTarget;
    private int _zoomDirection;
    private Point _zoomAnchor, _zoomWorld;
    private CancellationTokenSource? _structuresCts;
    private long _structureRequestId;
    private CancellationTokenSource? _flight;
    private (int X, int Z, string Name)? _searchResult;
    private SeedMap.StructureHit? _hoveredStructure;
    private SeedMap.StructureHit? _selectedStructure;
    private SeedMap.Engine? _engine;
    private SeedMapProfile? _profile;
    private List<SeedMap.StructureHit> _structures = new();
    private readonly List<Point> _measure = new();
    private Point _press, _last, _context;
    private bool _dragging, _moved;
    private int _worldGeneration;
    private double _overlayX, _overlayZ;
    private double _overlayScale = 1, _overlayWidth, _overlayHeight;
    private bool? _crosshairOnDark;
    public bool Measuring { get; set; }
    public int MeasurementPointCount => _measure.Count;
    public event Action<string>? StatusChanged;
    public event Action<string>? LoadStatusChanged;
    public event Action<string>? HoverChanged;
    public event Action? CameraChanged;
    public event Action<int, int>? WaypointRequested;
    public event Action<SeedMapWaypoint, Point>? WaypointContextRequested;
    public event Action<IReadOnlyList<SeedMap.StructureHit>>? StructuresChanged;
    public SeedMap.Engine? Engine => _engine;
    public SeedMapProfile? Profile => _profile;
    public int VisibleTileCount => _tiles.Count;
    public int PendingTileCount => _pending.Count;
    internal bool IsFlying => _flight is not null || _zoomAnimation?.IsRunning == true;
    internal (int X, int Z, string Name)? SearchResult => _searchResult;
    internal int PrefetchedTileCount => _tiles.Keys.Count(k => k.Step == CurrentStep() && !_visible.Contains(k));
    internal int MissingVisibleTileCount => _visible.Count(k => !_tiles.ContainsKey(k));
    internal bool HasVisibleCoverage
    {
        get
        {
            if (_profile is null) return false;
            int step = CurrentStep(), span = SeedMap.Engine.TileSize * step;
            var bounds = VisibleBounds();
            for (int z = SeedMap.Api.FloorDiv(bounds.MinZ, span); z <= SeedMap.Api.FloorDiv(bounds.MaxZ - 1, span); z++)
                for (int x = SeedMap.Api.FloorDiv(bounds.MinX, span); x <= SeedMap.Api.FloorDiv(bounds.MaxX - 1, span); x++)
                    if (!_tiles.ContainsKey(new(step, x, z))) return false;
            return true;
        }
    }

    public SeedMapViewport()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshChromeMaterial();
            UpdateViewportControlMargin();
            if (_profile is not null) Configure(_profile);
        };
        Unloaded += (_, _) => { if (!IsLoaded) Release(); };
        SizeChanged += (_, _) =>
        {
            StopZoomAnimation();
            Surface.Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };
            UpdateViewportControlMargin();
            MoveVisuals();
            if (!ShouldShowStructures()) HideStructures(redraw: true);
            QueueRefresh();
        };
        SearchBadge.SizeChanged += (_, _) => UpdateSearchMarker();
    }

    public void Configure(SeedMapProfile profile)
    {
        var version = SeedMap.Api.Versions.FirstOrDefault(v => v.Key == profile.Version)
            ?? throw new InvalidOperationException($"不支持 Java {profile.Version}。");
        var world = new SeedMap.World(profile.Seed, version.Id, profile.Dimension, profile.Y);
        bool reset = _engine is null || _engine.World != world;
        if (!ReferenceEquals(_profile, profile)) CancelFlight();
        _profile = profile;
        if (reset)
        {
            Release();
            _engine = new(world);
            StructuresChanged?.Invoke(Array.Empty<SeedMap.StructureHit>());
            _biomes.Clear(); _types.Clear();
            foreach (var biome in SeedMap.Api.Biomes(version.Id, profile.Dimension)) _biomes[biome.Id] = biome;
            foreach (var type in SeedMap.Api.Structures(version.Id, profile.Dimension)) _types[type.Id] = type;
            _measure.Clear();
        }
        if (_refresh is null)
        {
            _refresh = DispatcherQueue.CreateTimer();
            _refresh.Interval = TimeSpan.FromMilliseconds(90); _refresh.IsRepeating = false;
            _refresh.Tick += Refresh_Tick;
        }
        Recolor(); MoveVisuals(); QueueRefresh();
    }
    private void Refresh_Tick(DispatcherQueueTimer sender, object args) => RefreshVisible();

    public void Release()
    {
        CancelFlight();
        ClearMeasurement();
        _searchResult = null;
        _hoveredStructure = null;
        _selectedStructure = null;
        SearchMarker.Visibility = SearchBadge.Visibility = Visibility.Collapsed;
        _worldGeneration++;
        if (_refresh is not null) { _refresh.Stop(); _refresh.Tick -= Refresh_Tick; _refresh = null; }
        if (_zoomAnimation is not null)
        {
            _zoomAnimation.Tick -= ZoomAnimation_Tick;
            _zoomAnimation = null;
        }
        foreach (var cts in _pending.Values) cts.Cancel();
        _pending.Clear();
        _wanted.Clear(); _visible.Clear();
        _tileStep = 0;
        _structureRefresh?.Stop();
        if (_structureRefresh is not null)
        {
            _structureRefresh.Tick -= StructureRefresh_Tick;
            _structureRefresh = null;
        }
        CancelStructureQuery();
        _structuresCts = null;
        _engine?.Dispose(); _engine = null;
        foreach (var tile in _tiles.Values)
        {
            tile.Image.Source = null;
            if (_imagePool.Count < 128) _imagePool.Push(tile.Image);
        }
        _tiles.Clear(); Tiles.Children.Clear(); RecycleOverlays();
        _structures.Clear();
        MapProgress.Visibility = Visibility.Collapsed;
    }

    // In-app Acrylic samples the map. Mica is an opaque desktop backdrop, so it
    // cannot provide the translucent map overlays requested for this workbench.
    internal static Brush CreateChromeBrush() => WorkbenchTemplate.CreateChromeBrush();

    internal void RefreshChromeMaterial()
    {
        ZoomInButton.Background = CreateChromeBrush();
        ZoomOutButton.Background = CreateChromeBrush();
        OriginButton.Background = CreateChromeBrush();
    }

    private void UpdateViewportControlMargin()
    {
        double buttonWidth = Math.Clamp(ActualWidth - 24, 0, 40);
        double buttonHeight = Math.Clamp((ActualHeight - 32) / 3, 0, 32);
        ZoomInButton.Width = ZoomOutButton.Width = OriginButton.Width = buttonWidth;
        ZoomInButton.Height = ZoomOutButton.Height = OriginButton.Height = buttonHeight;
        ViewportControls.Margin = new Thickness(12);
    }

    public Point ScreenToWorld(Point point)
    {
        var world = Camera.ScreenToWorld(point.X, point.Y, ActualWidth, ActualHeight);
        return new(world.X, world.Z);
    }

    public Point WorldToScreen(double x, double z)
    {
        var screen = Camera.WorldToScreen(x, z, ActualWidth, ActualHeight);
        return new(screen.X, screen.Y);
    }

    public void Jump(double x, double z)
    {
        CancelFlight();
        MoveCamera(x, z);
    }

    private void MoveCamera(double x, double z, bool refresh = true)
    {
        if (_profile is null) return;
        MeasurementTip.IsOpen = false;
        _profile.CenterX = SeedMap.Api.ClampCoordinate(x); _profile.CenterZ = SeedMap.Api.ClampCoordinate(z);
        CancelStructureQuery();
        _structureRefresh?.Stop();
        MoveVisuals();
        if (!ShouldShowStructures()) HideStructures(redraw: true);
        if (refresh) QueueRefresh();
        CameraChanged?.Invoke();
    }

    public void CancelFlight()
    {
        _flight?.Cancel();
        _flight = null;
        StopZoomAnimation();
    }

    public Task ShowSearchResultAsync(int x, int z, string name, CancellationToken token) =>
        NavigateToAsync(x, z, name, token);

    public async Task NavigateToAsync(double x, double z, string? name = null, CancellationToken token = default)
    {
        if (_profile is null || token.IsCancellationRequested) return;
        CancelFlight();
        _hoveredStructure = null;
        _selectedStructure = null;
        x = SeedMap.Api.ClampCoordinate(x); z = SeedMap.Api.ClampCoordinate(z);
        _searchResult = name is null ? null : ((int)Math.Floor(x), (int)Math.Floor(z), name);
        UpdateSearchMarker();
        var point = WorldToScreen(x, z);
        bool inView = point.X >= 0 && point.Y >= 0 && point.X <= ActualWidth && point.Y <= ActualHeight;
        if (!new UISettings().AnimationsEnabled || ActualWidth < 1 || ActualHeight < 1 ||
            Math.Abs(point.X - ActualWidth / 2) + Math.Abs(point.Y - ActualHeight / 2) < .5)
        {
            MoveCamera(x, z);
            return;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _flight = cts;
        var p = _profile;
        int generation = _worldGeneration;
        double startX = p.CenterX, startZ = p.CenterZ, zoom = p.PixelsPerBlock;
        double fit = Math.Min(ActualWidth / (Math.Abs(x - startX) + 128),
            ActualHeight / (Math.Abs(z - startZ) + 128)) * .65;
        double wide = inView ? zoom : Math.Clamp(Math.Min(zoom, fit), 1.0 / 256, 8);
        double duration = inView ? 320 : 900;
        var clock = Stopwatch.StartNew();
        double lastRefresh = 0;
        static double Ease(double t) => t * t * (3 - 2 * t);
        try
        {
            while (clock.Elapsed.TotalMilliseconds < duration)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (generation != _worldGeneration || !ReferenceEquals(p, _profile)) return;
                double t = clock.Elapsed.TotalMilliseconds / duration;
                double move = inView ? Ease(t) : Ease(Math.Clamp((t - .2) / .6, 0, 1));
                p.PixelsPerBlock = inView ? zoom : t < .25 ? zoom + (wide - zoom) * Ease(t / .25)
                    : t > .75 ? wide + (zoom - wide) * Ease((t - .75) / .25) : wide;
                MoveCamera(startX + (x - startX) * move, startZ + (z - startZ) * move, refresh: false);
                // Keep tiles flowing at a bounded rate while the camera animates.
                if (clock.Elapsed.TotalMilliseconds - lastRefresh >= 150)
                {
                    RefreshVisible(); lastRefresh = clock.Elapsed.TotalMilliseconds;
                }
                await Task.Delay(16, cts.Token);
            }
            cts.Token.ThrowIfCancellationRequested();
            if (generation != _worldGeneration || !ReferenceEquals(p, _profile)) return;
            p.PixelsPerBlock = zoom; MoveCamera(x, z);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_flight, cts)) _flight = null; }
    }

    public void Zoom(double factor, Point anchor)
    {
        if (_profile is null) return;
        var camera = Camera;
        camera.ZoomAt(factor, anchor.X, anchor.Y, ActualWidth, ActualHeight);
        _profile.PixelsPerBlock = camera.PixelsPerBlock;
        MoveCamera(camera.CenterX, camera.CenterZ);
    }

    private void AnimateZoom(double factor, Point anchor)
    {
        if (_profile is null || !double.IsFinite(factor) || factor <= 0 || factor == 1) return;
        int direction = factor > 1 ? 1 : -1;
        // Accumulate matching wheel input. Reversing starts from the displayed
        // scale, discarding the unfinished target so the response is immediate.
        double start = _profile.PixelsPerBlock;
        double target = Math.Clamp((_zoomAnimation?.IsRunning == true && _zoomDirection == direction
            ? _zoomTarget : start) * factor, 1.0 / 256, 8);
        CancelFlight();
        if (target == start) { QueueRefresh(); return; }
        if (!new UISettings().AnimationsEnabled)
        {
            Zoom(target / start, anchor);
            return;
        }
        _zoomStart = start; _zoomTarget = target; _zoomDirection = direction;
        _zoomAnchor = anchor; _zoomWorld = ScreenToWorld(anchor);
        if (_zoomAnimation is null)
        {
            _zoomAnimation = DispatcherQueue.CreateTimer();
            _zoomAnimation.Interval = TimeSpan.FromMilliseconds(16);
            _zoomAnimation.Tick += ZoomAnimation_Tick;
        }
        _zoomClock.Restart();
        _zoomAnimation.Start();
    }

    private void ZoomAnimation_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!ReferenceEquals(sender, _zoomAnimation) || !sender.IsRunning) return;
        if (_profile is null || _engine is null)
        {
            StopZoomAnimation();
            return;
        }
        double elapsed = _zoomClock.Elapsed.TotalMilliseconds;
        double t = Math.Clamp(elapsed / 180, 0, 1);
        double ease = 1 - Math.Pow(1 - t, 3);
        double zoom = t >= 1 ? _zoomTarget :
            Math.Exp(Math.Log(_zoomStart) + Math.Log(_zoomTarget / _zoomStart) * ease);
        _profile.PixelsPerBlock = zoom;
        // Recompute from the same world anchor each frame to avoid accumulated
        // drift, even when a new wheel event moves the pointer or reverses.
        double x = _zoomWorld.X - (_zoomAnchor.X - ActualWidth / 2) / zoom;
        double z = _zoomWorld.Y - (_zoomAnchor.Y - ActualHeight / 2) / zoom;
        // Camera frames reuse existing visuals; the shared 90 ms refresh timer
        // bounds map generation throughout a continuous stream of wheel input.
        MoveCamera(x, z);
        if (t >= 1)
            StopZoomAnimation();
    }

    private void StopZoomAnimation()
    {
        _zoomAnimation?.Stop();
        _zoomClock.Reset();
    }

    public SeedMap.Bounds VisibleBounds()
    {
        return Camera.VisibleBounds(ActualWidth, ActualHeight);
    }

    private SeedMap.Camera Camera => new(
        _profile!.CenterX, _profile.CenterZ, _profile.PixelsPerBlock);

    private int CurrentStep() => Camera.SelectSamplingStep();
    private bool ShouldShowStructures() => _profile is not null && CurrentStep() < 64;

    private void QueueRefresh()
    {
        if (!IsLoaded || _engine is null) return;
        if (_refresh is { IsRunning: false }) _refresh.Start();
    }

    private void QueueStructureRefresh()
    {
        if (!IsLoaded || _engine is null || _profile is null) return;
        if (!ShouldShowStructures())
        {
            HideStructures(redraw: false);
            return;
        }
        if (_structureRefresh is null)
        {
            _structureRefresh = DispatcherQueue.CreateTimer();
            _structureRefresh.Interval = TimeSpan.FromMilliseconds(180);
            _structureRefresh.IsRepeating = false;
            _structureRefresh.Tick += StructureRefresh_Tick;
        }
        _structureRefresh.Stop();
        _structureRefresh.Start();
    }

    private void StructureRefresh_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!ReferenceEquals(sender, _structureRefresh)) return;
        if (!ShouldShowStructures())
        {
            HideStructures(redraw: true);
            return;
        }
        _ = LoadStructuresAsync(VisibleBounds());
    }

    private void CancelStructureQuery()
    {
        _structureRequestId++;
        _structuresCts?.Cancel();
    }

    private void HideStructures(bool redraw)
    {
        CancelStructureQuery();
        _structureRefresh?.Stop();
        bool notify = _structures.Count > 0;
        bool hadVisuals = _displayedStructures.Count > 0 ||
            _selectedStructure is not null || _hoveredStructure is not null;
        _structures.Clear();
        _displayedStructures.Clear();
        _selectedStructure = null;
        _hoveredStructure = null;
        if (notify) StructuresChanged?.Invoke(Array.Empty<SeedMap.StructureHit>());
        UpdateSearchMarker();
        if (redraw && hadVisuals) DrawOverlays();
    }

    private void MoveVisuals()
    {
        if (_profile is null) return;
        foreach (var (key, tile) in _tiles)
        {
            Point screen = WorldToScreen((double)key.X * SeedMap.Engine.TileSize * key.Step, (double)key.Z * SeedMap.Engine.TileSize * key.Step);
            double size = SeedMap.Engine.TileSize * key.Step * _profile.PixelsPerBlock;
            tile.Image.Width = size + 0.15; tile.Image.Height = size + 0.15;
            Canvas.SetLeft(tile.Image, screen.X); Canvas.SetTop(tile.Image, screen.Y);
        }
        double scale = _profile.PixelsPerBlock / _overlayScale;
        Overlays.RenderTransform = new CompositeTransform
        {
            ScaleX = scale,
            ScaleY = scale,
            TranslateX = ActualWidth / 2 - scale * _overlayWidth / 2 + (_overlayX - _profile.CenterX) * _profile.PixelsPerBlock,
            TranslateY = ActualHeight / 2 - scale * _overlayHeight / 2 + (_overlayZ - _profile.CenterZ) * _profile.PixelsPerBlock
        };
        UpdateCrosshair();
        UpdateSearchMarker();
    }

    private void UpdateCrosshair()
    {
        if (_profile is null) return;
        uint color = ThemeService.IsDark ? 0xff202020 : 0xffeeeeee;
        int bestStep = int.MaxValue;
        // A previous zoom level can still be the visible underlay while loading.
        foreach (var (key, tile) in _tiles)
        {
            int span = key.Step * SeedMap.Engine.TileSize;
            int px = (int)Math.Floor((_profile.CenterX - (double)key.X * span) / key.Step);
            int pz = (int)Math.Floor((_profile.CenterZ - (double)key.Z * span) / key.Step);
            if (px < 0 || pz < 0 || px >= SeedMap.Engine.TileSize || pz >= SeedMap.Engine.TileSize || key.Step >= bestStep) continue;
            color = PixelColor(tile.Ids[pz * SeedMap.Engine.TileSize + px]); bestStep = key.Step;
        }
        double luminance = .2126 * ((color >> 16) & 255) + .7152 * ((color >> 8) & 255) + .0722 * (color & 255);
        bool dark = luminance < 140;
        if (_crosshairOnDark == dark) return;
        _crosshairOnDark = dark;
        var foreground = new SolidColorBrush(dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        var outline = new SolidColorBrush(dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
        CrosshairX.Fill = CrosshairZ.Fill = foreground;
        CrosshairOutlineX.Fill = CrosshairOutlineZ.Fill = outline;
    }

    private void UpdateSearchMarker()
    {
        var target = _selectedStructure is { } selected
            ? (selected.X, selected.Z, Name: _types.GetValueOrDefault(selected.Type)?.Name ?? "结构")
            : _hoveredStructure is { } hovered
                ? (hovered.X, hovered.Z, Name: _types.GetValueOrDefault(hovered.Type)?.Name ?? "结构")
                : _searchResult;
        if (_profile is null || target is not { } result)
        {
            SearchMarker.Visibility = SearchBadge.Visibility = Visibility.Collapsed;
            return;
        }
        var at = WorldToScreen(result.X, result.Z);
        bool visible = at.X >= -28 && at.Y >= -28 && at.X <= ActualWidth + 28 && at.Y <= ActualHeight + 28;
        SearchMarker.Visibility = SearchBadge.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        SearchLabel.Text = $"{result.Name} · {result.X}, {result.Z}";
        SearchLabel.Foreground = Brush("PrimaryTextBrush");
        SearchLabel.FontFamily = FontService.BodyFont;
        Canvas.SetLeft(SearchMarker, at.X - 14); Canvas.SetTop(SearchMarker, at.Y - 14);
        Canvas.SetLeft(SearchBadge, Math.Clamp(at.X + 20, 4, Math.Max(4, ActualWidth - SearchBadge.ActualWidth - 4)));
        Canvas.SetTop(SearchBadge, Math.Clamp(at.Y + 16, 4, Math.Max(4, ActualHeight - SearchBadge.ActualHeight - 4)));
    }

    private SeedMap.StructureHit? HitTestStructure(Point at)
    {
        if (!ShouldShowStructures()) return null;
        SeedMap.StructureHit? nearest = null;
        double distance = 14 * 14;
        foreach (var hit in _displayedStructures)
        {
            var point = WorldToScreen(hit.X, hit.Z);
            double candidate = Math.Pow(point.X - at.X, 2) + Math.Pow(point.Y - at.Y, 2);
            if (candidate > distance) continue;
            nearest = hit; distance = candidate;
        }
        return nearest;
    }

    private void SelectStructure(SeedMap.StructureHit hit)
    {
        _hoveredStructure = null;
        _selectedStructure = hit;
        _searchResult = null;
        UpdateSearchMarker();
    }

    private void RefreshVisible()
    {
        if (_engine is null || _profile is null || ActualWidth < 1 || ActualHeight < 1) return;
        int step = CurrentStep(), span = SeedMap.Engine.TileSize * step;
        var bounds = VisibleBounds();
        var visible = new HashSet<SeedMap.Engine.TileKey>(SeedMap.TilePlanner.Visible(bounds, step));
        var wanted = new HashSet<SeedMap.Engine.TileKey>(SeedMap.TilePlanner.WithPrefetch(bounds, step));
        _wanted = wanted; _visible = visible; _tileStep = step;
        foreach (var (key, cts) in _pending.ToArray())
            if (!wanted.Contains(key)) cts.Cancel();
        // Keep the previous scale as a temporary underlay until the new level arrives.
        foreach (var key in _tiles.Keys.ToArray())
            if (!wanted.Contains(key) && (key.Step == step || _tiles.Count > 100))
                RemoveTile(key);
        PumpTiles();
        RemovePreviousScale();
        if (ShouldShowStructures())
            QueueStructureRefresh();
        else
            HideStructures(redraw: false);
        DrawOverlays();
        UpdateLoading();
    }

    private void PumpTiles()
    {
        // Wheel input changes CurrentStep before the debounced refresh installs
        // a new wanted set. Never feed an old plan during that interval.
        if (_pumping || _engine is null || _profile is null || !IsLoaded || _tileStep != CurrentStep()) return;
        _pumping = true;
        try
        {
            // A cache hit may finish synchronously. Select a bounded batch
            // once; completion cannot extend this batch or recurse into it.
            var next = SeedMap.TilePlanner.OrderForLoading(
                    _wanted.Where(k => !_tiles.ContainsKey(k) && !_pending.ContainsKey(k)),
                    _visible,
                    _profile.CenterX,
                    _profile.CenterZ,
                    Math.Max(0, 2 - _pending.Count))
                .ToArray();
            foreach (var key in next) _ = LoadTileAsync(key);
        }
        finally { _pumping = false; }
    }

    private void QueueTilePump()
    {
        if (_tilePumpQueued || _engine is null || _profile is null || !IsLoaded ||
            _tileStep != CurrentStep() || _pending.Count >= 2 ||
            !_wanted.Any(k => !_tiles.ContainsKey(k) && !_pending.ContainsKey(k))) return;
        _tilePumpQueued = true;
        // Cached results must not flood the normal-priority UI queue and keep
        // the refresh timer / pointer input from running.
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _tilePumpQueued = false;
                PumpTiles();
            }))
            _tilePumpQueued = false;
    }

    private void RemovePreviousScale()
    {
        if (_visible.Count == 0 || !_visible.All(_tiles.ContainsKey)) return;
        // Retain the scale belonging to the installed plan. CurrentStep can
        // already refer to a newer wheel event while this plan is finishing.
        foreach (var key in _tiles.Keys.Where(k => k.Step != _tileStep).ToArray()) RemoveTile(key);
    }

    private void RemoveTile(SeedMap.Engine.TileKey key)
    {
        var tile = _tiles[key]; tile.Image.Source = null; Tiles.Children.Remove(tile.Image); _tiles.Remove(key);
        if (_imagePool.Count < 128) _imagePool.Push(tile.Image);
    }

    private async Task LoadTileAsync(SeedMap.Engine.TileKey key)
    {
        var engine = _engine!;
        int generation = _worldGeneration;
        using var cts = new CancellationTokenSource();
        _pending[key] = cts;
        try
        {
            int[] ids = await engine.TileAsync(key, cts.Token);
            if (cts.IsCancellationRequested || generation != _worldGeneration || !IsLoaded ||
                key.Step != _tileStep || _tileStep != CurrentStep() || !_wanted.Contains(key)) return;
            var image = _imagePool.Count > 0 ? _imagePool.Pop() : new Image { Stretch = Stretch.Fill };
            image.Source = Bitmap(ids);
            _tiles[key] = new(ids, image);
            Tiles.Children.Add(image);
            MoveVisuals();
            RemovePreviousScale();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _worldGeneration) _wanted.Remove(key);
            StatusChanged?.Invoke(ex.Message); Log.Warn("地图瓦片：" + ex.Message);
        }
        finally
        {
            if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, cts)) _pending.Remove(key);
            if (generation == _worldGeneration) { QueueTilePump(); UpdateLoading(); }
        }
    }

    public uint PixelColor(int id)
    {
        var profile = _profile!;
        return SeedMap.Palette.ResolveBiomeColor(
            id,
            _biomes,
            profile.Biomes.Count == 0 ? null : profile.Biomes.ToHashSet(),
            profile.HideOtherBiomes,
            ThemeService.IsDark);
    }

    private WriteableBitmap Bitmap(int[] ids)
    {
        var profile = _profile!;
        var pixels = SeedMap.Palette.ToBgra(
            ids,
            _biomes,
            profile.Biomes.Count == 0 ? null : profile.Biomes.ToHashSet(),
            profile.HideOtherBiomes,
            ThemeService.IsDark);
        var bitmap = new WriteableBitmap(SeedMap.Engine.TileSize, SeedMap.Engine.TileSize);
        BitmapAllocations.Add(bitmap, new BitmapMemory());
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels);
        bitmap.Invalidate();
        return bitmap;
    }

    public void Recolor()
    {
        RefreshChromeMaterial();
        foreach (var tile in _tiles.Values) tile.Image.Source = Bitmap(tile.Ids);
        DrawOverlays(); UpdateCrosshair(); UpdateSearchMarker();
    }

    public void RefreshLayers()
    {
        if (!ShouldShowStructures()) HideStructures(redraw: false);
        DrawOverlays(); QueueRefresh();
    }

    private async Task LoadStructuresAsync(SeedMap.Bounds bounds)
    {
        var engine = _engine;
        var profile = _profile;
        if (engine is null || profile is null || !ShouldShowStructures()) return;
        CancelStructureQuery();
        long requestId = _structureRequestId;
        using var cts = new CancellationTokenSource();
        _structuresCts = cts;
        int generation = _worldGeneration;
        try
        {
            var types = _types.Values.Where(t => profile.Structures.Contains(t.Id)).ToArray();
            var result = await engine.QueryStructuresAsync(bounds, types, cts.Token);
            if (cts.IsCancellationRequested || requestId != _structureRequestId ||
                generation != _worldGeneration || !ReferenceEquals(profile, _profile) ||
                !ShouldShowStructures()) return;
            _structures = result.Hits;
            StructuresChanged?.Invoke(_structures);
            DrawOverlays();
            LoadStatusChanged?.Invoke(result.Limited ? "部分结构在此缩放下已暂停；放大后显示。" : $"已加载 · {_structures.Count} 个结构");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (requestId == _structureRequestId && generation == _worldGeneration &&
                ReferenceEquals(profile, _profile))
                StatusChanged?.Invoke(ex.Message);
        }
        finally { if (ReferenceEquals(_structuresCts, cts)) _structuresCts = null; }
    }

    private Brush Brush(string key) => ThemeService.Brush(key);
    private void RecycleOverlays()
    {
        _displayedStructures.Clear();
        foreach (var child in Overlays.Children)
        {
            switch (child)
            {
                case Rectangle rectangle when _slimePool.Count < 2048: _slimePool.Push(rectangle); break;
                case Line line when _linePool.Count < 1024: _linePool.Push(line); break;
                case Ellipse dot when _dotPool.Count < 1024: _dotPool.Push(dot); break;
                case Border badge when _labelPool.Count < 256:
                    ((TextBlock)badge.Child).Text = ""; _labelPool.Push(badge); break;
            }
        }
        Overlays.Children.Clear();
    }
    private void DrawOverlays()
    {
        RecycleOverlays(); Overlays.RenderTransform = null;
        if (_profile is null || _engine is null || ActualWidth < 1 || ActualHeight < 1) return;
        _overlayX = _profile.CenterX; _overlayZ = _profile.CenterZ;
        _overlayScale = _profile.PixelsPerBlock; _overlayWidth = ActualWidth; _overlayHeight = ActualHeight;
        var bounds = VisibleBounds();
        double chunkSize = 16 * _profile.PixelsPerBlock;
        if ((_profile.Grid || _profile.Slime && _profile.Dimension == 0) && chunkSize >= 8)
        {
            int x0 = Math.Max(-1875000, SeedMap.Api.FloorDiv(bounds.MinX, 16)), x1 = Math.Min(1874999, SeedMap.Api.FloorDiv(bounds.MaxX, 16));
            int z0 = Math.Max(-1875000, SeedMap.Api.FloorDiv(bounds.MinZ, 16)), z1 = Math.Min(1874999, SeedMap.Api.FloorDiv(bounds.MaxZ, 16));
            if (_profile.Slime && _profile.Dimension == 0 && (long)(x1 - x0 + 1) * (z1 - z0 + 1) <= 18000)
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                        if (SeedMap.Api.IsSlimeChunk(_profile.Seed, x, z))
                        {
                            var rect = _slimePool.Count > 0 ? _slimePool.Pop() : new Rectangle();
                            rect.Width = chunkSize; rect.Height = chunkSize;
                            rect.Fill = Brush("SystemFillColorSuccessBrush"); rect.Opacity = .48;
                            var chunk = SeedMap.Geometry.ChunkBounds(x, z);
                            var at = WorldToScreen(chunk.MinX, chunk.MinZ);
                            Canvas.SetLeft(rect, at.X); Canvas.SetTop(rect, at.Y); Overlays.Children.Add(rect);
                        }
            if (_profile.Grid)
            {
                for (int x = x0; x <= x1; x++) { double sx = WorldToScreen(x * 16, 0).X; Line(sx, 0, sx, ActualHeight, .35); }
                for (int z = z0; z <= z1; z++) { double sz = WorldToScreen(0, z * 16).Y; Line(0, sz, ActualWidth, sz, .35); }
            }
        }
        // Bound overview density with world-anchored buckets. Screen buckets
        // change their representative when the camera moves across a pixel
        // boundary, even if the visible world barely changes.
        if (ShouldShowStructures())
        {
            var cells = new HashSet<(int X, int Z)>();
            int cellSize = Math.Max(18, (int)Math.Ceiling(Math.Sqrt(ActualWidth * ActualHeight / 900)));
            int worldCellSize = Math.Max(1, (int)Math.Ceiling(
                cellSize / Math.Max(_profile.PixelsPerBlock, double.Epsilon)));
            foreach (var hit in _structures
                .Where(hit => _profile.Structures.Contains(hit.Type))
                .OrderBy(hit => hit.X)
                .ThenBy(hit => hit.Z)
                .ThenBy(hit => hit.Type))
            {
                var point = WorldToScreen(hit.X, hit.Z);
                if (point.X < -12 || point.Y < -12 || point.X > ActualWidth + 12 || point.Y > ActualHeight + 12) continue;
                if (!cells.Add((
                    SeedMap.Api.FloorDiv(hit.X, worldCellSize),
                    SeedMap.Api.FloorDiv(hit.Z, worldCellSize))))
                    continue;
                Dot(point, false);
                _displayedStructures.Add(hit);
            }
        }
        foreach (var marker in _profile.Waypoints.Where(w => w.Dimension == _profile.Dimension))
        {
            var point = WorldToScreen(marker.X, marker.Z);
            if (point.X < -12 || point.Y < -12 || point.X > ActualWidth + 12 || point.Y > ActualHeight + 12) continue;
            Dot(point, true);
            var badge = _labelPool.Count > 0 ? _labelPool.Pop() : new Border
            {
                Child = new TextBlock { MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis },
                Padding = new Thickness(4, 1, 4, 1),
                CornerRadius = new CornerRadius(3)
            };
            badge.Background = Brush("WindowBackgroundBrush");
            var label = (TextBlock)badge.Child;
            label.Text = marker.Name; label.Foreground = Brush("PrimaryTextBrush");
            label.FontFamily = FontService.BodyFont;
            Canvas.SetLeft(badge, point.X + 10); Canvas.SetTop(badge, point.Y - 10); Overlays.Children.Add(badge);
        }
        for (int i = 1; i < _measure.Count; i++)
        {
            var a = WorldToScreen(_measure[i - 1].X, _measure[i - 1].Y); var b = WorldToScreen(_measure[i].X, _measure[i].Y);
            Line(a.X, a.Y, b.X, b.Y, 1);
        }
        foreach (var point in _measure) Dot(WorldToScreen(point.X, point.Y), false);
    }

    public void SetMeasuring(bool enabled)
    {
        Measuring = enabled;
        if (!enabled) ClearMeasurement();
    }
    public void ClearMeasurement()
    {
        _measure.Clear(); MeasurementTip.IsOpen = false;
        DrawOverlays();
    }
    public void AddMeasurementPoint(double x, double z)
    {
        if (_profile is null || !Measuring) return;
        CancelFlight();
        _measure.Add(new Point(Math.Floor(x), Math.Floor(z))); DrawOverlays();
        if (_measure.Count < 2)
        {
            StatusChanged?.Invoke("已设置测距起点；点击地图或选择另一路径点。");
            return;
        }
        var end = _measure[^1]; var start = _measure[^2];
        static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        double segment = Distance(start, end), total = 0;
        for (int i = 1; i < _measure.Count; i++) total += Distance(_measure[i - 1], _measure[i]);
        string summary = _measure.Count == 2 ? $"距离 {segment:N1} 格"
            : $"本段 {segment:N1} 格 · 累计 {total:N1} 格 · {_measure.Count} 点";
        MeasurementTip.Content = $"{summary}\nΔX {end.X - start.X:N0} · ΔZ {end.Y - start.Y:N0}\n" +
            $"({start.X:N0}, {start.Y:N0}) → ({end.X:N0}, {end.Y:N0})";
        MeasurementTip.RequestedTheme = ThemeService.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        MeasurementTip.FontFamily = FontService.BodyFont;
        var at = WorldToScreen(end.X, end.Y);
        Canvas.SetLeft(MeasurementAnchor, Math.Clamp(at.X, 8, Math.Max(8, ActualWidth - 8)));
        Canvas.SetTop(MeasurementAnchor, Math.Clamp(at.Y, 8, Math.Max(8, ActualHeight - 8)));
        MeasurementTip.PlacementTarget = MeasurementAnchor;
        MeasurementTip.IsOpen = true;
        StatusChanged?.Invoke(summary);
    }

    private void Dot(Point point, bool waypoint)
    {
        var dot = _dotPool.Count > 0 ? _dotPool.Pop() : new Ellipse();
        dot.Width = dot.Height = waypoint ? 12 : 9;
        dot.Fill = Brush(waypoint ? "SystemFillColorCautionBrush" : "AccentFillColorDefaultBrush");
        dot.Stroke = Brush("PrimaryTextBrush"); dot.StrokeThickness = 1.5;
        Canvas.SetLeft(dot, point.X - dot.Width / 2); Canvas.SetTop(dot, point.Y - dot.Height / 2); Overlays.Children.Add(dot);
    }

    private void Line(double x1, double y1, double x2, double y2, double opacity)
    {
        var line = _linePool.Count > 0 ? _linePool.Pop() : new Line();
        line.X1 = x1; line.Y1 = y1; line.X2 = x2; line.Y2 = y2;
        line.Stroke = Brush(opacity == 1 ? "AccentTextFillColorPrimaryBrush" : "PrimaryTextBrush");
        line.StrokeThickness = opacity == 1 ? 2 : 1; line.Opacity = opacity;
        Overlays.Children.Add(line);
    }

    private void UpdateLoading()
    {
        MapProgress.Visibility = MissingVisibleTileCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Surface);
        if (!point.Properties.IsLeftButtonPressed || _profile is null) return;
        CancelFlight();
        _hoveredStructure = null;
        UpdateSearchMarker();
        Surface.Focus(FocusState.Pointer);
        _press = _last = point.Position; _moved = false;
        _dragging = Surface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_profile is null) return;
        var at = e.GetCurrentPoint(Surface).Position;
        if (_dragging)
        {
            if (Math.Abs(at.X - _press.X) + Math.Abs(at.Y - _press.Y) > 4) _moved = true;
            Jump(_profile.CenterX - (at.X - _last.X) / _profile.PixelsPerBlock,
                 _profile.CenterZ - (at.Y - _last.Y) / _profile.PixelsPerBlock);
            _last = at;
            e.Handled = true;
        }
        var hovered = _dragging || Measuring || _selectedStructure is not null ? null : HitTestStructure(at);
        if (_hoveredStructure != hovered)
        {
            _hoveredStructure = hovered;
            UpdateSearchMarker();
        }
        Point world = ScreenToWorld(at);
        int x = (int)Math.Floor(world.X), z = (int)Math.Floor(world.Y);
        string biome = "加载中";
        int step = CurrentStep(), span = SeedMap.Engine.TileSize * step;
        var key = new SeedMap.Engine.TileKey(step, SeedMap.Api.FloorDiv(x, span), SeedMap.Api.FloorDiv(z, span));
        if (_tiles.TryGetValue(key, out var tile))
        {
            int px = SeedMap.Api.FloorDiv(x, step) - key.X * SeedMap.Engine.TileSize;
            int pz = SeedMap.Api.FloorDiv(z, step) - key.Z * SeedMap.Engine.TileSize;
            int id = tile.Ids[pz * SeedMap.Engine.TileSize + px];
            biome = _biomes.TryGetValue(id, out var value) ? value.Name : $"ID {id}";
        }
        HoverChanged?.Invoke($"X {x:N0} · Z {z:N0} · {biome}");
    }

    private void Surface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoveredStructure = null;
        UpdateSearchMarker();
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false; Surface.ReleasePointerCapture(e.Pointer);
        var at = e.GetCurrentPoint(Surface).Position;
        if (!_moved) ClickAt(at);
        QueueRefresh(); e.Handled = true;
    }
    private void Surface_PointerCanceled(object sender, PointerRoutedEventArgs e) { _dragging = false; QueueRefresh(); }

    private void ClickAt(Point at)
    {
        if (_profile is null) return;
        var world = ScreenToWorld(at);
        _context = world;
        if (Measuring)
        {
            AddMeasurementPoint(world.X, world.Y); return;
        }
        if (HitTestStructure(at) is { } hit)
        {
            SelectStructure(hit);
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = _types.GetValueOrDefault(hit.Type)?.Name ?? "结构", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = $"X {hit.X} · Z {hit.Z}" });
            var flyout = new Flyout { Content = content };
            var navigate = new Button { Content = "导航到此处" };
            navigate.Click += async (_, _) =>
            {
                flyout.Hide();
                await NavigateToAsync(hit.X, hit.Z, _types.GetValueOrDefault(hit.Type)?.Name ?? "结构");
            };
            content.Children.Add(navigate);
            var copy = new Button { Content = "复制坐标" }; copy.Click += (_, _) => Copy(hit.X, hit.Z); content.Children.Add(copy);
            var add = new Button { Content = "添加路径点" }; add.Click += (_, _) => WaypointRequested?.Invoke(hit.X, hit.Z); content.Children.Add(add);
            flyout.ShowAt(Surface, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = at });
            return;
        }
        _selectedStructure = null;
        _searchResult = null;
        _hoveredStructure = null;
        UpdateSearchMarker();
        StatusChanged?.Invoke($"X {(int)Math.Floor(world.X)} · Z {(int)Math.Floor(world.Y)}");
    }

    private void Surface_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_profile is null) return;
        var at = e.GetPosition(Surface); _context = ScreenToWorld(at);
        var waypoint = _profile.Waypoints.Where(w => w.Dimension == _profile.Dimension)
            .OrderBy(w => Math.Pow(WorldToScreen(w.X, w.Z).X - at.X, 2) + Math.Pow(WorldToScreen(w.X, w.Z).Y - at.Y, 2))
            .FirstOrDefault();
        if (waypoint is not null)
        {
            var position = WorldToScreen(waypoint.X, waypoint.Z);
            if (Math.Abs(position.X - at.X) <= 16 && Math.Abs(position.Y - at.Y) <= 16)
            {
                WaypointContextRequested?.Invoke(waypoint, at); e.Handled = true; return;
            }
        }
        var structure = HitTestStructure(at);
        if (structure is not null) SelectStructure(structure);
        int x = (int)Math.Floor(_context.X), z = (int)Math.Floor(_context.Y);
        if (structure is not null) { x = structure.X; z = structure.Z; }
        var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = $"复制 {x}, {z}" }; copy.Click += (_, _) => Copy(x, z); menu.Items.Add(copy);
        var add = new MenuFlyoutItem { Text = "添加路径点" }; add.Click += (_, _) => WaypointRequested?.Invoke(x, z); menu.Items.Add(add);
        var center = new MenuFlyoutItem
        {
            Text = structure is null ? "以此为中心" : "导航到此处",
            Icon = new SymbolIcon(Symbol.MapPin)
        };
        center.Click += async (_, _) => await NavigateToAsync(x, z,
            structure is null ? null : _types.GetValueOrDefault(structure.Type)?.Name ?? "结构");
        menu.Items.Add(center);
        var clear = new MenuFlyoutItem { Text = "清除测距" }; clear.Click += (_, _) => ClearMeasurement(); menu.Items.Add(clear);
        menu.ShowAt(Surface, at); e.Handled = true;
    }
    public static void Copy(int x, int z)
    {
        var data = new DataPackage(); data.SetText($"{x}, {z}"); Clipboard.SetContent(data);
    }
    private void Surface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var at = e.GetCurrentPoint(Surface);
        AnimateZoom(Math.Pow(1.25, at.Properties.MouseWheelDelta / 120.0), at.Position); e.Handled = true;
    }
    private void Surface_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_profile is null) return;
        double amount = 120 / _profile.PixelsPerBlock;
        switch (e.Key)
        {
            case VirtualKey.Left: Jump(_profile.CenterX - amount, _profile.CenterZ); break;
            case VirtualKey.Right: Jump(_profile.CenterX + amount, _profile.CenterZ); break;
            case VirtualKey.Up: Jump(_profile.CenterX, _profile.CenterZ - amount); break;
            case VirtualKey.Down: Jump(_profile.CenterX, _profile.CenterZ + amount); break;
            case VirtualKey.Add: case (VirtualKey)187: AnimateZoom(1.25, new(ActualWidth / 2, ActualHeight / 2)); break;
            case VirtualKey.Subtract: case (VirtualKey)189: AnimateZoom(.8, new(ActualWidth / 2, ActualHeight / 2)); break;
            case VirtualKey.Escape: ClearMeasurement(); break;
            default: return;
        }
        e.Handled = true;
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => AnimateZoom(1.25, new(ActualWidth / 2, ActualHeight / 2));
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => AnimateZoom(.8, new(ActualWidth / 2, ActualHeight / 2));
    private async void Origin_Click(object sender, RoutedEventArgs e) => await NavigateToAsync(0, 0, "世界原点");
}
