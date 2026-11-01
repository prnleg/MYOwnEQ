using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyOwnEQ.UI;

/// <summary>Small helpers for building WPF UI in code.</summary>
internal static class Ui
{
    public static TextBlock Text(string s, double size = 13, FontWeight? weight = null, Brush? color = null) => new()
    {
        Text = s,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = color ?? (Brush)Application.Current.FindResource(SystemColors.ControlTextBrushKey),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Brush Secondary => new SolidColorBrush(Color.FromArgb(160, 128, 128, 128));
    public static Brush Accent => (Brush)Application.Current.FindResource(SystemColors.AccentColorBrushKey);

    public static Button Button(string label, Action onClick, bool primary = false)
    {
        var b = new Button { Content = label, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0) };
        if (primary && Application.Current.TryFindResource("AccentButtonStyle") is Style s) b.Style = s;
        b.Click += (_, _) => onClick();
        return b;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static StackPanel Column(double spacing = 8, params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var c in children)
        {
            if (c is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top, fe.Margin.Right, spacing);
            p.Children.Add(c);
        }
        return p;
    }

    public static GroupBox Group(string header, UIElement content) => new()
    {
        Header = header,
        Content = content,
        Margin = new Thickness(0, 0, 0, 12),
        Padding = new Thickness(10),
    };

    /// <summary>Labelled slider with live value text.</summary>
    public static Grid Slider(string label, double min, double max, double value, Func<double, string> fmt,
                              Action<double> changed, Action? committed = null, double labelWidth = 80, double step = 0)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        var l = Text(label);
        var v = Text(fmt(value), 12);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        var s = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center };
        if (step > 0) { s.TickFrequency = step; s.IsSnapToTickEnabled = true; }
        s.ValueChanged += (_, e) => { v.Text = fmt(e.NewValue); changed(e.NewValue); };
        if (committed is not null)
        {
            s.PreviewMouseUp += (_, _) => committed();
            s.LostKeyboardFocus += (_, _) => committed();
        }
        Grid.SetColumn(l, 0); Grid.SetColumn(s, 1); Grid.SetColumn(v, 2);
        g.Children.Add(l); g.Children.Add(s); g.Children.Add(v);
        return g;
    }

    public static string Hz(double f) => f >= 1000 ? $"{f / 1000:0.##} kHz" : $"{f:0} Hz";
    public static string Db(double d) => $"{d:+0.0;-0.0;0.0} dB";
}
