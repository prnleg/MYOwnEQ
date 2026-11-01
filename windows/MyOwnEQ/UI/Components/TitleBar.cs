using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using static MyOwnEQ.UI.Loc;

namespace MyOwnEQ.UI;

/// <summary>
/// Title bar blended into the app (like the Mac's hidden title bar): the themed background
/// runs under it; minimize / maximize / close are drawn with the theme. The maximize button
/// answers WM_NCHITTEST as HTMAXBUTTON so Windows 11 snap layouts still pop up on hover.
/// </summary>
// parity: blended-title-bar
public sealed class TitleBar
{
    public const double Height = 36;
    private const int WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2,
        WM_NCMOUSEMOVE = 0x00A0, WM_NCMOUSELEAVE = 0x02A2, HTMAXBUTTON = 9;

    private readonly Window _w;
    private Button? _max;
    private bool _maxHover;

    private TitleBar(Window w) => _w = w;

    /// <summary>Remove the system title bar. Place <see cref="Buttons"/> top-right
    /// (call it again after a theme change to restyle them).</summary>
    public static TitleBar Attach(Window w)
    {
        var tb = new TitleBar(w);
        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = Height,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(tb.Hook);
            int round = 2; // DWMWCP_ROUND: keep Windows 11 rounded corners
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        };
        // A maximized chrome window overhangs the screen by the resize border: pad it back.
        w.StateChanged += (_, _) => tb.Pad();
        return tb;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void Pad()
    {
        if (_w.Content is FrameworkElement root)
            root.Margin = _w.WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        if (_max?.Content is TextBlock g) g.Text = _w.WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    public FrameworkElement Buttons()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        row.Children.Add(Caption("\uE921", L("Minimize"), () => _w.WindowState = WindowState.Minimized, false));
        _max = Caption("\uE922", L("Maximize"), ToggleMax, false);
        row.Children.Add(_max);
        row.Children.Add(Caption("\uE8BB", L("Close"), _w.Close, true));
        Pad();
        return row;
    }

    private void ToggleMax() => _w.WindowState = _w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private Button Caption(string glyph, string tip, Action click, bool close)
    {
        var t = Ui.T;
        var b = new Button
        {
            Width = 46, Height = Height, ToolTip = tip, Focusable = false, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Content = new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 10, Foreground = t.InkBrush(0.85), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Template = Flat(),
        };
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        var hover = close ? Theme.Frozen(Color.FromRgb(0xC4, 0x2B, 0x1C)) : t.InkBrush(0.1);
        b.MouseEnter += (_, _) => { b.Background = hover; if (close) ((TextBlock)b.Content).Foreground = Brushes.White; };
        b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; ((TextBlock)b.Content).Foreground = t.InkBrush(0.85); };
        b.Click += (_, _) => click();
        return b;
    }

    private static ControlTemplate? _flat;

    private static ControlTemplate Flat() => _flat ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
        "<Border Background='{TemplateBinding Background}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
        "</ControlTemplate>");

    /// <summary>Report the maximize button as HTMAXBUTTON (snap layouts) and drive it ourselves.</summary>
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_max is null || !_max.IsLoaded) return IntPtr.Zero;
        switch (msg)
        {
            case WM_NCHITTEST:
                if (OverMax(lParam))
                {
                    handled = true;
                    SetMaxHover(true);
                    return new IntPtr(HTMAXBUTTON);
                }
                SetMaxHover(false);
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                ToggleMax();
                break;
            case WM_NCMOUSEMOVE when wParam.ToInt32() != HTMAXBUTTON:
            case WM_NCMOUSELEAVE:
                SetMaxHover(false);
                break;
        }
        return IntPtr.Zero;
    }

    private bool OverMax(IntPtr lParam)
    {
        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF)), y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        try
        {
            var p = _max!.PointFromScreen(new Point(x, y));
            return p.X >= 0 && p.Y >= 0 && p.X < _max.ActualWidth && p.Y < _max.ActualHeight;
        }
        catch { return false; }
    }

    private void SetMaxHover(bool on)
    {
        if (_max is null || on == _maxHover) return;
        _maxHover = on;
        _max.Background = on ? Ui.T.InkBrush(0.1) : Brushes.Transparent;
    }
}
