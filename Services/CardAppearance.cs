using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LinxLauncher.Services;

/// <summary>卡片边角：全局设置优先，否则只有功能卡与加载器选择卡使用独立设置。</summary>
public static class CardAppearance
{
    public static readonly DependencyProperty IsCardProperty = DependencyProperty.RegisterAttached(
        "IsCard", typeof(bool), typeof(CardAppearance), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty IsInteractiveCardProperty = DependencyProperty.RegisterAttached(
        "IsInteractiveCard", typeof(bool), typeof(CardAppearance), new PropertyMetadata(false));
    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    public static bool GetIsCard(DependencyObject element) => (bool)element.GetValue(IsCardProperty);
    public static void SetIsCard(DependencyObject element, bool value) => element.SetValue(IsCardProperty, value);
    public static bool GetIsInteractiveCard(DependencyObject element) => (bool)element.GetValue(IsInteractiveCardProperty);
    public static void SetIsInteractiveCard(DependencyObject element, bool value) => element.SetValue(IsInteractiveCardProperty, value);

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if ((bool)args.NewValue) States.GetValue(element, key => new State(key));
        else if (States.TryGetValue(element, out var state))
        {
            state.Detach();
            States.Remove(element);
        }
    }

    private sealed class State
    {
        private readonly FrameworkElement _element;
        private readonly DependencyProperty? _property;
        private CornerRadius _rounded;
        private long _token;
        private bool _loaded;
        private bool _applying;

        public State(FrameworkElement element)
        {
            _element = element;
            _property = element switch
            {
                Control => Control.CornerRadiusProperty,
                Border => Border.CornerRadiusProperty,
                Grid => Grid.CornerRadiusProperty,
                ContentPresenter => ContentPresenter.CornerRadiusProperty,
                _ => null,
            };
            if (_property is null) return;
            element.Loaded += Loaded;
            element.Unloaded += Unloaded;
            if (element.IsLoaded) Load();
        }

        private void Loaded(object sender, RoutedEventArgs args) => Load();
        private void Load()
        {
            if (_loaded || _property is null) return;
            _loaded = true;
            _rounded = (CornerRadius)_element.GetValue(_property);
            _token = _element.RegisterPropertyChangedCallback(_property, RadiusChanged);
            ThemeService.AppearanceChanged += Apply;
            Apply();
        }

        private void RadiusChanged(DependencyObject sender, DependencyProperty property)
        {
            if (_applying) return;
            _rounded = (CornerRadius)_element.GetValue(property);
            Apply();
        }

        private void Apply()
        {
            if (!_loaded || _property is null) return;
            var settings = AppState.Settings;
            bool rounded = !settings.GlobalCardCornersEnabled &&
                (!GetIsInteractiveCard(_element) || settings.RoundedCards);
            _applying = true;
            _element.SetValue(_property, rounded ? _rounded : new CornerRadius(0));
            _applying = false;
        }

        private void Unloaded(object sender, RoutedEventArgs args)
        {
            if (!_loaded || _property is null) return;
            _loaded = false;
            ThemeService.AppearanceChanged -= Apply;
            _element.UnregisterPropertyChangedCallback(_property, _token);
            // 缓存页面重入时从原始半径恢复，再应用当前设置。
            _element.SetValue(_property, _rounded);
        }

        public void Detach()
        {
            Unloaded(_element, new RoutedEventArgs());
            _element.Loaded -= Loaded;
            _element.Unloaded -= Unloaded;
        }
    }
}
