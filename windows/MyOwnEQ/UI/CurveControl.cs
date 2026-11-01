using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MyOwnEQ.UI;

/// <summary>
/// Response curve over a live spectrum. Parametric band handles can be dragged
/// (frequency / gain) and the mouse wheel changes Q. The band being edited is
/// highlighted with its own curve and a readout of what moved and by how much.
/// </summary>
public sealed class CurveControl : FrameworkElement
{
    private const double FMin = 20, FMax = 20000;
    private readonly AppController _c;
    private Profile _profile;
    private Curve? _curve;
    private float[]? _spectrum;
    private int? _drag;
    private Band? _dragStart;
    private DateTime _lastFrame;

    public bool Interactive { get; set; } = true;
    public int? Selected { get; set; }
    public event Action<int?>? SelectionChanged;
    /// <summary>Raised during drags; the profile was mutated in place.</summary>
    public event Action? ProfileEdited;

    public CurveControl(AppController c, Profile profile)
    {
        _c = c;
        _profile = profile;
        ClipToBounds = true;
        Focusable = true;
        MinHeight = 240;
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
        Refresh();
    }

    public void SetProfile(Profile p)
    {
        _profile = p;
        Refresh();
    }

    public void Refresh()
    {
        _curve = _c.CurveFor(_profile);
        InvalidateVisual();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if ((DateTime.Now - _lastFrame).TotalMilliseconds < 33) return; // ~30 fps
        _lastFrame = DateTime.Now;
        _spectrum = _c.Spectrum()?.ToArray();
        InvalidateVisual();
    }

    private List<Band> Bands => _profile.Eq is ParametricLayer p ? p.Bands : new();

    private double Range
    {
        get
        {
            double peak = _curve?.Total.Select(Math.Abs).DefaultIfEmpty(0).Max() ?? 0;
            double bp = Bands.Select(b => Math.Abs(b.GainDb)).DefaultIfEmpty(0).Max();
            return Math.Min(30, Math.Max(12, Math.Ceiling(Math.Max(peak, bp) / 6) * 6));
        }
    }

    private double X(double f) => Math.Log(f / FMin) / Math.Log(FMax / FMin) * ActualWidth;
    private double F(double x) => FMin * Math.Pow(FMax / FMin, Math.Clamp(x / ActualWidth, 0, 1));
    private double Y(double db) => ActualHeight / 2 - db / Range * (ActualHeight / 2 - 12);
    private double Db(double y) => (ActualHeight / 2 - y) / (ActualHeight / 2 - 12) * Range;
    private Point Handle(Band b) => new(X(b.Freq), Y(b.Kind.HasGain() ? b.GainDb : 0));

    private static readonly Typeface Face = new("Segoe UI");
    private FormattedText Label(string s, double size, Brush brush) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var bg = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128));
        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, w, h), 8, 8);
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), 1);
        var zero = new Pen(new SolidColorBrush(Color.FromArgb(110, 128, 128, 128)), 1);
        var text = Ui.Secondary;

        foreach (var f in new[] { 50.0, 100, 200, 500, 1000, 2000, 5000, 10000 })
        {
            double gx = X(f);
            dc.DrawLine(grid, new Point(gx, 0), new Point(gx, h));
            dc.DrawText(Label(f >= 1000 ? $"{f / 1000}k" : $"{f}", 10, text), new Point(gx + 2, h - 15));
        }
        for (double d = -Range; d <= Range; d += 6)
        {
            dc.DrawLine(d == 0 ? zero : grid, new Point(0, Y(d)), new Point(w, Y(d)));
            if (d != 0) dc.DrawText(Label($"{d:+0;-0}", 10, text), new Point(4, Y(d) - 14));
        }

        // Spectrum (post-EQ loopback), -96..0 dBFS over full height.
        if (_spectrum is { Length: > 1 } s)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(0, h), true, true);
                for (int i = 0; i < s.Length; i++)
                    ctx.LineTo(new Point(i / (double)(s.Length - 1) * w, h - Math.Clamp((s[i] + 96) / 96.0, 0, 1) * h), true, false);
                ctx.LineTo(new Point(w, h), true, false);
            }
            var fill = new LinearGradientBrush(Color.FromArgb(70, 0, 120, 215), Color.FromArgb(8, 0, 120, 215), 90);
            dc.DrawGeometry(fill, null, geo);
        }

        if (_curve is null || _curve.Freqs.Count == 0) return;

        // Selected band's contribution.
        int offset = _profile.Correction.Count;
        int? sel = _drag ?? Selected;
        if (sel is int si && si < Bands.Count && offset + si < _curve.Bands.Count)
        {
            var bc = _curve.Bands[offset + si];
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(0, Y(0)), true, true);
                for (int i = 0; i < bc.Count; i++) ctx.LineTo(new Point(X(_curve.Freqs[i]), Y(bc[i])), true, false);
                ctx.LineTo(new Point(w, Y(0)), true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(55, 255, 140, 0)), new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 140, 0)), 1), geo);
        }

        // Total (dashed, incl. preamp) and EQ shape (solid).
        var total = new StreamGeometry();
        var shape = new StreamGeometry();
        using (var t = total.Open())
        using (var sh = shape.Open())
        {
            for (int i = 0; i < _curve.Freqs.Count; i++)
            {
                var pt = new Point(X(_curve.Freqs[i]), Y(_curve.Total[i]));
                var pt2 = new Point(pt.X, Y(_curve.Total[i] - _curve.Preamp));
                if (i == 0) { t.BeginFigure(pt, false, false); sh.BeginFigure(pt2, false, false); }
                else { t.LineTo(pt, true, true); sh.LineTo(pt2, true, true); }
            }
        }
        dc.DrawGeometry(null, new Pen(Ui.Secondary, 1) { DashStyle = DashStyles.Dash }, total);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 2.4), shape);

        if (!Interactive) return;
        var white = new Pen(Brushes.White, 1.5);
        for (int i = 0; i < Bands.Count; i++)
        {
            var b = Bands[i];
            var p = Handle(b);
            bool isSel = i == sel;
            var fillB = !b.Enabled ? Brushes.Gray : isSel ? Brushes.DarkOrange : new SolidColorBrush(Color.FromRgb(0, 120, 215));
            dc.DrawEllipse(fillB, white, p, isSel ? 8 : 6.5, isSel ? 8 : 6.5);
            var num = Label($"{i + 1}", 9, Brushes.White);
            dc.DrawText(num, new Point(p.X - num.Width / 2, p.Y - num.Height / 2));
            if (!isSel) continue;
            string label = Ui.Hz(b.Freq) + (b.Kind.HasGain() ? $"  {b.GainDb:+0.0;-0.0;0.0} dB" : "") + $"  Q {b.Q:0.00}";
            if (_dragStart is { } st && _drag == i)
            {
                double df = (b.Freq / st.Freq - 1) * 100;
                label += $"   (Δ {df:+0;-0}%" + (b.Kind.HasGain() ? $", {b.GainDb - st.GainDb:+0.0;-0.0;0.0} dB" : "") + ")";
            }
            var ft = Label(label, 12, (Brush)Application.Current.FindResource(SystemColors.ControlTextBrushKey));
            double ax = Math.Clamp(p.X - ft.Width / 2, 6, w - ft.Width - 6);
            double ay = p.Y < 36 ? p.Y + 14 : p.Y - 34;
            var bgRect = new Rect(ax - 6, ay - 3, ft.Width + 12, ft.Height + 6);
            dc.DrawRoundedRectangle((Brush)Application.Current.FindResource(SystemColors.WindowBrushKey), new Pen(Ui.Secondary, 1), bgRect, 5, 5);
            dc.DrawText(ft, new Point(ax, ay));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!Interactive) return;
        Focus();
        var pos = e.GetPosition(this);
        int? hit = null;
        double best = 16;
        for (int i = 0; i < Bands.Count; i++)
        {
            var p = Handle(Bands[i]);
            double d = Math.Sqrt(Math.Pow(p.X - pos.X, 2) + Math.Pow(p.Y - pos.Y, 2));
            if (d < best) { best = d; hit = i; }
        }
        Selected = hit;
        SelectionChanged?.Invoke(hit);
        if (hit is int h)
        {
            _drag = h;
            _dragStart = Json.Clone(Bands[h]);
            CaptureMouse();
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag is not int i || i >= Bands.Count) return;
        var pos = e.GetPosition(this);
        var b = Bands[i];
        double f = F(pos.X);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(f)) - 2);
        b.Freq = Math.Clamp(Math.Round(f / mag) * mag, 20, 20000);
        if (b.Kind.HasGain()) b.GainDb = Math.Round(Math.Clamp(Db(pos.Y), -24, 24) * 10) / 10;
        ProfileEdited?.Invoke();
        Refresh();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _drag = null;
        _dragStart = null;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!Interactive || Selected is not int i || i >= Bands.Count) return;
        var b = Bands[i];
        b.Q = Math.Round(Math.Clamp(b.Q * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.1, 30) * 100) / 100;
        ProfileEdited?.Invoke();
        Refresh();
        e.Handled = true;
    }
}
