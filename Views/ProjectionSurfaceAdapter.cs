using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;

namespace LinxLauncher.Views;

internal static class ProjectionSurfaceAdapter
{
    public static nint GetNativeHandle(VirtualSurfaceImageSource surface) =>
        ((IWinRTObject)surface).NativeObject.ThisPtr;

    public static (uint Width, uint Height) GetPixelSize(FrameworkElement viewport)
    {
        double scale = viewport.XamlRoot?.RasterizationScale ?? 1;
        return ((uint)Math.Max(1, Math.Round(viewport.ActualWidth * scale)),
                (uint)Math.Max(1, Math.Round(viewport.ActualHeight * scale)));
    }
}
