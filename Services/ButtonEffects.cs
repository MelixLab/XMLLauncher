using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace LinxLauncher.Services;

/// <summary>卡片操作与主操作按钮共用的主题色反馈、单次扫光；保留 WinUI 原生 Button 模板。</summary>
public static class ButtonEffects
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ButtonEffects), new PropertyMetadata(false, Changed));
    private static readonly ConditionalWeakTable<Button, Effects> States = new();

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Button button) return;
        if ((bool)args.NewValue) States.GetValue(button, key => new Effects(key));
        else if (States.TryGetValue(button, out var effects))
        {
            effects.Detach();
            States.Remove(button);
        }
    }

    private sealed class Effects
    {
        private const int SheenStartDelayMs = 70;
        private const int SheenDurationMs = 850;
        private const float SheenAngleDegrees = -15;
        private const float SheenEdgePadding = 2;
        private const byte SheenPeakAlpha = 120;

        private static readonly Dictionary<string, string> ResourceKeys = new()
        {
            ["ButtonBackgroundPointerOver"] = "BrandBrush",
            ["ButtonBackgroundPressed"] = "BrandPressedBrush",
            ["ButtonBorderBrushPointerOver"] = "BrandBrush",
            ["ButtonBorderBrushPressed"] = "BrandPressedBrush",
            ["ButtonForegroundPointerOver"] = "OnBrandTextBrush",
            ["ButtonForegroundPressed"] = "OnBrandTextBrush",
        };

        private readonly Button _button;
        private readonly UISettings _settings = new();
        private readonly AccessibilitySettings _accessibility = new();
        private readonly Dictionary<string, object?> _originalResources = new();
        private bool _loaded;
        private bool _chromeApplied;
        private long _enabledToken;
        private long _cornerToken;
        private ContainerVisual? _container;
        private Visual? _previousVisual;
        private SpriteVisual? _sheen;
        private CompositionRoundedRectangleGeometry? _clipGeometry;

        public Effects(Button button)
        {
            _button = button;
            button.Loaded += Loaded;
            button.Unloaded += Unloaded;
            button.PointerEntered += PointerEntered;
            button.PointerExited += PointerExited;
            button.SizeChanged += SizeChanged;
            if (button.IsLoaded) Load();
        }

        private void Loaded(object sender, RoutedEventArgs args) => Load();

        private void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _settings.ColorValuesChanged += ColorsChanged;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _settings.AnimationsEnabledChanged += AnimationsChanged;
            _enabledToken = _button.RegisterPropertyChangedCallback(Control.IsEnabledProperty, EnabledChanged);
            _cornerToken = _button.RegisterPropertyChangedCallback(Control.CornerRadiusProperty, CornerChanged);
            ApplyChrome();
        }

        private void ApplyChrome()
        {
            if (!_loaded) return;
            if (_accessibility.HighContrast)
            {
                RestoreChrome();
                HideSheen();
                return;
            }
            if (_chromeApplied) return;
            foreach (var (key, brush) in ResourceKeys)
            {
                _originalResources[key] = _button.Resources.TryGetValue(key, out var value) ? value : null;
                _button.Resources[key] = ThemeService.Brush(brush);
            }
            _chromeApplied = true;
        }

        private void RestoreChrome()
        {
            if (!_chromeApplied) return;
            foreach (var (key, value) in _originalResources)
            {
                if (value is null) _button.Resources.Remove(key);
                else _button.Resources[key] = value;
            }
            _originalResources.Clear();
            _chromeApplied = false;
        }

        private void PointerEntered(object sender, PointerRoutedEventArgs args)
        {
            if (!_button.IsEnabled || !_settings.AnimationsEnabled || _accessibility.HighContrast ||
                args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) return;
            EnsureSheen();
            HideSheen();
            var compositor = _sheen!.Compositor;
            var linear = compositor.CreateLinearEasingFunction();
            // 斜向亮带的水平包围盒大于自身宽度，起点需将旋转后的整条亮带移到边界外。
            float angle = MathF.Abs(SheenAngleDegrees) * MathF.PI / 180;
            float projectedWidth = _sheen.Size.X * MathF.Cos(angle) + _sheen.Size.Y * MathF.Sin(angle);
            var start = new Vector3(-projectedWidth - SheenEdgePadding, -(float)_button.ActualHeight, 0);
            var end = new Vector3((float)_button.ActualWidth + SheenEdgePadding, start.Y, 0);
            _sheen.Offset = start;
            var sweep = compositor.CreateVector3KeyFrameAnimation();
            sweep.DelayTime = TimeSpan.FromMilliseconds(SheenStartDelayMs);
            sweep.Duration = TimeSpan.FromMilliseconds(SheenDurationMs);
            sweep.InsertKeyFrame(0, start);
            sweep.InsertKeyFrame(1, end, linear);
            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.DelayTime = sweep.DelayTime;
            opacity.Duration = sweep.Duration;
            // 在亮带中心进入左边缘之前完成淡入，右边缘扫完之后才淡出。
            opacity.InsertKeyFrame(0, 0);
            opacity.InsertKeyFrame(0.04f, 1);
            opacity.InsertKeyFrame(0.96f, 1);
            opacity.InsertKeyFrame(1, 0);
            _sheen.StartAnimation("Offset", sweep);
            _sheen.StartAnimation("Opacity", opacity);
        }

        private void EnsureSheen()
        {
            if (_container is null)
            {
                var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
                _previousVisual = ElementCompositionPreview.GetElementChildVisual(_button);
                _container = compositor.CreateContainerVisual();
                if (_previousVisual is not null)
                {
                    ElementCompositionPreview.SetElementChildVisual(_button, null);
                    _container.Children.InsertAtBottom(_previousVisual);
                }
                _clipGeometry = compositor.CreateRoundedRectangleGeometry();
                _container.Clip = compositor.CreateGeometricClip(_clipGeometry);
                var brush = compositor.CreateLinearGradientBrush();
                brush.StartPoint = new Vector2(0, 0);
                brush.EndPoint = new Vector2(1, 0);
                brush.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(0, 255, 255, 255)));
                brush.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, Color.FromArgb(SheenPeakAlpha, 255, 255, 255)));
                brush.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(0, 255, 255, 255)));
                _sheen = compositor.CreateSpriteVisual();
                _sheen.Brush = brush;
                _sheen.RotationAngleInDegrees = SheenAngleDegrees;
                _sheen.Opacity = 0;
                _container.Children.InsertAtTop(_sheen);
                ElementCompositionPreview.SetElementChildVisual(_button, _container);
            }
            UpdateSize();
        }

        private void UpdateSize()
        {
            if (_container is null) return;
            var size = new Vector2((float)_button.ActualWidth, (float)_button.ActualHeight);
            _container.Size = size;
            _clipGeometry!.Size = size;
            _clipGeometry.CornerRadius = new Vector2((float)_button.CornerRadius.TopLeft);
            _sheen!.Size = new Vector2(Math.Max(64, size.X * 0.65f), size.Y * 3);
        }

        private void PointerExited(object sender, PointerRoutedEventArgs args) => HideSheen();
        private void SizeChanged(object sender, SizeChangedEventArgs args) => UpdateSize();
        private void CornerChanged(DependencyObject sender, DependencyProperty property) => UpdateSize();
        private void EnabledChanged(DependencyObject sender, DependencyProperty property) { if (!_button.IsEnabled) HideSheen(); }
        private void HideSheen()
        {
            if (_sheen is null) return;
            _sheen.StopAnimation("Offset");
            _sheen.StopAnimation("Opacity");
            _sheen.Opacity = 0;
        }

        private void ColorsChanged(UISettings sender, object args) => _button.DispatcherQueue.TryEnqueue(ApplyChrome);
        private void AnimationsChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) =>
            _button.DispatcherQueue.TryEnqueue(() => { if (!_settings.AnimationsEnabled) HideSheen(); });

        private void Unloaded(object sender, RoutedEventArgs args) => Unload();
        private void Unload()
        {
            if (!_loaded) return;
            _loaded = false;
            _settings.ColorValuesChanged -= ColorsChanged;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _settings.AnimationsEnabledChanged -= AnimationsChanged;
            _button.UnregisterPropertyChangedCallback(Control.IsEnabledProperty, _enabledToken);
            _button.UnregisterPropertyChangedCallback(Control.CornerRadiusProperty, _cornerToken);
            HideSheen();
            if (_container is not null && ElementCompositionPreview.GetElementChildVisual(_button) == _container)
            {
                if (_previousVisual is not null) _container.Children.Remove(_previousVisual);
                ElementCompositionPreview.SetElementChildVisual(_button, _previousVisual);
            }
            _container = null;
            _sheen = null;
            _clipGeometry = null;
            _previousVisual = null;
            RestoreChrome();
        }

        public void Detach()
        {
            Unload();
            _button.Loaded -= Loaded;
            _button.Unloaded -= Unloaded;
            _button.PointerEntered -= PointerEntered;
            _button.PointerExited -= PointerExited;
            _button.SizeChanged -= SizeChanged;
        }
    }
}
