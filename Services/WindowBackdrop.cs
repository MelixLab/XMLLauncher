using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LinxLauncher.Services;

/// <summary>
/// 单窗口背景宿主。切材质时保留 Window.SystemBackdrop 和控制器，
/// 避免反复拆卸 XAML 背景宿主、销毁控制器；纯色由根元素的不透明底色提供。
/// </summary>
internal sealed partial class WindowBackdrop : SystemBackdrop
{
    private ICompositionSupportsSystemBackdrop? _target;
    private SystemBackdropConfiguration? _configuration;
    private MicaController? _micaController;
    private DesktopAcrylicController? _acrylicController;
    private ThemeMaterial? _attachedMaterial;
    private ThemeMaterial _material;

    public WindowBackdrop(ThemeMaterial material)
    {
        _material = material;
    }

    public void SetMaterial(ThemeMaterial material)
    {
        _material = material;
        if (_target is null || _configuration is null) return;
        if (_attachedMaterial == material) return;

        bool useMica = material is ThemeMaterial.Mica or ThemeMaterial.MicaAlt;
        if (useMica)
        {
            _micaController ??= new MicaController();
            _micaController.Kind = material == ThemeMaterial.Mica ? MicaKind.Base : MicaKind.BaseAlt;
            // 必须先配置再接到窗口，避免控制器以默认主题生成最初的背景。
            // 不自定义 Tint/Fallback 等属性，保留系统的深浅色及高对比度处理。
            _micaController.SetSystemBackdropConfiguration(_configuration);
            if (_attachedMaterial is ThemeMaterial.Mica or ThemeMaterial.MicaAlt)
            {
                _attachedMaterial = material;
                return;
            }
        }
        else if (material == ThemeMaterial.Acrylic)
        {
            _acrylicController ??= new DesktopAcrylicController();
            _acrylicController.SetSystemBackdropConfiguration(_configuration);
        }

        DetachController();
        // 连接失败时也保留本次控制器的记录，让调用方回落到纯色时能够清理部分连接。
        _attachedMaterial = material;
        if (useMica)
        {
            if (!_micaController!.AddSystemBackdropTarget(_target))
                throw new InvalidOperationException("无法连接云母背景控制器。");
        }
        else if (material == ThemeMaterial.Acrylic)
        {
            if (!_acrylicController!.AddSystemBackdropTarget(_target))
                throw new InvalidOperationException("无法连接亚克力背景控制器。");
        }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _target = connectedTarget;
        // 默认配置持续跟随根元素主题、激活状态与高对比度，不另行缓存启动时的主题。
        _configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        SetMaterial(_material);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        try
        {
            DetachController();
        }
        finally
        {
            // 离开窗口后才释放控制器，避免切换过程与旧控制器的异步销毁交错。
            try
            {
                _micaController?.Dispose();
            }
            finally
            {
                try
                {
                    _acrylicController?.Dispose();
                }
                finally
                {
                    _micaController = null;
                    _acrylicController = null;
                    _configuration = null;
                    _target = null;
                    _attachedMaterial = null;
                    base.OnTargetDisconnected(disconnectedTarget);
                }
            }
        }
    }

    private void DetachController()
    {
        if (_target is null) return;

        if (_attachedMaterial is ThemeMaterial.Mica or ThemeMaterial.MicaAlt)
            _micaController?.RemoveSystemBackdropTarget(_target);
        else if (_attachedMaterial == ThemeMaterial.Acrylic)
            _acrylicController?.RemoveSystemBackdropTarget(_target);

        _attachedMaterial = null;
    }
}
