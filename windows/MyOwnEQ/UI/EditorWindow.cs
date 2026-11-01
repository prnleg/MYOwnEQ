using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MyOwnEQ.Platform;

namespace MyOwnEQ.UI;

public sealed class EditorWindow : Window
{
    private readonly AppController _c;
    private readonly ListBox _list = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly TabControl _tabs = new();
    private readonly ContentControl _banner = new();
    private readonly ContentControl _easy = new(), _pro = new(), _device = new(), _sync = new();
    private string? _selectedId;
    private Profile? _draft;
    private CurveControl? _curve;
    private int? _selectedBand;
    private bool _suppress;
    private bool _audition = true;

    public EditorWindow(AppController c)
    {
        _c = c;
        Title = "MyOwnEQ";
        Width = 1150;
        Height = 760;
        MinWidth = 900;
        MinHeight = 600;

        var root = new Grid { Margin = new Thickness(12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Sidebar
        var side = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var sideButtons = Ui.Row(Ui.Button("New", NewProfile), Ui.Button("Duplicate", Duplicate), Ui.Button("Delete", DeleteSelected));
        DockPanel.SetDock(sideButtons, Dock.Bottom);
        side.Children.Add(sideButtons);
        var importMenu = Ui.Row(Ui.Button("Import AutoEQ…", () => Import(true)), Ui.Button("Import APO…", () => Import(false)));
        DockPanel.SetDock(importMenu, Dock.Bottom);
        side.Children.Add(importMenu);
        side.Children.Add(_list);
        _list.SelectionChanged += (_, _) =>
        {
            if (_suppress || _list.SelectedItem is not ListBoxItem { Tag: string id }) return;
            SwitchProfile(id);
        };
        Grid.SetColumn(side, 0);
        root.Children.Add(side);

        // Main
        var main = new DockPanel();
        DockPanel.SetDock(_banner, Dock.Top);
        main.Children.Add(_banner);
        _tabs.Items.Add(new TabItem { Header = "Easy", Content = Scroll(_easy) });
        _tabs.Items.Add(new TabItem { Header = "Pro", Content = Scroll(_pro) });
        _tabs.Items.Add(new TabItem { Header = "Device & Rules", Content = Scroll(_device) });
        _tabs.Items.Add(new TabItem { Header = "Sync", Content = Scroll(_sync) });
        _tabs.SelectionChanged += (_, e) => { if (e.Source == _tabs) BuildTabs(); };
        main.Children.Add(_tabs);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);
        Content = root;

        _c.Changed += OnChanged;
        Closed += (_, _) =>
        {
            _c.Changed -= OnChanged;
            _c.FlushSave();
            _c.Preview(null);
        };
        _selectedId = _c.ActiveProfile?.Id ?? _c.State?.Library.Profiles.FirstOrDefault()?.Id;
        Rebuild();
    }

    private static ScrollViewer Scroll(UIElement e) => new() { Content = e, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4, 10, 8, 4) };

    private void OnChanged()
    {
        // Don't rebuild the profile editors under the user's mouse; just refresh lists/status.
        BuildList();
        BuildBanner();
        int tab = _tabs.SelectedIndex;
        if (tab >= 2) BuildTabs();
    }

    private void Rebuild()
    {
        BuildList();
        BuildBanner();
        LoadDraft();
        BuildTabs();
    }

    // ---------------------------------------------------------------- sidebar

    private void BuildList()
    {
        _suppress = true;
        _list.Items.Clear();
        var s = _c.State;
        foreach (var p in s?.Library.Profiles ?? new())
        {
            string marks = (p.Id == _c.ActiveProfile?.Id ? "  🔊" : "") + (s!.CurrentGroup?.Favorites.Contains(p.Id) == true ? "  ★" : "");
            _list.Items.Add(new ListBoxItem { Content = p.Name + marks, Tag = p.Id, IsSelected = p.Id == _selectedId });
        }
        _suppress = false;
    }

    private void SwitchProfile(string id)
    {
        _c.FlushSave();
        _selectedId = id;
        _selectedBand = null;
        LoadDraft();
        BuildTabs();
    }

    private void LoadDraft()
    {
        var p = _c.State?.FindProfile(_selectedId);
        _draft = p is null ? null : Json.Clone(p);
        if (_draft is not null && _audition) _c.Preview(_draft);
    }

    private void NewProfile()
    {
        var p = new Profile { Name = "New Profile" };
        _c.Save(p);
        SwitchProfile(p.Id);
    }

    private void Duplicate()
    {
        if (_draft is null) return;
        var d = Json.Clone(_draft);
        d.Id = Profile.NewId();
        d.Name += " copy";
        d.Tags.Remove("builtin");
        _c.Save(d);
        SwitchProfile(d.Id);
    }

    private void DeleteSelected()
    {
        if (_draft is null) return;
        if (MessageBox.Show(this, $"Delete \"{_draft.Name}\"? This syncs to your other machines.", "MyOwnEQ", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _c.Delete(_draft.Id);
        _selectedId = _c.ActiveProfile?.Id;
        Rebuild();
    }

    private void Import(bool asCorrection)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files|*.*",
            Multiselect = true,
            Title = asCorrection ? "AutoEQ ParametricEQ.txt" : "Equalizer APO / Peace config",
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) _c.ImportFile(f, asCorrection);
    }

    // ---------------------------------------------------------------- APO banner

    private void BuildBanner()
    {
        var apo = _c.Apo;
        if (apo.Status == ApoStatus.Ready && _c.Message is null) { _banner.Content = null; return; }
        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        switch (apo.Status)
        {
            case ApoStatus.NotInstalled:
                box.Children.Add(Ui.Text("Equalizer APO is not installed. MyOwnEQ uses it as the Windows audio engine (free, open source).", 13, FontWeights.SemiBold));
                box.Children.Add(Ui.Text("1. Install it  2. In its Configurator, tick the outputs you use (speakers, headphones…)  3. Reboot if it asks  4. Click Check again.", 12, color: Ui.Secondary));
                box.Children.Add(Ui.Row(Ui.Button("Download Equalizer APO", ApoBackend.OpenDownloadPage, primary: true), Ui.Button("Check again", _c.ApoRedetect)));
                break;
            case ApoStatus.NoWriteAccess:
                box.Children.Add(Ui.Text($"Can't write to {apo.ConfigPath}. Allow it once (Windows will ask for admin).", 13, FontWeights.SemiBold));
                box.Children.Add(Ui.Row(Ui.Button("Allow", _c.ApoFixPermissions, primary: true), Ui.Button("Check again", _c.ApoRedetect)));
                break;
            case ApoStatus.NeedsSetup:
                box.Children.Add(Ui.Text("Equalizer APO found. Let MyOwnEQ manage it?", 13, FontWeights.SemiBold));
                box.Children.Add(Ui.Text($"Your current config.txt is backed up as {ApoBackend.BackupFile}. \"Take over\" disables other EQ (e.g. Peace) so you don't get double EQ; \"Keep mine\" adds MyOwnEQ on top.", 12, color: Ui.Secondary));
                box.Children.Add(Ui.Row(Ui.Button("Take over", () => _c.ApoSetup(true), primary: true), Ui.Button("Keep mine + add MyOwnEQ", () => _c.ApoSetup(false)),
                                         Ui.Button("Device selector…", apo.OpenConfigurator)));
                break;
        }
        if (_c.Message is { } m) box.Children.Add(Ui.Text(m, 12, color: Ui.Secondary));
        _banner.Content = new Border
        {
            Child = box,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(30, 255, 165, 0)),
        };
    }

    // ---------------------------------------------------------------- tabs

    private void BuildTabs()
    {
        switch (_tabs.SelectedIndex)
        {
            case 0: _easy.Content = BuildEasy(); break;
            case 1: _pro.Content = BuildPro(); break;
            case 2: _device.Content = BuildDevice(); break;
            case 3: _sync.Content = BuildSync(); break;
        }
    }

    /// <summary>Draft edited: audition + debounced save + redraw curve.</summary>
    private void Edited()
    {
        if (_draft is null) return;
        if (_audition) _c.Preview(_draft);
        _c.SaveDebounced(Json.Clone(_draft));
        _curve?.Refresh();
    }

    private UIElement Header()
    {
        var d = _draft!;
        var name = new TextBox { Text = d.Name, FontSize = 20, MinWidth = 260, Margin = new Thickness(0, 0, 12, 0) };
        name.TextChanged += (_, _) => { d.Name = name.Text; _c.SaveDebounced(Json.Clone(d)); };
        var audition = new CheckBox { Content = "Audition", IsChecked = _audition, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                                      ToolTip = "Hear this profile while editing, even if it isn't the active one" };
        audition.Click += (_, _) => { _audition = audition.IsChecked == true; _c.Preview(_audition ? _draft : null); };
        var use = Ui.Button("Use now", () => _c.Select(d.Id));
        use.IsEnabled = _c.ActiveProfile?.Id != d.Id;
        return Ui.Row(name, audition, use);
    }

    private UIElement CurveFor(bool interactive)
    {
        _curve = new CurveControl(_c, _draft!) { Interactive = interactive, Height = 280, Margin = new Thickness(0, 8, 0, 12), Selected = _selectedBand };
        _curve.ProfileEdited += () => { if (_draft is not null) { _c.Preview(_draft); _c.SaveDebounced(Json.Clone(_draft)); } };
        _curve.SelectionChanged += i => { _selectedBand = i; };
        return _curve;
    }

    private UIElement BuildEasy()
    {
        if (_draft is null) return Ui.Text("Pick a profile");
        var d = _draft;
        var tone = Ui.Column(2,
            Ui.Slider("Bass", -12, 12, d.Macros.Bass, Ui.Db, v => { d.Macros.Bass = v; Edited(); }, step: 0.5),
            Ui.Slider("Mids", -12, 12, d.Macros.Mids, Ui.Db, v => { d.Macros.Mids = v; Edited(); }, step: 0.5),
            Ui.Slider("Clarity", -12, 12, d.Macros.Clarity, Ui.Db, v => { d.Macros.Clarity = v; Edited(); }, step: 0.5),
            Ui.Slider("Treble", -12, 12, d.Macros.Treble, Ui.Db, v => { d.Macros.Treble = v; Edited(); }, step: 0.5));

        UIElement correction = d.CorrectionSource is { } src
            ? Ui.Row(Ui.Text($"✔ {src.Provider}: {src.Name} · {d.Correction.Count} filters"),
                     Ui.Button("Remove", () => { d.Correction.Clear(); d.CorrectionSource = null; Edited(); BuildTabs(); }))
            : Ui.Row(Ui.Text("None. Import an AutoEQ ParametricEQ.txt to correct your headphones.", color: Ui.Secondary),
                     Ui.Button("Import AutoEQ…", () => Import(true)));

        var limiter = new CheckBox { Content = "Clipping protection (auto preamp)", IsChecked = d.Limiter };
        limiter.Click += (_, _) => { d.Limiter = limiter.IsChecked == true; Edited(); };
        var comfort = Ui.Column(4,
            Ui.Slider("Loudness", 0, 1, d.Loudness, v => v == 0 ? "off" : $"{v * 100:0}%", v => { d.Loudness = v; Edited(); }),
            limiter);

        return Ui.Column(8, Header(), CurveFor(false), Ui.Group("Tone", tone), Ui.Group("Headphone correction", correction), Ui.Group("Comfort", comfort));
    }

    private UIElement BuildPro()
    {
        if (_draft is null) return Ui.Text("Pick a profile");
        var d = _draft;
        var panel = Ui.Column(8, Header(), CurveFor(true));

        var mode = new ComboBox { Width = 180 };
        mode.Items.Add("Parametric");
        mode.Items.Add("Graphic 10");
        mode.Items.Add("Graphic 31");
        mode.SelectedIndex = d.Eq is GraphicLayer g0 ? (g0.Points.Count > 10 ? 2 : 1) : 0;
        mode.SelectionChanged += (_, _) => { SwitchMode(mode.SelectedIndex); BuildTabs(); };
        var modeRow = Ui.Row(Ui.Text("EQ type  "), mode);
        if (d.Eq is ParametricLayer) modeRow.Children.Add(Ui.Button("+ Add band", AddBand));
        panel.Children.Add(modeRow);

        if (d.Eq is ParametricLayer pl)
        {
            if (pl.Bands.Count == 0) panel.Children.Add(Ui.Text("No bands yet. Add one, then drag the dots on the curve (mouse wheel = Q).", color: Ui.Secondary));
            for (int i = 0; i < pl.Bands.Count; i++) panel.Children.Add(BandRow(pl.Bands, i));
        }
        else if (d.Eq is GraphicLayer gl)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var pt in gl.Points)
            {
                var val = Ui.Text($"{pt.GainDb:+0.0;-0.0;0}", 10);
                var s = new Slider { Orientation = Orientation.Vertical, Minimum = -12, Maximum = 12, Value = pt.GainDb, Height = 160,
                                     TickFrequency = 0.5, IsSnapToTickEnabled = true, HorizontalAlignment = HorizontalAlignment.Center };
                var p = pt;
                s.ValueChanged += (_, e) => { p.GainDb = e.NewValue; val.Text = $"{e.NewValue:+0.0;-0.0;0}"; Edited(); };
                s.MouseDoubleClick += (_, _) => s.Value = 0;
                var label = Ui.Text(p.Freq >= 1000 ? $"{p.Freq / 1000:0.#}k" : $"{p.Freq:0.#}", 10);
                var col = Ui.Column(2, val, s, label);
                col.Margin = new Thickness(0, 0, gl.Points.Count > 10 ? 2 : 10, 0);
                foreach (var ch in col.Children.OfType<FrameworkElement>()) ch.HorizontalAlignment = HorizontalAlignment.Center;
                row.Children.Add(col);
            }
            panel.Children.Add(new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        }

        // Output
        var auto = new CheckBox { Content = "Auto preamp", IsChecked = d.Preamp.IsAuto };
        auto.Click += (_, _) => { d.Preamp = auto.IsChecked == true ? Preamp.Auto() : Preamp.Manual(0); Edited(); BuildTabs(); };
        var output = Ui.Column(4, auto);
        if (!d.Preamp.IsAuto)
            output.Children.Add(Ui.Slider("Preamp", -24, 6, d.Preamp.Db ?? 0, Ui.Db, v => { d.Preamp = Preamp.Manual(Math.Round(v, 1)); Edited(); }));
        output.Children.Add(Ui.Slider("Balance", -1, 1, d.Balance, v => Math.Abs(v) < 0.03 ? "center" : v < 0 ? $"L {-v * 100:0}%" : $"R {v * 100:0}%",
                                      v => { d.Balance = Math.Abs(v) < 0.03 ? 0 : v; Edited(); }));
        output.Children.Add(Ui.Slider("Loudness", 0, 1, d.Loudness, v => v == 0 ? "off" : $"{v * 100:0}%", v => { d.Loudness = v; Edited(); }));
        output.Children.Add(Ui.Text("Windows engine (Equalizer APO) has no limiter; auto preamp prevents clipping.", 11, color: Ui.Secondary));
        panel.Children.Add(Ui.Group("Output", output));

        if (d.Correction.Count > 0)
        {
            var lines = Ui.Column(0, d.Correction.Select(b => (UIElement)Ui.Text($"{b.Kind.Label(),-10} {Ui.Hz(b.Freq),10}  {Ui.Db(b.GainDb)}  Q {b.Q:0.00}", 11)).ToArray());
            panel.Children.Add(Ui.Group($"Correction ({d.CorrectionSource?.Name ?? "imported"}), applied before your bands", lines));
        }
        return panel;
    }

    private UIElement BandRow(List<Band> bands, int i)
    {
        var b = bands[i];
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = i == _selectedBand ? new SolidColorBrush(Color.FromArgb(30, 255, 140, 0)) : Brushes.Transparent };
        double[] widths = { 26, 34, 120, 80, 1, 70, 1, 60, 1, 40 };
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        void Add(UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }

        Add(Ui.Text($"{i + 1}", 12, FontWeights.Bold), 0);
        var en = new CheckBox { IsChecked = b.Enabled, VerticalAlignment = VerticalAlignment.Center };
        en.Click += (_, _) => { b.Enabled = en.IsChecked == true; Edited(); };
        Add(en, 1);
        var kind = new ComboBox { Margin = new Thickness(0, 0, 8, 0) };
        foreach (FilterKind k in Enum.GetValues<FilterKind>()) kind.Items.Add(new ComboBoxItem { Content = k.Label(), Tag = k, IsSelected = k == b.Kind });
        kind.SelectionChanged += (_, _) => { if (kind.SelectedItem is ComboBoxItem { Tag: FilterKind k }) { b.Kind = k; Edited(); BuildTabs(); } };
        Add(kind, 2);

        var fText = Ui.Text(Ui.Hz(b.Freq), 12);
        Add(fText, 3);
        var fs = new Slider { Minimum = Math.Log10(20), Maximum = Math.Log10(20000), Value = Math.Log10(b.Freq), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        fs.ValueChanged += (_, e) => { b.Freq = Math.Round(Math.Pow(10, e.NewValue) * 10) / 10; fText.Text = Ui.Hz(b.Freq); Edited(); };
        Add(fs, 4);

        var gText = Ui.Text(b.Kind.HasGain() ? Ui.Db(b.GainDb) : "—", 12);
        Add(gText, 5);
        var gs = new Slider { Minimum = -24, Maximum = 24, Value = b.GainDb, IsEnabled = b.Kind.HasGain(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        gs.ValueChanged += (_, e) => { b.GainDb = Math.Round(e.NewValue * 10) / 10; gText.Text = Ui.Db(b.GainDb); Edited(); };
        Add(gs, 6);

        var qText = Ui.Text($"Q {b.Q:0.00}", 12);
        Add(qText, 7);
        var qs = new Slider { Minimum = Math.Log10(0.1), Maximum = Math.Log10(30), Value = Math.Log10(b.Q), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        qs.ValueChanged += (_, e) => { b.Q = Math.Round(Math.Pow(10, e.NewValue) * 100) / 100; qText.Text = $"Q {b.Q:0.00}"; Edited(); };
        Add(qs, 8);

        var del = new Button { Content = "🗑", Padding = new Thickness(6, 2, 6, 2), ToolTip = "Delete band" };
        del.Click += (_, _) => { bands.RemoveAt(i); _selectedBand = null; Edited(); BuildTabs(); };
        Add(del, 9);
        g.MouseLeftButtonDown += (_, _) => { _selectedBand = i; if (_curve is not null) { _curve.Selected = i; _curve.InvalidateVisual(); } };
        return g;
    }

    private void AddBand()
    {
        if (_draft?.Eq is not ParametricLayer pl) return;
        var fs = new List<double> { 20 };
        fs.AddRange(pl.Bands.Select(b => b.Freq).OrderBy(f => f));
        fs.Add(20000);
        double best = 1000, gap = 0;
        for (int i = 0; i + 1 < fs.Count; i++)
            if (Math.Log(fs[i + 1] / fs[i]) > gap) { gap = Math.Log(fs[i + 1] / fs[i]); best = Math.Sqrt(fs[i] * fs[i + 1]); }
        pl.Bands.Add(new Band { Kind = FilterKind.Peak, Freq = Math.Round(best), GainDb = 0, Q = 1 });
        _selectedBand = pl.Bands.Count - 1;
        Edited();
        BuildTabs();
    }

    private static readonly double[] G10 = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    private static readonly double[] G31 = { 20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
                                             1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000 };

    private void SwitchMode(int idx)
    {
        if (_draft is null) return;
        switch (idx)
        {
            case 0 when _draft.Eq is GraphicLayer gl:
                double q = gl.Points.Count > 10 ? 4.3 : 1.41;
                _draft.Eq = new ParametricLayer { Bands = gl.Points.Where(p => p.GainDb != 0).Select(p => new Band { Freq = p.Freq, GainDb = p.GainDb, Q = q }).ToList() };
                break;
            case 1:
                _draft.Eq = new GraphicLayer { Points = G10.Select(f => new GraphicPoint { Freq = f }).ToList() };
                break;
            case 2:
                _draft.Eq = new GraphicLayer { Points = G31.Select(f => new GraphicPoint { Freq = f }).ToList() };
                break;
        }
        _selectedBand = null;
        Edited();
    }

    // ---------------------------------------------------------------- device & rules

    private UIElement BuildDevice()
    {
        var s = _c.State;
        if (s?.CurrentGroup is not { } g) return Ui.Text("No output device");
        var panel = Ui.Column(8);

        var name = new TextBox { Text = g.Name, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        name.LostFocus += (_, _) => { if (name.Text != g.Name) { g.Name = name.Text; _c.UpdateGroup(g); } };
        var enabled = new CheckBox { Content = "EQ enabled for this device", IsChecked = g.Enabled };
        enabled.Click += (_, _) => { g.Enabled = enabled.IsChecked == true; _c.UpdateGroup(g); };
        var def = new ComboBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        def.Items.Add(new ComboBoxItem { Content = "None (flat)", Tag = null, IsSelected = g.DefaultProfile is null });
        foreach (var p in s.Library.Profiles) def.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id, IsSelected = p.Id == g.DefaultProfile });
        def.SelectionChanged += (_, _) => { if (def.SelectedItem is ComboBoxItem it) { g.DefaultProfile = it.Tag as string; _c.UpdateGroup(g); } };
        panel.Children.Add(Ui.Group("Device", Ui.Column(6, Ui.Row(Ui.Text("Name  "), name), enabled, Ui.Row(Ui.Text("Default profile  "), def))));

        // Linking
        var link = Ui.Column(4);
        foreach (var m in g.Members)
        {
            bool mine = m.Key.Machine == s.Machine;
            var row = Ui.Row(Ui.Text((mine ? "💻 " : "🖥 ") + m.OsName), Ui.Text("   " + (mine ? "this PC" : m.Key.Machine), 11, color: Ui.Secondary));
            if (g.Members.Count > 1) { var key = m.Key; row.Children.Add(Ui.Button("Unlink", () => _c.Unlink(key))); }
            link.Children.Add(row);
        }
        var others = s.Library.Groups.Where(o => o.Id != g.Id && o.Members.Count > 0 && o.Members.All(m => m.Key.Machine != s.Machine)).ToList();
        if (others.Count > 0)
        {
            var pick = new ComboBox { Width = 300 };
            foreach (var o in others) pick.Items.Add(new ComboBoxItem { Content = $"{o.Name} ({o.Members[0].Key.Machine})", Tag = o.Id });
            link.Children.Add(Ui.Row(pick, Ui.Button("Link", () => { if (pick.SelectedItem is ComboBoxItem { Tag: string id }) _c.Link(id); })));
        }
        link.Children.Add(Ui.Text("Linked devices share default profile, favorites, rules and the selected profile across machines.", 11, color: Ui.Secondary));
        panel.Children.Add(Ui.Group("Same headphones on other machines", link));

        // Favorites
        var favs = new WrapPanel();
        foreach (var p in s.Library.Profiles)
        {
            var cb = new CheckBox { Content = p.Name, IsChecked = g.Favorites.Contains(p.Id), Margin = new Thickness(0, 0, 16, 6) };
            var id = p.Id;
            cb.Click += (_, _) => _c.ToggleFavorite(id);
            favs.Children.Add(cb);
        }
        panel.Children.Add(Ui.Group("Favorites (shown in the tray panel while this device is connected)", favs));

        // Rules
        var rules = Ui.Column(4);
        foreach (var r in s.Library.Rules.Where(r => r.Group == g.Id)) rules.Children.Add(RuleRow(r, s.Library.Profiles));
        var apps = AppWatcher.Pickable();
        var appPick = new ComboBox { Width = 260 };
        foreach (var a in apps) appPick.Items.Add(new ComboBoxItem { Content = $"{a.Name} ({a.Exe})", Tag = a });
        rules.Children.Add(Ui.Row(appPick, Ui.Button("Add rule", () =>
        {
            if (appPick.SelectedItem is not ComboBoxItem { Tag: AppWatcher.App a } || s.Library.Profiles.Count == 0) return;
            _c.UpsertRule(new Rule { Group = g.Id, App = new AppMatch { Name = a.Name, WinExe = a.Exe }, Profile = s.Library.Profiles[0].Id });
        })));
        rules.Children.Add(Ui.Text("When the app runs (or is focused) and this device is the output, its profile takes over. Rules made on the Mac need the Windows .exe filled in.", 11, color: Ui.Secondary));
        panel.Children.Add(Ui.Group("App rules", rules));
        return panel;
    }

    private UIElement RuleRow(Rule r, List<Profile> profiles)
    {
        var en = new CheckBox { IsChecked = r.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        en.Click += (_, _) => { r.Enabled = en.IsChecked == true; _c.UpsertRule(r); };
        var exe = new TextBox { Text = r.App.WinExe ?? "", Width = 140, ToolTip = "Windows executable, e.g. cs2.exe", Margin = new Thickness(8, 0, 8, 0) };
        exe.LostFocus += (_, _) =>
        {
            var v = exe.Text.Trim().ToLowerInvariant();
            if (v != (r.App.WinExe ?? "")) { r.App.WinExe = v.Length == 0 ? null : v; _c.UpsertRule(r); }
        };
        var trig = new ComboBox { Margin = new Thickness(0, 0, 8, 0) };
        trig.Items.Add(new ComboBoxItem { Content = "while running", Tag = Trigger.Running, IsSelected = r.Trigger == Trigger.Running });
        trig.Items.Add(new ComboBoxItem { Content = "while focused", Tag = Trigger.Focused, IsSelected = r.Trigger == Trigger.Focused });
        trig.SelectionChanged += (_, _) => { if (trig.SelectedItem is ComboBoxItem { Tag: Trigger t }) { r.Trigger = t; _c.UpsertRule(r); } };
        var prof = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var p in profiles) prof.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id, IsSelected = p.Id == r.Profile });
        prof.SelectionChanged += (_, _) => { if (prof.SelectedItem is ComboBoxItem { Tag: string id }) { r.Profile = id; _c.UpsertRule(r); } };
        var del = Ui.Button("🗑", () => _c.DeleteRule(r.Id));
        return Ui.Row(en, Ui.Text(r.App.Name, 13, FontWeights.SemiBold), exe, trig, Ui.Text("→  "), prof, del);
    }

    // ---------------------------------------------------------------- sync

    private string _syncMode = "";
    private string _url = "", _token = "";
    private int _port = 7878;

    private UIElement BuildSync()
    {
        var info = _c.State?.Sync ?? new SyncInfo();
        if (_syncMode == "") { _syncMode = info.Mode; _url = info.Url ?? ""; _token = info.Mode == "server" ? info.Token ?? "" : ""; _port = info.HostPort ?? 7878; }
        var panel = Ui.Column(8);

        string status = (info.Mode, info.Status) switch
        {
            ("off", _) => "Sync is off",
            (_, "connected") => info.Mode == "host" ? "Hosting · connected" : "Connected",
            (_, "connecting") => "Connecting…",
            (_, "error") => "Problem",
            _ => info.Status,
        };
        var dotColor = info.Status switch { "connected" => Brushes.LimeGreen, "connecting" => Brushes.Gold, "error" => Brushes.Red, _ => Brushes.Gray };
        var head = Ui.Row(Ui.Text("● ", 16, color: dotColor), Ui.Text(status, 15, FontWeights.SemiBold));
        if (info.Pending > 0) head.Children.Add(Ui.Text($"   {info.Pending} change(s) waiting", 11, color: Ui.Secondary));
        panel.Children.Add(head);
        if (!string.IsNullOrEmpty(info.Detail)) panel.Children.Add(Ui.Text(info.Detail, 11, color: Ui.Secondary));

        var modes = Ui.Column(4);
        foreach (var (m, label) in new[] { ("off", "Off"), ("server", "Connect to a server"), ("host", "Host on this PC (LAN)") })
        {
            var rb = new RadioButton { Content = label, IsChecked = _syncMode == m, GroupName = "syncmode" };
            rb.Checked += (_, _) => { _syncMode = m; BuildTabs(); };
            modes.Children.Add(rb);
        }
        panel.Children.Add(Ui.Group("Mode", modes));

        if (_syncMode == "server")
        {
            var url = new TextBox { Text = _url, Width = 360, ToolTip = "192.168.0.10:7878 or https://eq.example.com" };
            url.TextChanged += (_, _) => _url = url.Text;
            var token = new PasswordBox { Password = _token, Width = 360 };
            token.PasswordChanged += (_, _) => _token = token.Password;
            var col = Ui.Column(6, Ui.Row(Ui.Text("Address  ", 13), url), Ui.Row(Ui.Text("Token/code", 13), token),
                Ui.Row(Ui.Button(_c.Discovering ? "Searching…" : "Find on my network", _c.Discover), Ui.Button("Connect", () => _c.ConfigureSync("server", _url, _token), primary: true)));
            foreach (var h in _c.Discovered)
            {
                var host = h;
                col.Children.Add(Ui.Button($"{host.Name} — {host.Addr}", () => { _url = host.Addr; BuildTabs(); }));
            }
            col.Children.Add(Ui.Text("Your Docker server (server/docker-compose.yml), any cloud host running it, or another machine hosting on the LAN.", 11, color: Ui.Secondary));
            panel.Children.Add(Ui.Group("Server", col));
        }
        else if (_syncMode == "host")
        {
            var col = Ui.Column(6);
            if (info.Mode == "host" && info.Token is { } code)
            {
                col.Children.Add(Ui.Row(Ui.Text("Code  "), new TextBox { Text = code, IsReadOnly = true, FontSize = 22, FontFamily = new FontFamily("Consolas"), Width = 200 }));
                foreach (var a in info.HostAddresses ?? new()) col.Children.Add(Ui.Row(Ui.Text("Address  "), new TextBox { Text = a, IsReadOnly = true, Width = 200 }));
                col.Children.Add(Ui.Row(Ui.Button("New code", () => _c.ConfigureSync("host", port: _port))));
            }
            else
            {
                var port = new TextBox { Text = _port.ToString(), Width = 80 };
                port.TextChanged += (_, _) => { if (int.TryParse(port.Text, out var p)) _port = p; };
                col.Children.Add(Ui.Row(Ui.Text("Port  "), port, Ui.Button("Start hosting", () => _c.ConfigureSync("host", port: _port), primary: true)));
            }
            col.Children.Add(Ui.Text("On the other machine choose \"Connect to a server\", find this PC (or type the address) and enter the code. Windows Firewall will ask to allow MyOwnEQ on private networks.", 11, color: Ui.Secondary));
            panel.Children.Add(Ui.Group("LAN host", col));
        }
        else if (info.Mode != "off")
        {
            panel.Children.Add(Ui.Row(Ui.Button("Turn sync off", () => _c.ConfigureSync("off"))));
        }

        var u = _c.Update;
        var autoUpd = new CheckBox { Content = "Download updates automatically, install on restart", IsChecked = u?.Auto ?? true };
        autoUpd.Click += (_, _) => _c.SetAutoUpdate(autoUpd.IsChecked == true);
        var updRow = Ui.Row(Ui.Text((u?.Label ?? "") + "   ", 12, color: Ui.Secondary));
        updRow.Children.Add(u?.IsReady == true
            ? Ui.Button("Restart now", () => { if (_c.ApplyUpdate()) Application.Current.Shutdown(); }, primary: true)
            : Ui.Button("Check now", _c.CheckForUpdates));
        panel.Children.Add(Ui.Group("Updates", Ui.Column(6, autoUpd, updRow, Ui.Text($"Version {AppController.AppVersion}", 11, color: Ui.Secondary))));

        panel.Children.Add(Ui.Group("This PC", Ui.Text($"Machine id: {_c.State?.Machine}", 12, color: Ui.Secondary)));
        if (_c.Apo.Status == ApoStatus.Ready)
            panel.Children.Add(Ui.Group("Equalizer APO", Ui.Row(Ui.Button("Device selector…", _c.Apo.OpenConfigurator), Ui.Button("Restore my old config", _c.ApoRestore))));
        return panel;
    }
}
