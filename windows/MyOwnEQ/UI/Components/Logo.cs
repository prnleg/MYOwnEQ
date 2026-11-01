using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static MyOwnEQ.UI.Loc;

namespace MyOwnEQ.UI;

/// <summary>App logo, same drawing as Logo.swift. Wave: EQ curve with three band dots.
/// Finger: three sliders, the middle one standing tall. Custom: the user's image.</summary>
public sealed class LogoMark : FrameworkElement
{
    private readonly LogoStyle _style;
    private readonly Color[] _colors;
    private readonly ImageSource? _custom;

    public LogoMark(LogoStyle? style = null, Color[]? colors = null)
    {
        _style = style ?? Ui.Store.Settings.Logo;
        _colors = colors ?? Ui.T.CurveColors;
        _custom = Ui.Store.CustomLogo;
    }

    /// <summary>Logo curve shape, x in 0…1 → roughly -0.45…0.55.</summary>
    public static double WaveY(double x) =>
        0.55 * Math.Exp(-Math.Pow((x - 0.22) / 0.13, 2)) - 0.45 * Math.Exp(-Math.Pow((x - 0.52) / 0.12, 2)) + 0.5 * Math.Exp(-Math.Pow((x - 0.8) / 0.11, 2));

    protected override void OnRender(DrawingContext dc) => Draw(dc, Math.Min(ActualWidth, ActualHeight), _style, _colors, _custom);

    public static void Draw(DrawingContext dc, double s, LogoStyle style, Color[] colors, ImageSource? custom)
    {
        if (s <= 0) return;
        if (style == LogoStyle.Custom)
        {
            if (custom is not null)
            {
                double k = Math.Min(s / custom.Width, s / custom.Height);
                dc.DrawImage(custom, new Rect((s - custom.Width * k) / 2, (s - custom.Height * k) / 2, custom.Width * k, custom.Height * k));
            }
            else
            {
                dc.DrawRoundedRectangle(null, new Pen(Brushes.Gray, 2) { DashStyle = DashStyles.Dash }, new Rect(1, 1, s - 2, s - 2), s * 0.2, s * 0.2);
            }
            return;
        }
        double r = s * 0.225;
        var rect = new Rect(0, 0, s, s);
        var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        for (int i = 0; i < colors.Length; i++) grad.GradientStops.Add(new GradientStop(colors[i], i / (double)Math.Max(1, colors.Length - 1)));
        dc.DrawRoundedRectangle(grad, null, rect, r, r);
        var shine = new RadialGradientBrush(Color.FromArgb(90, 255, 255, 255), Colors.Transparent) { Center = new Point(0, 0), GradientOrigin = new Point(0, 0), RadiusX = 0.9, RadiusY = 0.9 };
        dc.DrawRoundedRectangle(shine, new Pen(new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)), Math.Max(0.5, s * 0.008)), rect, r, r);
        if (style == LogoStyle.Finger) DrawFinger(dc, s); else DrawWave(dc, s, colors.FirstOrDefault(Colors.HotPink));
    }

    private static void DrawWave(DrawingContext dc, double s, Color dot)
    {
        var pts = Enumerable.Range(0, 65).Select(i =>
        {
            double x = i / 64.0;
            return new Point(s * (0.14 + 0.72 * x), s * (0.54 - 0.3 * WaveY(x)));
        }).ToArray();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(pts[0], false, false);
            g.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        Pen P(double w, byte a) => new(new SolidColorBrush(Color.FromArgb(a, 255, 255, 255)), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, P(s * 0.11, 50), geo); // soft glow
        dc.DrawGeometry(null, P(s * 0.055, 255), geo);
        foreach (int i in new[] { 14, 33, 51 })
        {
            dc.DrawEllipse(Brushes.White, null, pts[i], s * 0.06, s * 0.06);
            dc.DrawEllipse(new SolidColorBrush(dot), null, pts[i], s * 0.027, s * 0.027);
        }
    }

    private static void DrawFinger(DrawingContext dc, double s)
    {
        double[] xs = { 0.3, 0.5, 0.7 }, knobs = { 0.62, 0.2, 0.66 };
        double top = s * 0.16, bottom = s * 0.84;
        for (int i = 0; i < 3; i++)
        {
            double cx = s * xs[i], ky = s * knobs[i];
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(56, 0, 0, 0)), null, new Rect(cx - s * 0.03, top, s * 0.06, bottom - top), s * 0.03, s * 0.03);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), null, new Rect(cx - s * 0.03, ky, s * 0.06, bottom - ky), s * 0.03, s * 0.03);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), null, new Rect(cx - s * 0.11, ky - s * 0.065, s * 0.22, s * 0.13), s * 0.05, s * 0.05);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(cx - s * 0.085, ky - s * 0.045, s * 0.17, s * 0.09), s * 0.035, s * 0.035);
        }
    }
}

public static class LogoRenderer
{
    /// <summary>Tray icon from the chosen logo; gray when the EQ is off.</summary>
    public static System.Drawing.Icon TrayIcon(bool enabled)
    {
        int px = (int)Math.Round(16 * (System.Windows.Forms.SystemInformation.SmallIconSize.Width / 16.0));
        px = Math.Max(16, px) * 2; // render at 2x, Windows scales down cleanly
        var colors = enabled ? Ui.T.CurveColors : new[] { Color.FromRgb(140, 140, 140), Color.FromRgb(100, 100, 100) };
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) LogoMark.Draw(dc, px, Ui.Store.Settings.Logo, colors, Ui.Store.CustomLogo);
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        using var bmp = new System.Drawing.Bitmap(ms);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}

/// <summary>Title bar in the theme's colors (Windows 11; ignored elsewhere).</summary>
public static class Dwm
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Style(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var t = Ui.T;
        int dark = t.Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var c = t.BaseColor;
        int caption = c.R | c.G << 8 | c.B << 16;               // COLORREF 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
        var ink = t.Ink;
        int text = ink.R | ink.G << 8 | ink.B << 16;
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));    // DWMWA_TEXT_COLOR
    }
}

/// <summary>Small modal prompts (save changes / rename).</summary>
public static class Dialogs
{
    public enum SaveChoice { Save, DontSave, Cancel }

    public static SaveChoice AskSave(Window? owner, string name, bool allowCancel)
    {
        var r = MessageBox.Show(owner ?? Application.Current.MainWindow!,
            L("Save changes to “%@”?", name) + "\n\n" + L("Auto-save is off. Turn it on in Preferences to never see this again."),
            "MyOwnEQ", allowCancel ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo, MessageBoxImage.Question);
        return r switch { MessageBoxResult.Yes => SaveChoice.Save, MessageBoxResult.No => SaveChoice.DontSave, _ => SaveChoice.Cancel };
    }

    /// <summary>Text prompt; null on cancel or empty.</summary>
    public static string? AskName(Window? owner, string title, string current)
    {
        var box = new TextBox { Text = current, MinWidth = 280, FontSize = 14, Margin = new Thickness(0, 10, 0, 14) };
        string? result = null;
        var w = new Window
        {
            Title = "MyOwnEQ",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
            Background = Theme.Frozen(Ui.T.PanelColor),
        };
        var ok = Ui.Button(L("Rename"), () => { result = box.Text.Trim(); w.DialogResult = true; }, prominent: true);
        ok.IsDefault = true;
        var cancel = Ui.Button(L("Cancel"), () => w.DialogResult = false);
        cancel.IsCancel = true;
        var buttons = Ui.HStack(8, cancel, ok);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        w.Content = new StackPanel { Margin = new Thickness(20), Children = { Ui.Text(title, 15, FontWeights.SemiBold), box, buttons } };
        w.SourceInitialized += (_, _) => Dwm.Style(w);
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true && !string.IsNullOrEmpty(result) ? result : null;
    }
}
