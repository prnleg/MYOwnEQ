using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using static MyOwnEQ.UI.Loc;

namespace MyOwnEQ.UI;

/// <summary>Pro mode: parametric bands or graphic sliders, output, correction (ProPanel.swift).</summary>
// parity: pro-panel
public sealed class ProPanel : StackPanel
{
    private enum Mode { Parametric, G10, G31 }

    private readonly AppController _c;
    private readonly Profile _p;
    private readonly Action<bool> _edited;
    private readonly Action<int?> _select;
    private readonly List<Border> _rows = new();
    private int? _selected;

    public ProPanel(AppController c, Profile p, int? selected, Action<bool> edited, Action<int?> select)
    {
        _c = c; _p = p; _edited = edited; _select = select; _selected = selected;
        var t = Ui.T;

        var mode = new PillPicker<Mode>(new[] { (Mode.Parametric, L("Parametric")), (Mode.G10, L("Graphic 10")), (Mode.G31, L("Graphic 31")) }, CurrentMode);
        mode.Changed += SwitchMode;
        UIElement top = mode;
        if (p.Eq is ParametricLayer)
            top = Ui.Spread(mode, Ui.Button(L("Add Band"), AddBand, prominent: true, glyph: Ui.GAdd));
        Children.Add(top);

        var body = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (p.Eq is ParametricLayer pl)
        {
            for (int i = 0; i < pl.Bands.Count; i++) body.Children.Add(BandRow(pl.Bands, i));
            if (pl.Bands.Count == 0) body.Children.Add(Ui.Secondary(L("No bands yet. Click Add Band or double-click the curve."), 13));
        }
        else if (p.Eq is GraphicLayer gl) body.Children.Add(Ui.Card(null, null, GraphicGrid(gl)));
        Children.Add(body);

        // Output
        var output = Ui.VStack(8);
        output.Children.Add(ToggleSwitch.Row(L("Auto preamp"), p.Preamp.IsAuto, on => { p.Preamp = on ? Preamp.Auto() : Preamp.Manual(0); edited(true); }));
        if (!p.Preamp.IsAuto)
            Ui.Add(output, LabeledSlider(L("Preamp"), -24, 6, p.Preamp.Db ?? 0, true, v => Ui.Db(v), v => { p.Preamp = Preamp.Manual(Math.Round(v, 1)); edited(false); }), 8, false);
        Ui.Add(output, LabeledSlider(L("Balance"), -1, 1, p.Balance, true,
            v => Math.Abs(v) < 0.03 ? "C" : v < 0 ? L("L") + $" {-v * 100:0}%" : L("R") + $" {v * 100:0}%",
            v => { p.Balance = Math.Abs(v) < 0.03 ? 0 : v; edited(false); }), 8, false);
        Ui.Add(output, LabeledSlider(L("Loudness"), 0, 1, p.Loudness, false, v => v == 0 ? L("off") : $"{v * 100:0}%", v => { p.Loudness = v; edited(false); }), 8, false);
        Ui.Add(output, Ui.Secondary(L("Windows engine (Equalizer APO) has no limiter: auto preamp keeps it from clipping."), 11.5), 8, false);
        var outCard = Ui.Card(L("Output"), Ui.GVolume, output);
        outCard.Margin = new Thickness(0, 14, 0, 0);
        Children.Add(outCard);

        if (p.Correction.Count > 0)
        {
            var lines = Ui.VStack(2, p.Correction.Select(b =>
            {
                var tb = Ui.Text($"{b.Kind.Label(),-10} {Ui.Hz(b.Freq),10}  {Ui.Db(b.GainDb)}  Q {b.Q:0.00}", 11.5, opacity: b.Enabled ? 1 : 0.5);
                tb.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                return (UIElement)tb;
            }).ToArray());
            var corr = Ui.Card(L("Correction · %@ · before your bands", p.CorrectionSource?.Name ?? L("imported")), Ui.GCheck, lines);
            corr.Margin = new Thickness(0, 14, 0, 0);
            Children.Add(corr);
        }
    }

    private Mode CurrentMode => _p.Eq is GraphicLayer g ? (g.Points.Count > 10 ? Mode.G31 : Mode.G10) : Mode.Parametric;

    public void Select(int? i)
    {
        _selected = i;
        for (int k = 0; k < _rows.Count; k++) StyleRow(_rows[k], k == i);
    }

    private static void StyleRow(Border row, bool on)
    {
        var t = Ui.T;
        row.BorderBrush = on ? t.DiagonalBrush() : new LinearGradientBrush(Theme.WithAlpha(t.Ink, 0.16), Theme.WithAlpha(t.Ink, 0.04), 45);
        row.BorderThickness = new Thickness(on ? 1.5 : 1);
    }

    private static DockPanel LabeledSlider(string label, double min, double max, double value, bool bipolar, Func<double, string> fmt, Action<double> changed, double step = 0, double labelWidth = 76)
    {
        var l = Ui.Text(label, 13);
        l.Width = labelWidth;
        var v = Ui.Text(fmt(value), 12);
        v.Width = 64;
        v.TextAlignment = TextAlignment.Right;
        var s = new GlowSlider(min, max, value) { Bipolar = bipolar, Step = step };
        s.ValueChanged += x => { v.Text = fmt(x); changed(x); };
        var row = new DockPanel();
        DockPanel.SetDock(l, Dock.Left);
        DockPanel.SetDock(v, Dock.Right);
        row.Children.Add(l);
        row.Children.Add(v);
        row.Children.Add(s);
        return row;
    }

    private Border BandRow(List<Band> bands, int i)
    {
        var t = Ui.T;
        var b = bands[i];
        var col = t.CurveColor(Math.Log(b.Freq / 20) / Math.Log(1000));
        var num = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            Background = Theme.Frozen(b.Enabled ? col : Colors.Gray),
            Child = new TextBlock { Text = $"{i + 1}", FontSize = 11, FontWeight = FontWeights.Heavy, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var en = new ToggleSwitch(b.Enabled) { Width = 34, Height = 18, Margin = new Thickness(10, 0, 10, 0) };
        en.Toggled += on => { b.Enabled = on; _edited(true); };
        var kind = new ComboBox { Width = 116, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (FilterKind k in Enum.GetValues<FilterKind>()) kind.Items.Add(new ComboBoxItem { Content = L(k.Label()), Tag = k, IsSelected = k == b.Kind });
        kind.SelectionChanged += (_, _) => { if (kind.SelectedItem is ComboBoxItem { Tag: FilterKind k }) { b.Kind = k; _edited(true); } };
        var del = Ui.IconButton(Ui.GDelete, () => { bands.RemoveAt(i); _select(null); _edited(true); }, L("Delete"), t.InkBrush(0.6));

        var grid = new Grid();
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1.3, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        void Put(UIElement e, int c) { Grid.SetColumn(e, c); grid.Children.Add(e); }
        Put(num, 0);
        Put(en, 1);
        Put(kind, 2);
        Put(Mini(Ui.Hz(b.Freq), Math.Log10(20), Math.Log10(20000), Math.Log10(b.Freq), false, 0,
                 v => { b.Freq = Math.Round(Math.Pow(10, v) * 10) / 10; return Ui.Hz(b.Freq); }), 3);
        Put(Mini(b.Kind.HasGain() ? Ui.Db(b.GainDb) : "—", -24, 24, b.GainDb, true, 0.1,
                 v => { b.GainDb = Math.Round(v * 10) / 10; return Ui.Db(b.GainDb); }, enabled: b.Kind.HasGain()), 4);
        Put(Mini($"Q {b.Q:0.00}", Math.Log10(0.1), Math.Log10(30), Math.Log10(b.Q), false, 0,
                 v => { b.Q = Math.Round(Math.Pow(10, v) * 100) / 100; return $"Q {b.Q:0.00}"; }), 5);
        Put(del, 6);

        var row = Ui.Glass(grid, radius: 11, padding: 8, shadow: false);
        row.Margin = new Thickness(0, 0, 0, 6);
        row.Cursor = Cursors.Hand;
        StyleRow(row, i == _selected);
        row.MouseLeftButtonDown += (_, _) => { _select(i); Select(i); };
        _rows.Add(row);
        return row;
    }

    /// <summary>Value label over a compact glow slider.</summary>
    private FrameworkElement Mini(string text, double min, double max, double value, bool bipolar, double step, Func<double, string> set, bool enabled = true)
    {
        var label = Ui.Text(text, 11.5, FontWeights.Medium);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var s = new GlowSlider(min, max, value) { Bipolar = bipolar, Step = step, IsEnabled = enabled, Opacity = enabled ? 1 : 0.4 };
        s.ValueChanged += v => { label.Text = set(v); _edited(false); };
        var col = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        col.Children.Add(label);
        col.Children.Add(s);
        return col;
    }

    private UIElement GraphicGrid(GraphicLayer gl)
    {
        int cols = gl.Points.Count > 10 ? 3 : 2;
        var grid = new UniformGrid { Columns = cols };
        foreach (var pt in gl.Points)
        {
            var p = pt;
            string F() => p.Freq >= 1000 ? $"{p.Freq / 1000:0.#}k" : $"{p.Freq:0.#}";
            var row = LabeledSlider(F(), -12, 12, p.GainDb, true, v => $"{v:+0.0;-0.0;0.0}", v => { p.GainDb = v; _edited(false); }, 0.5, 44);
            row.Margin = new Thickness(0, 2, 14, 2);
            grid.Children.Add(row);
        }
        return grid;
    }

    private void AddBand()
    {
        if (_p.Eq is not ParametricLayer pl || _c.SuggestBand(_p.Eq) is not { } band) return;
        pl.Bands.Add(band);
        _select(pl.Bands.Count - 1);
        _edited(true);
    }

    /// <summary>Switch layout keeping the sound. The core samples the curve and keeps
    /// the way back to the exact original bands (same logic on every platform).</summary>
    // parity: eq-layout-convert
    private void SwitchMode(Mode m)
    {
        if (m == CurrentMode) return;
        string to = m switch { Mode.Parametric => "parametric", Mode.G10 => "graphic10", _ => "graphic31" };
        if (_c.ConvertEq(_p.Eq, to, _p.Id) is { } eq) _p.Eq = eq;
        _select(null);
        _edited(true);
    }
}
