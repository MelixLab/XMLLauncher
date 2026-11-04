using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using LinxLauncher.Controls;
using LinxLauncher.Models;
using LinxLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SeedMap = RZeroF1.Minecraft.SeedMap.SeedMap;
using SeedMapEngine = RZeroF1.Minecraft.SeedMap.SeedMapEngine;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace LinxLauncher.Services;

/// <summary>Opt-in real WinUI/AOT acceptance runner, isolated from user data.</summary>
internal static class SeedMapDiagnostics
{
    public static string? TestRoot { get; } = Environment.GetEnvironmentVariable("XML_SEEDMAP_TEST_ROOT") is { Length: > 0 } path
        ? Path.GetFullPath(path) : null;
    private static void Write(string text)
    {
        File.AppendAllText(Path.Combine(TestRoot!, "ui-run.log"), $"{DateTime.Now:O} {text}{Environment.NewLine}");
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Write("PASS " + message);
    }
    private static async Task WaitMapAsync(SeedMapWorkbench map)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(100);
            if (map.Map.Engine is not null && map.Map.VisibleTileCount > 0 && map.Map.PendingTileCount == 0) return;
        }
        throw new TimeoutException("Map did not finish loading.");
    }
    private static async Task CaptureAsync(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        var file = await StorageFile.GetFileFromPathAsync(Path.Combine(TestRoot!, name));
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        stream.Size = 0;
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }
    private static async Task VerifyWorkbenchAsync(SeedMapWorkbench map)
    {
        T Control<T>(string name) where T : class => map.FindName(name) as T
            ?? throw new InvalidOperationException("Missing control " + name);
        Assert(map.Map.PrefetchedTileCount > 0 && map.Map.PendingTileCount <= 2,
            "offscreen ring is prefetched with at most two tile loads");
        var camera = SeedMapSession.Current.Profile;
        double initialX = camera.CenterX, initialZ = camera.CenterZ;
        map.Map.Jump(initialX + 48 / camera.PixelsPerBlock, initialZ);
        Assert(map.Map.HasVisibleCoverage, "short pan has immediate tile coverage from preload ring");
        map.Map.Jump(initialX, initialZ);
        var jump = Control<Flyout>("JumpFlyout");
        jump.ShowAt(Control<AppBarButton>("JumpButton"));
        await Task.Delay(150);
        Control<TextBox>("JumpX").Text = "-12345";
        Control<TextBox>("JumpZ").Text = "6789";
        map.Map.Jump(initialX + 1, initialZ + 1);
        await Task.Delay(150);
        Assert(Control<TextBox>("JumpX").Text == "-12345" && Control<TextBox>("JumpZ").Text == "6789",
            "camera and layout updates preserve live jump coordinate input");
        jump.Hide(); map.Map.Jump(initialX, initialZ);
        map.Map.SetMeasuring(true);
        map.Map.AddMeasurementPoint(0, 0); map.Map.AddMeasurementPoint(3, 4);
        Assert(map.Map.MeasurementPointCount == 2 && ((ToolTip)map.Map.FindName("MeasurementTip")).IsOpen,
            "measurement shows native tooltip beside endpoint");
        map.Map.SetMeasuring(false);
        Assert(map.Map.MeasurementPointCount == 0 && !((ToolTip)map.Map.FindName("MeasurementTip")).IsOpen,
            "disabling measurement clears points lines and tooltip");
        var pane = Control<SplitView>("Workspace");
        Assert(Grid.GetRow(Control<TextBlock>("HoverText")) == 0 &&
            Grid.GetRow(Control<TextBlock>("CameraText")) == 1 &&
            Grid.GetRow(Control<TextBlock>("StatusText")) == 2 &&
            ReferenceEquals(VisualTreeHelper.GetParent(Control<Grid>("StatusBar")), VisualTreeHelper.GetParent(map.Map)),
            "pointer status and camera share the info panel inside the map");
        var button = Control<AppBarToggleButton>("PanelButton");
        pane.IsPaneOpen = false;
        Assert(button.IsChecked == false, "pane close synchronizes toggle state");
        pane.IsPaneOpen = true;
        Assert(button.IsChecked == true, "pane reopen synchronizes toggle state");
        await Task.Delay(250);
        var bar = (ScrollBar)Control<PersistentScrollBar>("BiomeScrollBar").Content;
        Assert(bar.Maximum > 0 && bar.IndicatorMode == ScrollingIndicatorMode.MouseIndicator,
            "biome list keeps a native scrollbar visible independently of system auto-hide");
        var root = Control<Grid>("BiomePanel");
        var footer = (FrameworkElement)root.Children.Last();
        Assert(footer.TransformToVisual(root).TransformPoint(new()).Y + footer.ActualHeight <= root.ActualHeight + 1,
            "biome search footer stays within pane");
        var tools = Control<SelectorBar>("Tools");
        tools.SelectedItem = Control<SelectorBarItem>("StructuresTab");
        await Task.Delay(250);
        Assert(Control<Grid>("StructurePanel").Visibility == Visibility.Visible &&
            root.Visibility == Visibility.Collapsed, "native selector changes pane content");
        bar = (ScrollBar)Control<PersistentScrollBar>("StructureScrollBar").Content;
        Assert(bar.IndicatorMode == ScrollingIndicatorMode.MouseIndicator, "structure scrollbar stays visible");
        var results = Control<ListView>("StructureResults");
        Assert(Control<Expander>("ResultsExpander").ExpandDirection == ExpandDirection.Up,
            "structure result expander opens upward");
        Control<Expander>("ResultsExpander").IsExpanded = true;
        var queryClock = Stopwatch.StartNew();
        while (results.Items.Count == 0 && queryClock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
        Write($"STRUCTURE_UI count={results.Items.Count} menus={results.Items.Cast<ListViewItem>().Count(r => r.ContextFlyout is MenuFlyout)}");
        Assert(results.Items.Count > 0 && results.Items.Cast<ListViewItem>().All(r => r.ContextFlyout is MenuFlyout),
            "visible structures populate automatically with coordinate menus");
        await CaptureAsync(map, Path.GetFileName(CreateEmpty("structures.png")));
        var original = SeedMapSession.Current.Profile;
        original.Structures.Clear();
        map.Map.RefreshLayers();
        await Task.Delay(400);
        Assert(results.Items.Count == 0, "structure selection automatically refreshes results");
        original.Structures.AddRange(new[] { 5, 8, 9, 13, 100 });
        map.Map.RefreshLayers();
        tools.SelectedItem = Control<SelectorBarItem>("BiomesTab");
        await map.Map.ShowSearchResultAsync(2400, -1800, "定位样本", CancellationToken.None);
        Assert(map.Map.SearchResult is { X: 2400, Z: -1800 } &&
            original.CenterX == 2400 && original.CenterZ == -1800, "offscreen search marker animates camera to result");
        await WaitMapAsync(map);
        await CaptureAsync(map, Path.GetFileName(CreateEmpty("search-result.png")));
        var flight = map.Map.ShowSearchResultAsync(-12000, 8000, "取消样本", CancellationToken.None);
        await Task.Delay(100);
        map.Map.Jump(-300, 400);
        try { await flight; } catch (OperationCanceledException) { }
        Assert(!map.Map.IsFlying && original.CenterX == -300 && original.CenterZ == 400,
            "manual camera interaction cancels search flight");
        flight = map.Map.ShowSearchResultAsync(12000, -8000, "世界切换样本", CancellationToken.None);
        await Task.Delay(100);
        SeedMapSession.Current.Select("-1", "1.20.6", -1, 64, null);
        try { await flight; } catch (OperationCanceledException) { }
        Assert(!map.Map.IsFlying && map.Map.SearchResult is null, "world switch clears search result and flight");
        SeedMapSession.Current.Select("123", "1.20.6", 0, 64, null);
        map.Map.Jump(0, 0);
        await WaitMapAsync(map);
    }
    public static async Task RunAsync(MainWindow window)
    {
        Directory.CreateDirectory(TestRoot!);
        try
        {
            Write($"START dynamicCode={RuntimeFeature.IsDynamicCodeSupported} executable={Environment.ProcessPath}");
            await Task.Delay(500);
            window.NavigateTo("seedmap");
            await Task.Delay(300);
            var map = window.CurrentSeedMap ?? throw new InvalidOperationException("Seed map page missing.");
            await WaitMapAsync(map);
            Write($"INITIAL tiles={map.Map.VisibleTileCount} cache={map.Map.Engine!.CacheBytes} private={Process.GetCurrentProcess().PrivateMemorySize64}");
            File.WriteAllBytes(Path.Combine(TestRoot!, "initial.png"), Array.Empty<byte>());
            await CaptureAsync(map, "initial.png");
            if (Environment.GetEnvironmentVariable("XML_SEEDMAP_AUTOTEST") != "1") { Write("INTERACTIVE_READY"); return; }
            await VerifyWorkbenchAsync(map);
            var p = SeedMapSession.Current.Profile;
            var at = new Windows.Foundation.Point(map.Map.ActualWidth * .3, map.Map.ActualHeight * .4);
            var before = map.Map.ScreenToWorld(at); map.Map.Zoom(2, at); var after = map.Map.ScreenToWorld(at);
            Assert(Math.Abs(before.X - after.X) < 1e-7 && Math.Abs(before.Y - after.Y) < 1e-7, "mouse anchor zoom");
            map.Map.Jump(-12345, 6789);
            await WaitMapAsync(map);
            Assert(p.CenterX == -12345 && p.CenterZ == 6789, "negative coordinate jump");
            for (int i = 0; i < 12; i++)
            {
                SeedMapSession.Current.Select((i % 2 == 0 ? "-1" : "hello"), "1.20.6", i % 3 - 1, 64, null);
                await Task.Delay(35);
            }
            await WaitMapAsync(map);
            Assert(map.Map.Engine!.World.Seed == SeedMapSession.Current.Profile.Seed &&
                map.Map.Engine.World.Dimension == SeedMapSession.Current.Profile.Dimension, "rapid seed/dimension changes discard old results");
            p = SeedMapSession.Current.Profile;
            p.Biomes.Add(SeedMap.Biomes(map.Map.Engine.World.Version, p.Dimension)[0].Id);
            p.HideOtherBiomes = true;
            p.Waypoints.Add(new() { Name = "AOT 样本", X = -123, Z = 456, Dimension = p.Dimension });
            SeedMapSession.Current.Notify(null);
            SeedMapSession.Current.Save();
            var restored = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(Paths.AppData, "seed-map.json")), AppJsonContext.Default.SeedMapWorkspace)!;
            Assert(restored.Profiles.First(v => v.Id == p.Id).Waypoints.Any(w => w.Name == "AOT 样本"), "AOT JSON waypoint round trip");
            var palette = Enumerable.Range(0, 256).Select(map.Map.PixelColor).ToArray();
            var export = await SeedMapExport.RenderAsync(map.Map.Engine.World, new(-256, -256, 256, 256), 4, palette, p, CancellationToken.None);
            var png = await StorageFile.GetFileFromPathAsync(CreateEmpty("export.png"));
            using (var stream = await png.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)export.Width, (uint)export.Height, 96, 96, export.Pixels);
                await encoder.FlushAsync();
            }
            Assert(new FileInfo(png.Path).Length > 100, "PNG export encoded");
            AppState.Settings.ThemeMode = "Dark"; ThemeService.Apply();
            await Task.Delay(250); await CaptureAsync(map, Path.GetFileName(CreateEmpty("dark.png")));
            AppState.Settings.ThemeMode = "Light"; AppState.Settings.UseNotoSans = false;
            FontService.ApplyGlobal(false); ThemeService.Apply();
            await Task.Delay(250); await CaptureAsync(map, Path.GetFileName(CreateEmpty("light.png")));
            window.AppWindow.ResizeClient(new(720, 620)); await Task.Delay(350);
            var narrowPane = (SplitView)map.FindName("Workspace");
            narrowPane.IsPaneOpen = true;
            await Task.Delay(250);
            ((TextBlock)map.FindName("StatusText")).Text = "1024 格范围、32 格采样内未找到；较小群系可能漏过。";
            await CaptureAsync(map, Path.GetFileName(CreateEmpty("narrow.png")));
            window.AppWindow.ResizeClient(new(1280, 820)); await Task.Delay(250);
            window.NavigateTo("tools"); await Task.Delay(150);
            Assert(map.Map.Engine is null, "leaving page releases engine");
            SeedMapWindow.Open(new SeedMapWorkbench());
            await Task.Delay(600);
            var auxiliary = SeedMapWindow.Current!;
            auxiliary.SetAlwaysOnTop(true);
            Assert(auxiliary.IsAlwaysOnTop, "pin sets native WS_EX_TOPMOST");
            auxiliary.SetAlwaysOnTop(false);
            Assert(!auxiliary.IsAlwaysOnTop, "unpin clears native WS_EX_TOPMOST");
            Assert(auxiliary.ExtendsContentIntoTitleBar &&
                auxiliary.AppWindow.TitleBar.ButtonBackgroundColor?.A == 0 &&
                auxiliary.AppWindow.TitleBar.ButtonInactiveBackgroundColor?.A == 0,
                "extended auxiliary title bar has transparent caption backgrounds");
            await CaptureAsync((FrameworkElement)auxiliary.Content, Path.GetFileName(CreateEmpty("auxiliary-light.png")));
            auxiliary.AppWindow.ResizeClient(new(480, 560));
            await Task.Delay(350);
            var auxiliaryMap = ((Grid)auxiliary.Content).Children.OfType<SeedMapWorkbench>().Single();
            var windowRoot = (FrameworkElement)auxiliary.Content;
            Assert(Math.Abs(auxiliaryMap.TransformToVisual(windowRoot).TransformPoint(new()).Y) < 1 &&
                Math.Abs(auxiliaryMap.ActualHeight - windowRoot.ActualHeight) < 2,
                "map extends behind title bar to top edge of client area");
            Assert(((Border)auxiliaryMap.FindName("CommandSurface")).Background is Microsoft.UI.Xaml.Media.AcrylicBrush &&
                ((Grid)auxiliaryMap.FindName("StatusBar")).Background is Microsoft.UI.Xaml.Media.AcrylicBrush,
                "floating toolbar and info panel use in-app acrylic");
            Assert(((Flyout)auxiliaryMap.FindName("WorldFlyout")).Content is Grid { Name: "WorldBar" } &&
                Math.Abs(auxiliaryMap.Map.ActualWidth - auxiliaryMap.ActualWidth) < 2,
                "auxiliary map fills content area with world settings in flyout");
            ((SplitView)auxiliaryMap.FindName("Workspace")).IsPaneOpen = true;
            await Task.Delay(250);
            var compactPanel = (Grid)auxiliaryMap.FindName("BiomePanel");
            var compactFooter = (FrameworkElement)compactPanel.Children.Last();
            Assert(compactFooter.TransformToVisual(compactPanel).TransformPoint(new()).Y + compactFooter.ActualHeight <= compactPanel.ActualHeight + 1,
                "480 x 560 auxiliary search footer stays within pane");
            await CaptureAsync((FrameworkElement)auxiliary.Content, Path.GetFileName(CreateEmpty("auxiliary-compact.png")));
            auxiliary.AppWindow.ResizeClient(new(1100, 720));
            auxiliary.AppWindow.Presenter.AsOverlapped()?.Maximize();
            await Task.Delay(250);
            auxiliary.AppWindow.Presenter.AsOverlapped()?.Restore();
            auxiliary.AppWindow.Presenter.AsOverlapped()?.Minimize();
            auxiliary.Activate();
            auxiliary.ReturnToLauncher();
            var closeClock = Stopwatch.StartNew();
            while (SeedMapWindow.Current is not null && closeClock.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(50);
            Assert(SeedMapWindow.Current is null, "auxiliary close releases singleton");
            window.NavigateTo("seedmap"); await Task.Delay(250);
            Assert(window.CurrentSeedMap?.Map.Profile?.Waypoints.Any(w => w.Name == "AOT 样本") == true, "page restores auxiliary session");
            SeedMapSession.Current.Select("123", "1.20.6", 0, 64, null);
            await WaitMapAsync(window.CurrentSeedMap!);
            var memory = new List<(long Private, int Handles)>();
            var workbenches = new HashSet<int>();
            for (int i = 0; i < 100; i++)
            {
                window.NavigateTo("tools"); await Task.Delay(350);
                window.NavigateTo("seedmap"); await Task.Delay(100);
                var current = window.CurrentSeedMap!;
                workbenches.Add(RuntimeHelpers.GetHashCode(current));
                await WaitMapAsync(current);
                current.Map.Jump(i * 777, -i * 333);
                await WaitMapAsync(current);
                if (i % 5 == 4)
                {
                    using var proc = Process.GetCurrentProcess();
                    Write($"MEMORY cycle={i + 1} private={proc.PrivateMemorySize64} working={proc.WorkingSet64} managed={GC.GetTotalMemory(false)} handles={proc.HandleCount} tiles={current.Map.VisibleTileCount} cache={current.Map.Engine!.CacheBytes}");
                    memory.Add((proc.PrivateMemorySize64, proc.HandleCount));
                }
            }
            Assert(workbenches.Count == 1, "bounded page reuses one light workbench");
            long warmPeak = memory.Take(10).Max(m => m.Private), laterPeak = memory.Skip(10).Max(m => m.Private);
            int warmHandles = memory.Take(10).Max(m => m.Handles), laterHandles = memory.Skip(10).Max(m => m.Handles);
            Write($"MEMORY_PEAK first50={warmPeak} last50={laterPeak} handlesFirst={warmHandles} handlesLast={laterHandles}");
            Assert(laterPeak - warmPeak < 32L * 1024 * 1024 && laterHandles - warmHandles < 50,
                "second 50 navigation cycles stay within 32 MiB / 50 handles of first-half peaks without forced GC");
            var exploring = window.CurrentSeedMap!;
            var profile = SeedMapSession.Current.Profile;
            profile.Grid = true; profile.Slime = true;
            exploring.Map.Zoom(2, new(exploring.Map.ActualWidth / 2, exploring.Map.ActualHeight / 2));
            var latencies = new List<double>();
            for (int i = 0; i < 100; i++)
            {
                var clock = Stopwatch.StartNew();
                exploring.Map.Jump(i * 1907 - 95000, i * 733 - 36000);
                await WaitMapAsync(exploring);
                latencies.Add(clock.Elapsed.TotalMilliseconds);
                if (exploring.Map.Engine!.CacheBytes > SeedMapEngine.CacheBudget)
                    throw new InvalidOperationException("Exploration exceeded the tile cache budget.");
                if (i % 20 == 19)
                {
                    using var proc = Process.GetCurrentProcess();
                    Write($"EXPLORE camera={i + 1} private={proc.PrivateMemorySize64} handles={proc.HandleCount} cache={exploring.Map.Engine.CacheBytes}");
                }
            }
            latencies.Sort();
            Assert(true, "100 camera moves respect the cache budget");
            Write($"LOAD_LATENCY median={latencies[50]:F0}ms p95={latencies[95]:F0}ms (includes 100ms polling, not frame timing)");
            window.NavigateTo("tools"); await Task.Delay(400);
            Assert(exploring.Map.Engine is null && exploring.Map.VisibleTileCount == 0, "exploration resources released on exit");
            Write("SUCCESS");
            window.Close();
        }
        catch (Exception ex) { Write("FAIL " + ex); Log.Error("SeedMapAcceptance", ex); }
    }
    private static string CreateEmpty(string name) { string path = Path.Combine(TestRoot!, name); File.WriteAllBytes(path, Array.Empty<byte>()); return path; }
    private static Microsoft.UI.Windowing.OverlappedPresenter? AsOverlapped(this Microsoft.UI.Windowing.AppWindowPresenter presenter)
        => presenter as Microsoft.UI.Windowing.OverlappedPresenter;
}
