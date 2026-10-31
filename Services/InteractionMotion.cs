using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace LinxLauncher.Services;

/// <summary>
/// 功能卡片、加载器选择卡与普通按钮共用的按压反馈。
/// 读取原生 IsPressed，不接管点击、指针捕获或键盘操作；只改变视觉缩放。
/// </summary>
public static class InteractionMotion
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty HoverScaleProperty = DependencyProperty.RegisterAttached(
        "HoverScale", typeof(double), typeof(InteractionMotion), new PropertyMetadata(1.0));

    public static readonly DependencyProperty PressedScaleProperty = DependencyProperty.RegisterAttached(
        "PressedScale", typeof(double), typeof(InteractionMotion), new PropertyMetadata(0.975));

    private static readonly ConditionalWeakTable<ButtonBase, MotionState> States = new();

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static double GetHoverScale(DependencyObject element) => (double)element.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject element, double value) => element.SetValue(HoverScaleProperty, value);
    public static double GetPressedScale(DependencyObject element) => (double)element.GetValue(PressedScaleProperty);
    public static void SetPressedScale(DependencyObject element, double value) => element.SetValue(PressedScaleProperty, value);

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ButtonBase button) return;
        if ((bool)args.NewValue) Attach(button, button);
        else
        {
            if (States.TryGetValue(button, out var state)) state.Detach();
            States.Remove(button);
        }
    }

    // 卡片主体由透明导航按钮接收输入，整张卡片的根容器承担视觉反馈。
    internal static void Attach(ButtonBase button, FrameworkElement target)
    {
        if (States.TryGetValue(button, out _)) return;
        States.Add(button, new MotionState(button, target));
    }

    private sealed class MotionState
    {
        private readonly ButtonBase _source;
        private readonly FrameworkElement _target;
        private readonly UISettings _settings = new();
        private readonly AccessibilitySettings _accessibility = new();
        private bool _loaded;
        private bool _hovered;
        private bool _pressed;
        private float? _destination;
        private long _pressedToken;
        private long _enabledToken;
        private Vector3 _originalCenter;
        private SpringVector3NaturalMotionAnimation? _spring;
        private Vector3KeyFrameAnimation? _pressAnimation;

        public MotionState(ButtonBase source, FrameworkElement target)
        {
            _source = source;
            _target = target;
            source.Loaded += Loaded;
            source.Unloaded += Unloaded;
            source.PointerEntered += PointerEntered;
            source.PointerExited += PointerExited;
            target.SizeChanged += SizeChanged;
            if (source.IsLoaded) Load();
        }

        private void Loaded(object sender, RoutedEventArgs args) => Load();

        private void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _originalCenter = _target.CenterPoint;
            _pressedToken = _source.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, StateChanged);
            _enabledToken = _source.RegisterPropertyChangedCallback(Microsoft.UI.Xaml.Controls.Control.IsEnabledProperty, StateChanged);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _settings.AnimationsEnabledChanged += AnimationsChanged;
            // AccessibilitySettings.HighContrastChanged 在无 CoreWindow 的桌面环境可能订阅失败。
            _settings.ColorValuesChanged += ColorsChanged;
            UpdateCenter();
            Update();
        }

        private void Unloaded(object sender, RoutedEventArgs args) => Unload();

        private void Unload()
        {
            if (!_loaded) return;
            _loaded = false;
            _source.UnregisterPropertyChangedCallback(ButtonBase.IsPressedProperty, _pressedToken);
            _source.UnregisterPropertyChangedCallback(Microsoft.UI.Xaml.Controls.Control.IsEnabledProperty, _enabledToken);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _settings.AnimationsEnabledChanged -= AnimationsChanged;
            _settings.ColorValuesChanged -= ColorsChanged;
            _hovered = _pressed = false;
            _destination = null;
            StopAnimations();
            _target.Scale = Vector3.One;
            _target.CenterPoint = _originalCenter;
        }

        public void Detach()
        {
            Unload();
            _source.Loaded -= Loaded;
            _source.Unloaded -= Unloaded;
            _source.PointerEntered -= PointerEntered;
            _source.PointerExited -= PointerExited;
            _target.SizeChanged -= SizeChanged;
        }

        private void StateChanged(DependencyObject sender, DependencyProperty property) => Update();

        private void StopAnimations()
        {
            if (_spring is not null) _target.StopAnimation(_spring);
            if (_pressAnimation is not null) _target.StopAnimation(_pressAnimation);
        }

        private void PointerEntered(object sender, PointerRoutedEventArgs args)
        {
            // 触摸松开后不留下鼠标式放大。
            if (args.Pointer.PointerDeviceType == PointerDeviceType.Touch) return;
            _hovered = true;
            Update();
        }

        private void PointerExited(object sender, PointerRoutedEventArgs args)
        {
            _hovered = false;
            Update();
        }

        private void SizeChanged(object sender, SizeChangedEventArgs args) => UpdateCenter();

        private void UpdateCenter()
        {
            if (_loaded) _target.CenterPoint = new Vector3((float)_target.ActualWidth / 2, (float)_target.ActualHeight / 2, 0);
        }

        private void AnimationsChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) => RefreshSettings();
        private void ColorsChanged(UISettings sender, object args) => RefreshSettings();

        private void RefreshSettings() => _source.DispatcherQueue.TryEnqueue(() =>
        {
            _destination = null;
            Update();
        });

        private void Update()
        {
            if (!_loaded) return;
            bool wasPressed = _pressed;
            _pressed = _source.IsPressed;
            bool animate = _source.IsEnabled && _settings.AnimationsEnabled && !_accessibility.HighContrast;
            float destination = !animate ? 1f
                : _pressed ? (float)Math.Clamp(GetPressedScale(_source), 0.95, 1)
                : _hovered ? (float)Math.Clamp(GetHoverScale(_source), 1, 1.02)
                : 1f;
            if (_destination == destination) return;
            _destination = destination;
            var value = new Vector3(destination, destination, 1);
            if (!animate)
            {
                StopAnimations();
                _target.Scale = value;
                return;
            }

            var compositor = CompositionTarget.GetCompositorForCurrentThread();
            if (_pressed)
            {
                _pressAnimation ??= compositor.CreateVector3KeyFrameAnimation();
                _pressAnimation.Target = "Scale";
                _pressAnimation.Duration = TimeSpan.FromMilliseconds(70);
                _pressAnimation.InsertExpressionKeyFrame(0, "this.StartingValue");
                _pressAnimation.InsertKeyFrame(1, value);
                _target.StartAnimation(_pressAnimation);
            }
            else
            {
                _spring ??= compositor.CreateSpringVector3Animation();
                _spring.Target = "Scale";
                _spring.FinalValue = value;
                _spring.DampingRatio = wasPressed ? 0.78f : 1f;
                _spring.Period = TimeSpan.FromMilliseconds(50);
                // 不手动重置起点：快速反向时由合成层衔接当前视觉值和速度。
                _target.StartAnimation(_spring);
            }
        }
    }
}
