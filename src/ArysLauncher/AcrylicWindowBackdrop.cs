using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ArysLauncher;

internal sealed class AcrylicWindowBackdrop
{
    private const int DwmCompositionChanged = 0x031E;
    private const int ImmersiveDarkMode = 20;
    private const int SystemBackdropType = 38;
    private const int NoBackdrop = 1;
    private const int DesktopAcrylic = 3;

    private readonly Window window;
    private readonly Border surface;
    private readonly HwndSource source;
    private readonly HwndTarget target;
    private readonly Brush windowBackground;
    private readonly Brush solidSurface;
    private readonly Brush acrylicTint;
    private readonly Color clearColor;
    private bool closed;

    private AcrylicWindowBackdrop(Window window, Border surface, HwndSource source, HwndTarget target)
    {
        this.window = window;
        this.surface = surface;
        this.source = source;
        this.target = target;
        windowBackground = window.Background;
        solidSurface = surface.Background;
        acrylicTint = (Brush)window.FindResource("AcrylicTintBrush");
        clearColor = target.BackgroundColor;
        source.AddHook(WindowMessage);
        window.Closed += WindowClosed;
        Refresh();
    }

    public static AcrylicWindowBackdrop? Attach(Window window, Border surface)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || window.AllowsTransparency ||
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle) is not { CompositionTarget: { } target } source)
        {
            return null;
        }
        return new AcrylicWindowBackdrop(window, surface, source, target);
    }

    public void Refresh()
    {
        if (closed || source.IsDisposed)
        {
            return;
        }
        var handle = source.Handle;
        var margins = new GlassMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        var enabled = !SystemParameters.HighContrast && DwmExtendFrameIntoClientArea(handle, in margins) >= 0;
        if (enabled)
        {
            DwmSetWindowAttribute(handle, ImmersiveDarkMode, 1, sizeof(int));
            enabled = DwmSetWindowAttribute(handle, SystemBackdropType, DesktopAcrylic, sizeof(int)) >= 0;
        }
        if (!enabled)
        {
            DwmSetWindowAttribute(handle, SystemBackdropType, NoBackdrop, sizeof(int));
            margins = default;
            DwmExtendFrameIntoClientArea(handle, in margins);
        }

        window.Background = enabled ? Brushes.Transparent : windowBackground;
        target.BackgroundColor = enabled ? Colors.Transparent : clearColor;
        surface.Background = enabled ? acrylicTint : solidSurface;
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == DwmCompositionChanged)
        {
            window.Dispatcher.InvokeAsync(Refresh, DispatcherPriority.Loaded);
        }
        return 0;
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        closed = true;
        window.Closed -= WindowClosed;
        if (!source.IsDisposed)
        {
            source.RemoveHook(WindowMessage);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlassMargins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, uint valueSize);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, in GlassMargins margins);
}
