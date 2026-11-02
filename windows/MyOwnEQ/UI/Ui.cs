using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace MyOwnEQ.UI;

/// <summary>App-wide UI state (the Mac app's environment objects) + small builders.</summary>
public static class Ui
{
    public static ThemeStore Store { get; private set; } = null!;
    public static Lang Lang { get; private set; } = null!;

    /// <summary>After the core exists: themes and languages are loaded through it.</summary>
    public static void Init(CoreBridge core)
    {
        Store = new ThemeStore(core);
        Lang = new Lang(Store.Settings.Language, core);
    }

    public static Theme T => Store.Current;

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    /// <summary>Windows 11 icon font (falls back to Windows 10's).</summary>
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // ---- glyphs (Segoe Fluent Icons)
    public const string GHeadphones = "\uE7F6", GSync = "\uE895", GSettings = "\uE713", GPalette = "\uE790",
        GAdd = "\uE710", GEdit = "\uE70F", GVolume = "\uE767", GMute = "\uE74F", GBolt = "\uE945",
        GStar = "\uE734", GStarFill = "\uE735", GSearch = "\uE721", GDelete = "\uE74D", GCheck = "\uE73E",
        GShield = "\uEA18", GEar = "\uE7F5", GChevron = "\uE76C", GWave = "\uE9D9", GSpeaker = "\uE7F5",
        GDial = "\uE9E9", GSparkle = "\uE945", GGlobe = "\uE774", GLink = "\uE71B", GWarning = "\uE7BA",
        GDownload = "\uE896", GInfo = "\uE946", GPhoto = "\uEB9F", GDoc = "\uE8A5", GPencilSave = "\uE74E", GCopy = "\uE8C8";

    // ---- text

    public static TextBlock Text(string s, double size = 13, FontWeight? weight = null, double opacity = 1, Brush? color = null) => new()
    {
        Text = s,
        FontSize = size,
        FontFamily = size >= 20 ? Display : Font,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = color ?? T.InkBrush(opacity),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Secondary(string s, double size = 12) => Text(s, size, opacity: 0.6);

    public static TextBlock Caps(string s) => new()
    {
        Text = s.ToUpper(System.Globalization.CultureInfo.CurrentCulture),
        FontSize = 11,
        FontWeight = FontWeights.Bold,
        FontFamily = Font,
        Foreground = T.InkBrush(0.55),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Glyph(string g, double size = 14, Brush? color = null) => new()
    {
        Text = g,
        FontFamily = Icons,
        FontSize = size,
        Foreground = color ?? T.InkBrush(),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    public static StackPanel HStack(double spacing, params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) Add(p, c, spacing, horizontal: true);
        return p;
    }

    public static StackPanel VStack(double spacing, params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var c in children) Add(p, c, spacing, horizontal: false);
        return p;
    }

    /// <summary>Append with spacing before it (except the first).</summary>
    public static void Add(Panel p, UIElement c, double spacing, bool horizontal)
    {
        if (p.Children.Count > 0 && c is FrameworkElement fe)
            fe.Margin = horizontal
                ? new Thickness(fe.Margin.Left + spacing, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom)
                : new Thickness(fe.Margin.Left, fe.Margin.Top + spacing, fe.Margin.Right, fe.Margin.Bottom);
        p.Children.Add(c);
    }

    /// <summary>Row with an element pushed to the right edge.</summary>
    public static DockPanel Spread(UIElement left, UIElement right)
    {
        var d = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(right, Dock.Right);
        d.Children.Add(right);
        d.Children.Add(left);
        return d;
    }

    // ---- surfaces

    /// <summary>Glass card: theme panel color, faint gradient border, soft shadow.</summary>
    public static Border Glass(UIElement child, double radius = 14, bool highlighted = false, double padding = 14, bool shadow = true)
    {
        var t = T;
        // Frosted look without real backdrop blur (WPF can't blur what's behind):
        // denser panel, lighter at the top like light on glass, and a lit top-left edge.
        var top = Theme.Mix(t.PanelColor, Colors.White, t.Dark ? 0.07 : 0.35);
        var bg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        bg.GradientStops.Add(new GradientStop(Theme.WithAlpha(top, t.Dark ? 0.78 : 0.85), 0));
        bg.GradientStops.Add(new GradientStop(Theme.WithAlpha(t.PanelColor, t.Dark ? 0.70 : 0.78), 1));
        bg.Freeze();
        var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        edge.GradientStops.Add(new GradientStop(Theme.WithAlpha(Colors.White, t.Dark ? 0.22 : 0.7), 0));
        edge.GradientStops.Add(new GradientStop(Theme.WithAlpha(t.Ink, 0.06), 0.55));
        edge.GradientStops.Add(new GradientStop(Theme.WithAlpha(t.Ink, 0.03), 1));
        edge.Freeze();
        var b = new Border
        {
            Child = child,
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(padding),
            Background = bg,
            BorderThickness = new Thickness(highlighted ? 1.5 : 1),
            BorderBrush = highlighted ? t.DiagonalBrush() : edge,
        };
        if (shadow) b.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = t.Dark ? 0.35 : 0.1, Color = Colors.Black };
        return b;
    }

    /// <summary>Titled glass card (Card in the Mac app).</summary>
    public static Border Card(string? title, string? glyph, UIElement content, double padding = 14)
    {
        var col = new StackPanel();
        if (title is not null)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            if (glyph is not null)
            {
                var g = Glyph(glyph, 13, T.CurveBrush());
                g.Margin = new Thickness(0, 0, 7, 0);
                head.Children.Add(g);
            }
            head.Children.Add(Caps(title));
            col.Children.Add(head);
        }
        col.Children.Add(content);
        return Glass(col, padding: padding);
    }

    public static Border Tag(string text, Color color) => new()
    {
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(7, 2, 7, 2),
        Background = Theme.Frozen(Theme.WithAlpha(color, 0.15)),
        Child = new TextBlock { Text = text, FontSize = 9.5, FontWeight = FontWeights.Heavy, Foreground = Theme.Frozen(color), FontFamily = Font },
    };

    public static UIElement PageHeader(string title, string subtitle, string glyph)
    {
        var t = T;
        var icon = new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(11),
            Background = t.DiagonalBrush(),
            Child = Glyph(glyph, 20, Brushes.White),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Color = t.AccentColor, Opacity = 0.5 },
        };
        var text = VStack(2, Text(title, 22, FontWeights.Bold), Secondary(subtitle, 13));
        return HStack(12, icon, text);
    }

    // ---- buttons

    private static ControlTemplate? _buttonTemplate;

    /// <summary>Rounded button with hover + press feedback (GlowButtonStyle in the Mac app).</summary>
    public static Button Button(string label, Action onClick, bool prominent = false, string? glyph = null)
    {
        _buttonTemplate ??= (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' " +
            "BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='16' Padding='{TemplateBinding Padding}' RenderTransformOrigin='0.5,0.5' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
            "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.88'/></Trigger>" +
            // Setters can only target named elements (the Border), not a named transform inside it.
            "<Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='RenderTransform'>" +
            "<Setter.Value><ScaleTransform ScaleX='0.96' ScaleY='0.96'/></Setter.Value></Setter></Trigger>" +
            "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.4'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>");
        var t = T;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null)
        {
            var g = Glyph(glyph, 12, prominent ? Brushes.White : t.InkBrush());
            g.Margin = new Thickness(0, 0, 6, 0);
            content.Children.Add(g);
        }
        content.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = Font,
                                             Foreground = prominent ? Brushes.White : t.InkBrush() });
        var b = new Button
        {
            Template = _buttonTemplate,
            Content = content,
            Padding = new Thickness(14, 7, 14, 7),
            Cursor = Cursors.Hand,
            Background = prominent ? t.CurveBrush() : t.InkBrush(0.08),
            BorderBrush = prominent ? Brushes.Transparent : t.InkBrush(0.12),
            BorderThickness = new Thickness(1),
            Focusable = true,
        };
        if (prominent) b.Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 1, Color = t.AccentColor, Opacity = 0.45 };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Borderless glyph button (pencil, star, trash…).</summary>
    public static Button IconButton(string glyph, Action onClick, string tooltip, Brush? color = null, double size = 14)
    {
        var b = Button("", onClick);
        var g = Glyph(glyph, size, color ?? T.InkBrush(0.7));
        b.Content = g;
        b.Background = Brushes.Transparent;
        b.BorderBrush = Brushes.Transparent;
        b.Padding = new Thickness(6);
        b.ToolTip = tooltip;
        return b;
    }

    // ---- motion helpers

    public static void Spring(UIElement e, DependencyProperty p, double to, double ms = 350, bool bounce = true)
    {
        if (!Store.MotionAllowed) { e.BeginAnimation(p, null); e.SetValue(p, to); return; }
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = bounce ? new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        e.BeginAnimation(p, a);
    }

    public static void Slide(TranslateTransform tt, double x)
    {
        if (!Store.MotionAllowed) { tt.BeginAnimation(TranslateTransform.XProperty, null); tt.X = x; return; }
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(350))
        { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Fade + rise when a page or panel appears.</summary>
    public static void Appear(FrameworkElement e)
    {
        if (!Store.MotionAllowed) return;
        var tt = new TranslateTransform(0, 10);
        e.RenderTransform = tt;
        e.Opacity = 0;
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(380))
        { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
    }

    // ---- notices

    /// <summary>Toast for <see cref="AppController.Notice"/>; click to dismiss. Null when none.</summary>
    // parity: notices
    public static FrameworkElement? Toast(AppController c)
    {
        if (c.Notice is not { } n) return null;
        var t = T;
        var row = HStack(8, Glyph(n.IsError ? GWarning : GInfo, 14, n.IsError ? Theme.Frozen(Colors.Orange) : Theme.Frozen(t.AccentColor)), Text(n.Text, 13));
        var box = Glass(row, radius: 14, highlighted: n.IsError, padding: 12, shadow: true);
        box.Cursor = Cursors.Hand;
        box.MaxWidth = 520;
        box.HorizontalAlignment = HorizontalAlignment.Center;
        box.VerticalAlignment = VerticalAlignment.Bottom;
        box.MouseLeftButtonUp += (_, _) => c.DismissNotice();
        return box;
    }

    /// <summary>Small indeterminate spinner for long actions (update check).</summary>
    public static FrameworkElement Spinner()
    {
        var g = Glyph("\uE895", 14, T.CurveBrush());
        g.RenderTransformOrigin = new Point(0.5, 0.5);
        var rot = new RotateTransform();
        g.RenderTransform = rot;
        rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        return g;
    }

    // ---- formatting

    public static string Hz(double f) => f >= 1000 ? $"{f / 1000:0.00} kHz" : $"{f:0} Hz";
    public static string Db(double d) => $"{d:+0.0;-0.0;0.0} dB";
}
