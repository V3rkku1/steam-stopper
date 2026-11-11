using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace SteamStopper;

internal static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeOld = 19;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwcpRound = 2;

    private const int DwmwaSystemBackdropType = 38;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>
    /// Windows 11 (22H2+) backdrop behind the window: "acrylic" (blurred desktop) or "mica" (tinted wallpaper),
    /// seen through the theme's see-through background. False when Windows can't, so the caller goes solid.
    /// </summary>
    public static bool SetBackdrop(Window window, string kind)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var type = kind switch { "acrylic" => 3, "mica" => 2, _ => 1 };
        if (Environment.OSVersion.Version.Build < 22621) return type == 1;
        // The chrome keeps re-applying its glass frame, so the frame size goes through it too.
        if (WindowChrome.GetWindowChrome(window) is { } chrome) chrome.GlassFrameThickness = new Thickness(type == 1 ? 0 : -1);
        // WPF paints its own black behind the content unless told not to.
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = type == 1 ? System.Windows.Media.Colors.Black : System.Windows.Media.Colors.Transparent;
        var margins = type == 1 ? new Margins() : new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref type, sizeof(int)) == 0 || type == 1;
    }

    public static void Apply(Window window)
    {
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 56,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref on, sizeof(int));
        int round = DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        int caption = 0x00141211;
        _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        int border = 0x002D2726;
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        int text = 0x00EDEDED;
        _ = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
    }
}
