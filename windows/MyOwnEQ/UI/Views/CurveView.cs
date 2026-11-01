using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using static MyOwnEQ.UI.Loc;

namespace MyOwnEQ.UI;

/// <summary>FrameworkElement that draws through a callback (layers of the curve).</summary>
internal sealed class DrawLayer : FrameworkElement
{
    private readonly Action<DrawingContext, Size> _draw;
    public DrawLayer(Action<DrawingContext, Size> draw) { _draw = draw; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc) => _draw(dc, RenderSize);
}

/// <summary>
/// Frequency response over the live spectrum (port of CurveView.swift). Springs between
/// states, glows with the music, shimmers, and is editable on the graph:
/// Pro parametric = drag dots (Shift+drag or wheel = Q, double-click adds a band),
/// Pro graphic = drag each slider up/down, Easy = Bass/Mids/Clarity/Treble dots.
/// The profile is mutated in place; <see cref="ProfileEdited"/> fires after each change.
/// </summary>
public sealed class CurveView : Grid
{
    public enum Edit { None, Easy, Pro }

    private const double FMin = 20, FMax = 20000;
    private readonly AppController _c;
    private Profile _profile;
    private readonly Edit _edit;
    private readonly bool _compact;

    private readonly DrawLayer _back, _front;
    private readonly Path _glow, _stroke, _shimmer;

    private Curve? _curve;
    private bool _curveDirty = true;
    private double _rate;

    // spring state (CurveAnim)
    private double[] _shape = Array.Empty<double>(), _shapeV = Array.Empty<double>();
    private double[] _total = Array.Empty<double>(), _totalV = Array.Empty<double>();
    private double[] _band = Array.Empty<double>(), _bandV = Array.Empty<double>();
    private int? _bandIndex;
    private double _range = 12, _rangeV;
    private double[] _hScale = Array.Empty<double>(), _hScaleV = Array.Empty<double>();
    private readonly List<(Point pt, DateTime t0, Color color)> _ripples = new();
    private DateTime _lastT = DateTime.MinValue;
    private readonly DateTime _t0 = DateTime.UtcNow;

    // interaction
    private int? _dragging, _hover;
    private Point _dragStartPt, _dragStartHandle;
    private Profile? _dragStartProfile;
    private bool _qMode, _moved;

    public int? Selected { get; set; }
    public event Action<int?>? SelectionChanged;
    public event Action? ProfileEdited;

    public CurveView(AppController c, Profile profile, Edit edit, bool compact = false)
    {
        _c = c;
        _profile = profile;
        _edit = edit;
        _compact = compact;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Focusable = edit != Edit.None;
        _back = new DrawLayer(DrawBack);
        _glow = new Path { IsHitTestVisible = false, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Effect = new BlurEffect { Radius = compact ? 6 : 10 } };
        _stroke = new Path { IsHitTestVisible = false, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        _shimmer = new Path { IsHitTestVisible = false, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        _front = new DrawLayer(DrawFront);
        Children.Add(_back);
        Children.Add(_glow);
        Children.Add(_stroke);
        Children.Add(_shimmer);
        Children.Add(_front);
        Frames.Attach(this, Tick, 60);
    }

    /// <summary>Profile edited from outside (sliders, mode switch): recompute the curve.</summary>
    public void Invalidate() => _curveDirty = true;

    public void SetProfile(Profile p)
    {
        _profile = p;
        _curveDirty = true;
    }

    // ------------------------------------------------------------ geometry

    private List<Band> Bands => _profile.Eq is ParametricLayer p ? p.Bands : new();
    private List<GraphicPoint> Points => _profile.Eq is GraphicLayer g ? g.Points : new();
    private double Pad => _compact ? 6 : 16;
    private double W => ActualWidth;
    private double H => ActualHeight;
    private double X(double f) => Math.Log(f / FMin) / Math.Log(FMax / FMin) * W;
    private double F(double x) => FMin * Math.Pow(FMax / FMin, Math.Clamp(x / Math.Max(W, 1), 0, 1));
    private double Y(double db) => H / 2 - db / _range * (H / 2 - Pad);
    private double Db(double y) => (H / 2 - y) / (H / 2 - Pad) * _range;

    private static readonly (string name, double freq, Func<Macros, double> get, Action<Macros, double> set)[] MacroSpots =
    {
        ("Bass", 105, m => m.Bass, (m, v) => m.Bass = v),
        ("Mids", 1000, m => m.Mids, (m, v) => m.Mids = v),
        ("Clarity", 3000, m => m.Clarity, (m, v) => m.Clarity = v),
        ("Treble", 8000, m => m.Treble, (m, v) => m.Treble = v),
    };

    private enum HKind { Band, Point, Macro }
    private readonly record struct Handle(HKind Kind, int Index, Point Pt, bool Enabled);

    private List<Handle> Handles()
    {
        var hs = new List<Handle>();
        switch (_edit)
        {
            case Edit.Easy:
                for (int i = 0; i < MacroSpots.Length; i++)
                    hs.Add(new Handle(HKind.Macro, i, new Point(X(MacroSpots[i].freq), Y(MacroSpots[i].get(_profile.Macros))), true));
                break;
            case Edit.Pro when _profile.Eq is ParametricLayer pl:
                for (int i = 0; i < pl.Bands.Count; i++)
                {
                    var b = pl.Bands[i];
                    hs.Add(new Handle(HKind.Band, i, new Point(X(b.Freq), Y(b.Kind.HasGain() ? b.GainDb : 0)), b.Enabled));
                }
                break;
            case Edit.Pro when _profile.Eq is GraphicLayer gl:
                for (int i = 0; i < gl.Points.Count; i++)
                    hs.Add(new Handle(HKind.Point, i, new Point(X(gl.Points[i].Freq), Y(gl.Points[i].GainDb)), true));
                break;
        }
        return hs;
    }

    private int? Hit(Point p)
    {
        var hs = Handles();
        bool columns = _edit == Edit.Pro && Points.Count > 10;
        double radius = columns ? 9 : 16;
        int? best = null;
        double bd = radius;
        for (int i = 0; i < hs.Count; i++)
        {
            double d = columns ? Math.Abs(hs[i].Pt.X - p.X) : (hs[i].Pt - p).Length;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    // ------------------------------------------------------------ animation

    private static void Spring(ref double[] x, ref double[] v, double[] target, double k, double c, double dt, bool snap)
    {
        if (x.Length != target.Length || snap)
        {
            x = (double[])target.Clone();
            v = new double[target.Length];
            return;
        }
        for (int i = 0; i < x.Length; i++)
        {
            double a = k * (target[i] - x[i]) - c * v[i];
            v[i] += a * dt;
            x[i] += v[i] * dt;
        }
    }

    private static void Spring(ref double x, ref double v, double target, double k, double c, double dt, bool snap)
    {
        if (snap) { x = target; v = 0; return; }
        double a = k * (target - x) - c * v;
        v += a * dt;
        x += v * dt;
    }

    private double TargetRange()
    {
        double peak = _curve?.Total.Select(Math.Abs).DefaultIfEmpty(0).Max() ?? 0;
        double bp = Bands.Select(b => Math.Abs(b.GainDb)).DefaultIfEmpty(0).Max();
        return Math.Min(30, Math.Max(12, Math.Ceiling(Math.Max(peak, bp) / 6) * 6));
    }

    private void Tick()
    {
        if (W <= 0 || H <= 0) return;
        SpectrumFeed.Shared.Update(_c);
        double rate = _c.State?.SampleRate ?? 0;
        if (_curve is null || _curveDirty || rate != _rate)
        {
            var cv = _c.CurveFor(_profile);
            if (cv is not null) { _curve = cv; _curveDirty = false; _rate = rate; }
        }
        Advance();
        UpdatePaths();
        _back.InvalidateVisual();
        _front.InvalidateVisual();
    }

    private void Advance()
    {
        var now = DateTime.UtcNow;
        double dt = _lastT == DateTime.MinValue ? 1.0 / 60 : Math.Min((now - _lastT).TotalSeconds, 1.0 / 20);
        _lastT = now;
        if (_curve is null) return;
        bool motion = Ui.Store.MotionAllowed;
        var hs = Handles();
        int? dragBand = _dragging is int d && d < hs.Count && hs[d].Kind == HKind.Band ? hs[d].Index : null;
        var shapeT = _curve.Total.Select(v => v - _curve.Preamp).ToArray();
        int? sel = _edit == Edit.Pro ? (Selected ?? dragBand) : null;
        int off = _profile.Correction.Count;
        var bandT = sel is int s && off + s < _curve.Bands.Count ? _curve.Bands[off + s].ToArray() : new double[_curve.Freqs.Count];
        if (_bandIndex != sel)
        {
            _band = new double[_curve.Freqs.Count]; // grows out of the zero line
            _bandV = new double[_curve.Freqs.Count];
            _bandIndex = sel;
        }
        var (k, c) = _dragging is not null ? (900.0, 60.0) : (170.0, 15.0);
        int steps = dt > 1.0 / 50 ? 2 : 1;
        dt /= steps;
        var scaleT = hs.Select((h, i) => i == _dragging ? 1.55 : (h.Kind == HKind.Band && h.Index == Selected) ? 1.3 : i == _hover ? 1.2 : 1.0).ToArray();
        var totalT = _curve.Total.ToArray();
        for (int i = 0; i < steps; i++)
        {
            Spring(ref _shape, ref _shapeV, shapeT, k, c, dt, !motion);
            Spring(ref _total, ref _totalV, totalT, k, c, dt, !motion);
            Spring(ref _band, ref _bandV, bandT, 260, 20, dt, !motion);
            Spring(ref _range, ref _rangeV, TargetRange(), 90, 19, dt, !motion);
            Spring(ref _hScale, ref _hScaleV, scaleT, 380, 14, dt, !motion);
        }
        _ripples.RemoveAll(r => (now - r.t0).TotalSeconds > 0.9);
    }

    private StreamGeometry Line(double[] vals)
    {
        var geo = new StreamGeometry();
        if (_curve is null || vals.Length != _curve.Freqs.Count || vals.Length == 0) return geo;
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(X(_curve.Freqs[0]), Y(vals[0])), false, false);
            var pts = new List<Point>(vals.Length);
            for (int i = 1; i < vals.Length; i++) pts.Add(new Point(X(_curve.Freqs[i]), Y(vals[i])));
            g.PolyLineTo(pts, true, true);
        }
        geo.Freeze();
        return geo;
    }

    private LinearGradientBrush Horizontal(IReadOnlyList<GradientStop> stops)
    {
        var b = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(Math.Max(W, 1), 0) };
        foreach (var s in stops) b.GradientStops.Add(s);
        return b;
    }

    private LinearGradientBrush CurveGradient(double opacity = 1)
    {
        var cs = Ui.T.CurveColors;
        return Horizontal(cs.Select((c, i) => new GradientStop(Theme.WithAlpha(c, opacity * c.A / 255.0), i / (double)(cs.Length - 1))).ToList());
    }

    private void UpdatePaths()
    {
        var t = Ui.T;
        double level = Ui.Store.Settings.Reactive ? SpectrumFeed.Shared.Level : 0;
        double lw = _compact ? 2 : 2.6;
        var shape = Line(_shape);
        var grad = CurveGradient();
        _stroke.Data = shape;
        _stroke.Stroke = grad;
        _stroke.StrokeThickness = lw;
        _glow.Data = shape;
        _glow.Stroke = grad;
        _glow.StrokeThickness = lw * (2.2 + 1.5 * level);
        _glow.Opacity = t.Glow * (0.55 + 0.45 * level);
        ((BlurEffect)_glow.Effect).Radius = (_compact ? 5 : 9) + 7 * level;
        if (Ui.Store.Settings.Shimmer && Ui.Store.MotionAllowed)
        {
            double p = (DateTime.UtcNow - _t0).TotalSeconds * 0.16 % 1.6 - 0.3;
            byte a = (byte)(t.Dark ? 217 : 178);
            _shimmer.Data = shape;
            _shimmer.StrokeThickness = lw * 0.9;
            _shimmer.Stroke = Horizontal(new[]
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), Math.Clamp(p - 0.09, 0, 1)),
                new GradientStop(Color.FromArgb(a, 255, 255, 255), Math.Clamp(p, 0, 1)),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), Math.Clamp(p + 0.09, 0, 1)),
            });
            _shimmer.Visibility = Visibility.Visible;
        }
        else _shimmer.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------ drawing

    private static readonly Typeface Face = new(Ui.Font, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    private static readonly Typeface Bold = new(Ui.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private FormattedText Txt(string s, double size, Brush b, Typeface? face = null) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face ?? Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawBack(DrawingContext dc, Size size)
    {
        double w = size.Width, h = size.Height;
        if (w <= 0 || h <= 0) return;
        var t = Ui.T;
        double level = Ui.Store.Settings.Reactive ? SpectrumFeed.Shared.Level : 0;
        var grid = new Pen(t.InkBrush(t.Dark ? 0.07 : 0.09), 1);
        var label = t.InkBrush(0.45);
        if (!_compact)
        {
            foreach (var f in new[] { 50.0, 100, 200, 500, 1000, 2000, 5000, 10000 })
            {
                double gx = X(f);
                dc.DrawLine(grid, new Point(gx, 0), new Point(gx, h));
                dc.DrawText(Txt(f >= 1000 ? $"{f / 1000}k" : $"{f}", 9, label), new Point(gx + 3, h - 15));
            }
            for (double d = -Math.Floor(_range / 6) * 6; d <= _range; d += 6)
            {
                if (d == 0) continue;
                dc.DrawLine(grid, new Point(0, Y(d)), new Point(w, Y(d)));
                dc.DrawText(Txt($"{d:+0;-0}", 9, label), new Point(5, Y(d) - 13));
            }
        }

        // Live spectrum: smoothed body + peak-hold line.
        var feed = SpectrumFeed.Shared;
        if (feed.Smooth.Length > 1)
        {
            int n = feed.Smooth.Length;
            double Py(float v) => h - Math.Clamp((v + 96) / 96.0, 0, 1) * h * (_compact ? 0.9 : 0.85);
            var body = new StreamGeometry();
            using (var g = body.Open())
            {
                g.BeginFigure(new Point(0, h), true, true);
                for (int i = 0; i < n; i++) g.LineTo(new Point(i / (double)(n - 1) * w, Py(feed.Smooth[i])), true, false);
                g.LineTo(new Point(w, h), true, false);
            }
            var sc = t.SpectrumColors;
            var fill = new LinearGradientBrush(Theme.WithAlpha(sc[0], 0.28 + 0.15 * level), Theme.WithAlpha(sc[^1], 0.04), 90);
            dc.DrawGeometry(fill, null, body);
            if (!_compact)
            {
                var peak = new StreamGeometry();
                using (var g = peak.Open())
                {
                    g.BeginFigure(new Point(0, Py(feed.Peaks[0])), false, false);
                    for (int i = 1; i < n; i++) g.LineTo(new Point(i / (double)(n - 1) * w, Py(feed.Peaks[i])), true, false);
                }
                dc.DrawGeometry(null, new Pen(Theme.Frozen(Theme.WithAlpha(sc[0], 0.35)), 1), peak);
            }
        }

        if (_curve is null || _shape.Length != _curve.Freqs.Count) return;
        double zeroY = Y(0);
        dc.DrawLine(new Pen(t.InkBrush(0.22), 1), new Point(0, zeroY), new Point(w, zeroY));

        // Selected band's own contribution, in its color.
        if (_bandIndex is int sel && sel < Bands.Count && _band.Length == _curve.Freqs.Count)
        {
            var col = t.CurveColor(X(Bands[sel].Freq) / w);
            dc.DrawGeometry(Theme.Frozen(Theme.WithAlpha(col, 0.22)), null, Area(_band, zeroY));
            dc.DrawGeometry(null, new Pen(Theme.Frozen(Theme.WithAlpha(col, 0.75)), 1.2) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) }, Line(_band));
        }

        // Area under the EQ shape.
        dc.PushOpacity(0.2 + 0.12 * level);
        dc.DrawGeometry(CurveGradient(), null, Area(_shape, zeroY));
        dc.Pop();

        if (!_compact)
            dc.DrawGeometry(null, new Pen(t.InkBrush(0.3), 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) }, Line(_total));
    }

    private StreamGeometry Area(double[] vals, double zeroY)
    {
        var geo = new StreamGeometry();
        if (_curve is null) return geo;
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(0, zeroY), true, true);
            for (int i = 0; i < vals.Length; i++) g.LineTo(new Point(X(_curve.Freqs[i]), Y(vals[i])), true, false);
            g.LineTo(new Point(W, zeroY), true, false);
        }
        return geo;
    }

    private void DrawFront(DrawingContext dc, Size size)
    {
        if (_edit == Edit.None || _curve is null) return;
        var t = Ui.T;
        double w = size.Width, h = size.Height;
        var now = DateTime.UtcNow;

        foreach (var r in _ripples)
        {
            double p = (now - r.t0).TotalSeconds / 0.9;
            for (int k = 0; k < 2; k++)
            {
                double q = p - k * 0.18;
                if (q <= 0) continue;
                double rad = 8 + 46 * (1 - Math.Pow(1 - q, 3));
                dc.DrawEllipse(null, new Pen(Theme.Frozen(Theme.WithAlpha(r.color, 0.6 * (1 - q))), 2), r.pt, rad, rad);
            }
        }

        var hs = Handles();
        int? active = _dragging;
        if (active is null && Selected is int s)
        {
            int fi = hs.FindIndex(x => x.Kind == HKind.Band && x.Index == s);
            if (fi >= 0) active = fi;
        }

        if (_edit == Edit.Pro && _profile.Eq is GraphicLayer)
        {
            double zeroY = Y(0);
            for (int i = 0; i < hs.Count; i++)
            {
                var col = t.CurveColor(hs[i].Pt.X / w);
                dc.DrawLine(new Pen(Theme.Frozen(Theme.WithAlpha(col, i == active || i == _hover ? 0.9 : 0.35)), Points.Count > 10 ? 2 : 3),
                            new Point(hs[i].Pt.X, zeroY), hs[i].Pt);
            }
        }

        for (int i = 0; i < hs.Count; i++)
        {
            var hd = hs[i];
            var col = hd.Enabled ? t.CurveColor(hd.Pt.X / w) : Colors.Gray;
            double sc = i < _hScale.Length ? _hScale[i] : 1;
            double baseR = hd.Kind switch { HKind.Point => Points.Count > 10 ? 3.5 : 5, HKind.Macro => 7.5, _ => 6 };
            double r = baseR * sc;
            if (i == active || i == _hover)
            {
                var halo = new RadialGradientBrush(Theme.WithAlpha(col, i == _dragging ? 0.75 : 0.45), Theme.WithAlpha(col, 0));
                dc.DrawEllipse(halo, null, hd.Pt, r * 2.4, r * 2.4);
            }
            dc.DrawEllipse(Theme.Frozen(col), new Pen(Brushes.White, 1.6), hd.Pt, r, r);
            if (hd.Kind == HKind.Band)
            {
                var num = Txt($"{hd.Index + 1}", 8 * Math.Min(sc, 1.3), Brushes.White, Bold);
                dc.DrawText(num, new Point(hd.Pt.X - num.Width / 2, hd.Pt.Y - num.Height / 2));
            }
            else if (hd.Kind == HKind.Macro)
            {
                var name = Txt(L(MacroSpots[hd.Index].name), 10, t.InkBrush(0.8), Bold);
                bool below = hd.Pt.Y < h - 28;
                dc.DrawText(name, new Point(hd.Pt.X - name.Width / 2, below ? hd.Pt.Y + r + 4 : hd.Pt.Y - r - 4 - name.Height));
            }
        }

        // Value bubble.
        int? bubble = active ?? (_edit == Edit.Easy || Points.Count > 0 ? _hover : null);
        if (bubble is int bi && bi < hs.Count)
        {
            var hd = hs[bi];
            string txt = "";
            switch (hd.Kind)
            {
                case HKind.Band when hd.Index < Bands.Count:
                    var b = Bands[hd.Index];
                    txt = Ui.Hz(b.Freq) + (b.Kind.HasGain() ? $"  {b.GainDb:+0.0;-0.0;0.0} dB" : "") + $"  Q {b.Q:0.00}";
                    if (_dragging == bi && _dragStartProfile?.Eq is ParametricLayer spl && hd.Index < spl.Bands.Count)
                    {
                        var sb = spl.Bands[hd.Index];
                        txt += $"   Δ {(b.Freq / sb.Freq - 1) * 100:+0;-0;0}%";
                        if (b.Kind.HasGain()) txt += $" {b.GainDb - sb.GainDb:+0.0;-0.0;0.0} dB";
                        if (_qMode) txt += $"  ×{b.Q / Math.Max(sb.Q, 0.01):0.00} Q";
                    }
                    break;
                case HKind.Point when hd.Index < Points.Count:
                    txt = Ui.Hz(Points[hd.Index].Freq) + $"  {Points[hd.Index].GainDb:+0.0;-0.0;0.0} dB";
                    break;
                case HKind.Macro:
                    txt = L(MacroSpots[hd.Index].name) + $"  {MacroSpots[hd.Index].get(_profile.Macros):+0.0;-0.0;0.0} dB";
                    break;
            }
            if (txt.Length > 0)
            {
                var ft = Txt(txt, 11, Brushes.White, Bold);
                double ay = hd.Pt.Y < 40 ? hd.Pt.Y + 22 : hd.Pt.Y - 22 - ft.Height;
                double ax = Math.Clamp(hd.Pt.X - ft.Width / 2, 10, w - ft.Width - 10);
                var rect = new Rect(ax - 8, ay - 4, ft.Width + 16, ft.Height + 8);
                var col = t.CurveColor(hd.Pt.X / w);
                dc.DrawRoundedRectangle(new RadialGradientBrush(Theme.WithAlpha(col, 0.35), Theme.WithAlpha(col, 0)), null, new Rect(rect.X - 8, rect.Y - 8, rect.Width + 16, rect.Height + 16), 18, 18);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), new Pen(Theme.Frozen(Theme.WithAlpha(col, 0.9)), 1), rect, rect.Height / 2, rect.Height / 2);
                dc.DrawText(ft, new Point(ax, ay));
            }
        }
    }

    // ------------------------------------------------------------ interaction

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_edit == Edit.None) return;
        Focus();
        var p = e.GetPosition(this);
        var hit = Hit(p);
        if (hit is not int i)
        {
            if (Selected is not null) { Selected = null; SelectionChanged?.Invoke(null); }
            // Double-click on empty space adds a band (Pro, parametric).
            if (_edit == Edit.Pro && _profile.Eq is ParametricLayer && e.ClickCount == 2) AddBand(p);
            return;
        }
        var hs = Handles();
        _dragging = i;
        _moved = false;
        _dragStartPt = p;
        _dragStartHandle = hs[i].Pt;
        _dragStartProfile = Json.Clone(_profile);
        _qMode = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (hs[i].Kind == HKind.Band) { Selected = hs[i].Index; SelectionChanged?.Invoke(Selected); }
        if (Ui.Store.MotionAllowed) _ripples.Add((hs[i].Pt, DateTime.UtcNow, Ui.T.CurveColor(hs[i].Pt.X / W)));
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragging is not int i)
        {
            if (_edit != Edit.None) _hover = Hit(p);
            return;
        }
        var delta = p - _dragStartPt;
        // A plain click selects; nothing changes until the mouse really moves.
        if (!_moved && delta.Length <= 2) return;
        _moved = true;
        var hs = Handles();
        if (i >= hs.Count || _dragStartProfile is null) return;
        var target = _dragStartHandle + delta; // relative: no jump to the cursor
        double db = Math.Round(Math.Clamp(Db(target.Y), -24, 24) * 10) / 10;
        switch (hs[i].Kind)
        {
            case HKind.Band when _profile.Eq is ParametricLayer pl && _dragStartProfile.Eq is ParametricLayer spl:
                var b = pl.Bands[hs[i].Index];
                var sb = spl.Bands[hs[i].Index];
                if (_qMode)
                {
                    b.Q = Math.Round(Math.Clamp(sb.Q * Math.Pow(2, -delta.Y / 70), 0.1, 30) * 100) / 100;
                }
                else
                {
                    double f = F(target.X);
                    double mag = Math.Pow(10, Math.Floor(Math.Log10(f)) - 2);
                    b.Freq = Math.Clamp(Math.Round(f / mag) * mag, 20, 20000);
                    if (b.Kind.HasGain()) b.GainDb = db;
                }
                break;
            case HKind.Point when _profile.Eq is GraphicLayer gl:
                gl.Points[hs[i].Index].GainDb = Math.Round(Math.Clamp(db, -12, 12) * 2) / 2;
                break;
            case HKind.Macro:
                MacroSpots[hs[i].Index].set(_profile.Macros, Math.Round(Math.Clamp(db, -12, 12) * 2) / 2);
                break;
        }
        _curveDirty = true;
        ProfileEdited?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragging is null) return;
        _dragging = null;
        _dragStartProfile = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { if (_dragging is null) _hover = null; }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        int? idx = Selected ?? (_hover is int hv && hv < Handles().Count && Handles()[hv].Kind == HKind.Band ? Handles()[hv].Index : null);
        if (_edit != Edit.Pro || idx is not int i || i >= Bands.Count) return;
        var b = Bands[i];
        b.Q = Math.Round(Math.Clamp(b.Q * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.1, 30) * 100) / 100;
        _curveDirty = true;
        ProfileEdited?.Invoke();
        e.Handled = true;
    }

    private void AddBand(Point p)
    {
        if (_profile.Eq is not ParametricLayer pl || pl.Bands.Count >= 64) return;
        pl.Bands.Add(new Band { Kind = FilterKind.Peak, Freq = Math.Round(F(p.X) * 10) / 10, GainDb = Math.Round(Math.Clamp(Db(p.Y), -24, 24) * 10) / 10, Q = 1 });
        Selected = pl.Bands.Count - 1;
        SelectionChanged?.Invoke(Selected);
        if (Ui.Store.MotionAllowed) _ripples.Add((p, DateTime.UtcNow, Ui.T.CurveColor(p.X / W)));
        _curveDirty = true;
        ProfileEdited?.Invoke();
    }
}
