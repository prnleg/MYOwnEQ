using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace MyOwnEQ.UI;

/// <summary>Smoothed live spectrum + overall level, fetched at most once per frame
/// so every view that animates can read it (SpectrumFeed in the Mac app).</summary>
public sealed class SpectrumFeed
{
    public static readonly SpectrumFeed Shared = new();
    public float[] Smooth { get; private set; } = Array.Empty<float>();
    public float[] Peaks { get; private set; } = Array.Empty<float>();
    /// <summary>0…1, how loud it is right now (smoothed).</summary>
    public double Level { get; private set; }
    private DateTime _last = DateTime.MinValue;

    public void Update(AppController c)
    {
        var now = DateTime.UtcNow;
        double dt = (now - _last).TotalSeconds;
        if (dt < 1.0 / 90) return;
        _last = now;
        float step = (float)Math.Min(dt, 0.1);
        var s = c.Spectrum();
        if (s is null || s.Length == 0)
        {
            for (int i = 0; i < Smooth.Length; i++) { Smooth[i] = Math.Max(-110, Smooth[i] - step * 40); Peaks[i] = Math.Max(Smooth[i], Peaks[i] - step * 18); }
            Level = Math.Max(0, Level - step * 2);
            return;
        }
        if (Smooth.Length != s.Length) { Smooth = (float[])s.Clone(); Peaks = (float[])s.Clone(); }
        float sum = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float v = Math.Max(s[i], -110);
            float k = v > Smooth[i] ? Math.Min(1, step * 28) : Math.Min(1, step * 6);
            Smooth[i] += (v - Smooth[i]) * k;
            Peaks[i] = Math.Max(Smooth[i], Peaks[i] - step * 18);
            sum += Math.Max(0, (Smooth[i] + 72) / 72);
        }
        double target = sum / s.Length;
        Level += (Math.Min(1, target * 1.8) - Level) * Math.Min(1, step * (target > Level ? 14 : 4));
    }
}

/// <summary>Runs a callback every display frame while loaded (and throttled).</summary>
public static class Frames
{
    public static void Attach(FrameworkElement e, Action tick, double fps = 60)
    {
        DateTime last = DateTime.MinValue;
        EventHandler h = (_, _) =>
        {
            var now = DateTime.UtcNow;
            if ((now - last).TotalSeconds < 1.0 / fps - 0.002) return;
            last = now;
            tick();
        };
        e.Loaded += (_, _) => CompositionTarget.Rendering += h;
        e.Unloaded += (_, _) => CompositionTarget.Rendering -= h;
    }
}

/// <summary>Theme base color with slow drifting color blobs; pulses with the music.</summary>
public sealed class ThemeBackground : FrameworkElement
{
    private readonly AppController _c;
    private readonly bool _compact;
    private readonly DateTime _t0 = DateTime.UtcNow;

    public ThemeBackground(AppController c, bool compact = false)
    {
        _c = c;
        _compact = compact;
        IsHitTestVisible = false;
        Frames.Attach(this, () => { if (Ui.Store.MotionAllowed) InvalidateVisual(); }, 30);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var t = Ui.T;
        if (Ui.Store.Settings.Reactive) SpectrumFeed.Shared.Update(_c);
        double pulse = Ui.Store.Settings.Reactive ? SpectrumFeed.Shared.Level : 0;
        double time = (DateTime.UtcNow - _t0).TotalSeconds * 0.07 * t.Background.Speed;
        dc.DrawRectangle(Theme.Frozen(t.BaseColor), null, new Rect(0, 0, w, h));
        var colors = t.BlobColors;
        int n = Math.Max(colors.Length, 1);
        double dim = Math.Max(w, h);
        for (int i = 0; i < colors.Length; i++)
        {
            double ph = i / (double)n * Math.PI * 2;
            double cx = w * (0.5 + 0.38 * Math.Sin(time * (0.9 + 0.13 * i) + ph));
            double cy = h * (0.5 + 0.36 * Math.Cos(time * (0.7 + 0.17 * i) + ph * 1.3));
            double r = dim * (_compact ? 0.75 : 0.55) * (1 + 0.12 * Math.Sin(time * 1.3 + ph) + 0.18 * pulse);
            double a = t.Background.Intensity * (0.85 + 0.35 * pulse);
            var brush = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Theme.WithAlpha(colors[i], a), 0),
                    new GradientStop(Theme.WithAlpha(colors[i], a * 0.35), 0.45),
                    new GradientStop(Theme.WithAlpha(colors[i], 0), 1),
                },
            };
            dc.DrawEllipse(brush, null, new Point(cx, cy), r, r);
        }
        var vignette = new RadialGradientBrush(Colors.Transparent, Theme.WithAlpha(t.BaseColor, t.Dark ? 0.55 : 0.25))
        { RadiusX = 0.85, RadiusY = 0.85 };
        dc.DrawRectangle(vignette, null, new Rect(0, 0, w, h));
    }
}

/// <summary>Slider whose track fills from 0 (bipolar, for dB) or the left with the theme
/// gradient; the thumb grows and glows while dragged (GlowSlider in the Mac app).</summary>
public sealed class GlowSlider : FrameworkElement
{
    public double Min { get; }
    public double Max { get; }
    public double Step { get; set; }
    public bool Bipolar { get; set; } = true;
    private double _value;
    private bool _drag, _hover;
    private double _thumb = 7; // animated radius

    public event Action<double>? ValueChanged;
    public event Action<bool>? Editing;

    public GlowSlider(double min, double max, double value)
    {
        Min = min; Max = max; _value = value;
        Height = 24;
        MinWidth = 60;
        Cursor = Cursors.Hand;
        Focusable = true;
    }

    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, Min, Max); InvalidateVisual(); }
    }

    private double Frac(double v) => (v - Min) / (Max - Min);

    protected override void OnRender(DrawingContext dc)
    {
        var t = Ui.T;
        double w = ActualWidth, h = ActualHeight, cy = h / 2;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit area
        double pad = 9, tw = w - 2 * pad;
        double x = pad + Frac(_value) * tw;
        double zero = Bipolar ? pad + Frac(0) * tw : pad;
        dc.DrawRoundedRectangle(t.InkBrush(0.12), null, new Rect(pad, cy - 2.5, tw, 5), 2.5, 2.5);
        var fill = new Rect(Math.Min(x, zero), cy - 2.5, Math.Abs(x - zero), 5);
        if (fill.Width > 0)
        {
            var g = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(pad, 0), EndPoint = new Point(pad + tw, 0) };
            var cs = t.CurveColors;
            for (int i = 0; i < cs.Length; i++) g.GradientStops.Add(new GradientStop(cs[i], i / (double)(cs.Length - 1)));
            dc.DrawRoundedRectangle(g, null, fill, 2.5, 2.5);
        }
        if (Bipolar) dc.DrawRectangle(t.InkBrush(0.35), null, new Rect(zero - 0.75, cy - 5.5, 1.5, 11));
        var col = t.CurveColor(Frac(_value));
        double target = _drag ? 9 : _hover ? 8 : 7;
        _thumb += (target - _thumb) * 0.5;
        if (Math.Abs(target - _thumb) > 0.05) Dispatcher.BeginInvoke(InvalidateVisual, System.Windows.Threading.DispatcherPriority.Render);
        var glow = new RadialGradientBrush(Theme.WithAlpha(col, _drag ? 0.75 : 0.35), Theme.WithAlpha(col, 0));
        dc.DrawEllipse(glow, null, new Point(x, cy), _thumb * 2.2, _thumb * 2.2);
        dc.DrawEllipse(Brushes.White, new Pen(Theme.Frozen(col), 2.5), new Point(x, cy), _thumb, _thumb);
    }

    private void SetFromMouse(MouseEventArgs e)
    {
        double pad = 9, tw = Math.Max(1, ActualWidth - 2 * pad);
        double v = Min + Math.Clamp((e.GetPosition(this).X - pad) / tw, 0, 1) * (Max - Min);
        if (Step > 0) v = Math.Round(v / Step) * Step;
        if (Bipolar && Math.Abs(v) < (Max - Min) * 0.012) v = 0; // snap to 0
        if (v == _value) return;
        _value = v;
        InvalidateVisual();
        ValueChanged?.Invoke(v);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _drag = true;
        CaptureMouse();
        Focus();
        Editing?.Invoke(true);
        SetFromMouse(e);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetFromMouse(e); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_drag) return;
        _drag = false;
        ReleaseMouseCapture();
        Editing?.Invoke(false);
        InvalidateVisual();
    }

    protected override void OnMouseEnter(MouseEventArgs e) { _hover = true; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = false; InvalidateVisual(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = Step > 0 ? Step : (Max - Min) / 100;
        double v = e.Key switch { Key.Left or Key.Down => _value - step, Key.Right or Key.Up => _value + step, _ => double.NaN };
        if (double.IsNaN(v)) return;
        Value = v;
        ValueChanged?.Invoke(_value);
        Editing?.Invoke(false);
        e.Handled = true;
    }
}

/// <summary>Segmented switch with a sliding gradient pill (PillPicker in the Mac app).</summary>
public sealed class PillPicker<T> : Border where T : notnull
{
    private readonly Grid _grid = new();
    private readonly Border _pill;
    private readonly List<(T value, TextBlock label)> _opts = new();
    private T _selected;
    public event Action<T>? Changed;

    public PillPicker(IEnumerable<(T value, string label)> options, T selected)
    {
        var t = Ui.T;
        _selected = selected;
        CornerRadius = new CornerRadius(16);
        Padding = new Thickness(3);
        Background = t.InkBrush(0.08);
        BorderBrush = t.InkBrush(0.1);
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Left;
        _pill = new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = t.CurveBrush(),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = new TranslateTransform(),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Color = t.AccentColor, Opacity = 0.5 },
        };
        Grid.SetColumnSpan(_pill, 99);
        _grid.Children.Add(_pill);
        int col = 0;
        foreach (var (value, label) in options)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tb = new TextBlock { Text = label, Padding = new Thickness(14, 5, 14, 5), FontSize = 13, FontFamily = Ui.Font, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            Grid.SetColumn(tb, col++);
            var v = value;
            tb.MouseLeftButtonUp += (_, _) => Select(v, fire: true);
            _grid.Children.Add(tb);
            _opts.Add((value, tb));
        }
        Child = _grid;
        Loaded += (_, _) => Place(animate: false);
    }

    public T Selected => _selected;

    public void Select(T v, bool fire)
    {
        if (EqualityComparer<T>.Default.Equals(v, _selected)) return;
        _selected = v;
        Place(animate: true);
        if (fire) Changed?.Invoke(v);
    }

    private void Place(bool animate)
    {
        var t = Ui.T;
        double x = 0;
        foreach (var (value, label) in _opts)
        {
            bool on = EqualityComparer<T>.Default.Equals(value, _selected);
            label.Foreground = on ? Brushes.White : t.InkBrush(0.6);
            label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (on)
            {
                double w = label.DesiredSize.Width, h = label.DesiredSize.Height;
                _pill.Height = h;
                if (animate && !double.IsNaN(_pill.Width)) { Ui.Spring(_pill, WidthProperty, w); Ui.Slide((TranslateTransform)_pill.RenderTransform, x); }
                else { _pill.Width = w; ((TranslateTransform)_pill.RenderTransform).X = x; }
                return;
            }
            x += label.DesiredSize.Width;
        }
    }
}

/// <summary>Animated on/off switch in the theme gradient.</summary>
public sealed class ToggleSwitch : FrameworkElement
{
    private bool _on;
    private double _pos;
    public event Action<bool>? Toggled;

    public ToggleSwitch(bool on)
    {
        _on = on;
        _pos = on ? 1 : 0;
        Width = 40; Height = 22;
        Cursor = Cursors.Hand;
        Focusable = true;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public bool IsOn
    {
        get => _on;
        set { _on = value; _pos = value ? 1 : 0; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var t = Ui.T;
        double target = _on ? 1 : 0;
        _pos += (target - _pos) * (Ui.Store.MotionAllowed ? 0.35 : 1);
        if (Math.Abs(target - _pos) > 0.01) Dispatcher.BeginInvoke(InvalidateVisual, System.Windows.Threading.DispatcherPriority.Render);
        else _pos = target;
        var r = new Rect(0, 0, ActualWidth, ActualHeight);
        Brush track = _pos > 0.5 ? t.CurveBrush() : t.InkBrush(0.18);
        dc.DrawRoundedRectangle(track, null, r, r.Height / 2, r.Height / 2);
        double kr = r.Height / 2 - 3;
        double kx = 3 + kr + _pos * (r.Width - 2 * kr - 6);
        dc.DrawEllipse(Brushes.White, null, new Point(kx, r.Height / 2), kr, kr);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _on = !_on;
        InvalidateVisual();
        Toggled?.Invoke(_on);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter)) return;
        _on = !_on;
        InvalidateVisual();
        Toggled?.Invoke(_on);
        e.Handled = true;
    }

    /// <summary>Labelled switch row ("Toggle" in SwiftUI).</summary>
    public static DockPanel Row(string label, bool on, Action<bool> changed, bool enabled = true)
    {
        var sw = new ToggleSwitch(on) { IsEnabled = enabled, Opacity = enabled ? 1 : 0.45 };
        sw.Toggled += changed;
        var text = Ui.Text(label, 13, opacity: enabled ? 1 : 0.5);
        text.Margin = new Thickness(0, 0, 12, 0);
        var row = Ui.Spread(text, sw);
        row.Margin = new Thickness(0, 3, 0, 3);
        return row;
    }
}

/// <summary>Tiny frequency-response line for lists and chips.</summary>
public sealed class MiniCurve : FrameworkElement
{
    private double[] _values = Array.Empty<double>();
    public double LineWidth { get; set; } = 1.6;

    public double[] Values
    {
        get => _values;
        set { _values = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_values.Length < 2 || ActualWidth <= 0) return;
        double w = ActualWidth, h = ActualHeight;
        double range = Math.Max(6, _values.Select(Math.Abs).Max());
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < _values.Length; i++)
            {
                var p = new Point(i / (double)(_values.Length - 1) * w, h / 2 - _values[i] / range * (h / 2 - 1.5));
                if (i == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, true);
            }
        }
        dc.DrawGeometry(null, new Pen(Ui.T.CurveBrush(), LineWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geo);
    }
}
