using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static MyOwnEQ.UI.Loc;

namespace MyOwnEQ.UI;

/// <summary>Tray flyout (QuickPanel in the Mac app): device, on/off, why-this-profile,
/// live mini curve, volume, favorite chips, Easy knobs.</summary>
// parity: tray-panel
public sealed class QuickPanel : Window
{
    private readonly AppController _c;
    private readonly Action _openEditor, _quit, _restartForUpdate;
    private readonly Grid _root = new();
    private readonly StackPanel _content = new() { Margin = new Thickness(14) };
    private Profile? _draft;
    private bool _dragging;

    public QuickPanel(AppController c, Action openEditor, Action quit, Action restartForUpdate)
    {
        _c = c;
        _openEditor = openEditor;
        _quit = quit;
        _restartForUpdate = restartForUpdate;
        Width = 350;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "MyOwnEQ";
        FontFamily = Ui.Font;
        UseLayoutRounding = true;
        _root.SizeChanged += (_, e) => _root.Clip = new RectangleGeometry(new Rect(e.NewSize), 14, 14);
        Content = new Border { Child = _root, CornerRadius = new CornerRadius(14), Margin = new Thickness(0) };
        Deactivated += (_, _) => { Hide(); HiddenAt = DateTime.UtcNow; };
        _c.Changed += () => { if (IsVisible && !_dragging) Build(); };
        Ui.Store.Changed += () => { if (IsVisible) Build(); };
        Ui.Lang.Changed += () => { if (IsVisible) Build(); };
    }

    /// <summary>When the panel last auto-hid (clicking the tray icon deactivates it first).</summary>
    public DateTime HiddenAt { get; private set; }

    public void ShowNearTray()
    {
        Build();
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
        Activate();
        Ui.Appear(_content);
    }

    private void Build()
    {
        var t = Ui.T;
        var s = _c.State;
        _root.Children.Clear();
        _root.Children.Add(new ThemeBackground(_c, compact: true));
        _root.Children.Add(new Border { BorderBrush = t.InkBrush(0.14), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), IsHitTestVisible = false });
        _content.Children.Clear();
        _root.Children.Add(_content);

        // Header: logo, device, sync, master switch.
        var logo = new LogoMark { Width = 32, Height = 32 };
        var devName = Ui.Text(s?.CurrentGroup?.Name ?? _c.Current?.Name ?? L("No output"), 14, FontWeights.SemiBold);
        devName.TextTrimming = TextTrimming.CharacterEllipsis;
        devName.TextWrapping = TextWrapping.NoWrap;
        var sub = Ui.Secondary(_c.Apo.Status != Platform.ApoStatus.Ready ? L("Equalizer APO needs setup")
                               : (s?.Enabled ?? true) ? L("EQ on") : L("EQ off"), 11.5);
        var sw = new ToggleSwitch(s?.Enabled ?? true);
        sw.Toggled += on => _c.SetEnabled(on);
        var right = Ui.HStack(8);
        if (s?.Sync is { Mode: not "off" } sync)
        {
            var g = Ui.Glyph(Ui.GSync, 13, sync.Status == "connected" ? t.InkBrush(0.6) : Theme.Frozen(Colors.Orange));
            g.ToolTip = sync.Status == "connected" ? L("Synced") : L("Sync: %@ %@", sync.Status, sync.Detail);
            right.Children.Add(g);
        }
        right.Children.Add(sw);
        var head = Ui.Spread(Ui.HStack(10, logo, Ui.VStack(1, devName, sub)), right);
        _content.Children.Add(head);

        if (_c.Apo.Status != Platform.ApoStatus.Ready)
            Add(Ui.Glass(Ui.Spread(Ui.Secondary(L("Equalizer APO needs setup"), 12), Ui.Button(L("Open"), _openEditor, prominent: true)), radius: 12, padding: 10, shadow: false));
        if (_c.Update is { IsReady: true } u)
            Add(Ui.Glass(Ui.Spread(Ui.Text(L("MyOwnEQ %@ ready", u.Version ?? ""), 13), Ui.Button(L("Restart"), _restartForUpdate, prominent: true)), radius: 12, highlighted: true, padding: 10, shadow: false));

        // Now playing + live mini curve.
        var active = _c.ActiveProfile;
        var title = Ui.Text(active?.Name ?? L("Flat"), 17, FontWeights.Bold);
        var why = Ui.Secondary(_c.Reason, 11.5);
        var change = Ui.Button(L("Change"), () => { });
        change.ContextMenu = new ContextMenu();
        foreach (var p in s?.Library.Profiles ?? new())
        {
            var mi = new MenuItem { Header = p.Name, IsChecked = p.Id == active?.Id };
            var id = p.Id;
            mi.Click += (_, _) => _c.Select(id);
            change.ContextMenu.Items.Add(mi);
        }
        change.Click += (_, _) => { change.ContextMenu.PlacementTarget = change; change.ContextMenu.IsOpen = true; };
        var nowRight = Ui.HStack(4, change);
        if (active is not null)
        {
            bool fav = s?.CurrentGroup?.Favorites.Contains(active.Id) ?? false;
            nowRight.Children.Add(Ui.IconButton(fav ? Ui.GStarFill : Ui.GStar, () => _c.ToggleFavorite(active.Id), L("Favorite for this device"),
                                                fav ? Brushes.Gold : t.InkBrush(0.6)));
        }
        var nowCol = Ui.VStack(8, Ui.Spread(Ui.VStack(1, title, why), nowRight));
        var shown = _draft ?? active;
        if (shown is not null)
        {
            var curve = new CurveView(_c, shown, CurveView.Edit.None, compact: true) { Height = 74 };
            var box = new Border { Child = curve, CornerRadius = new CornerRadius(10), ClipToBounds = true };
            curve.SizeChanged += (_, e) => curve.Clip = new RectangleGeometry(new Rect(e.NewSize), 10, 10);
            nowCol.Children.Add(box);
        }
        Add(Ui.Glass(nowCol, radius: 14, highlighted: (s?.Enabled ?? true) && _c.Current is not null, padding: 12, shadow: false));

        Add(Ui.Glass(new VolumeCard(_c), radius: 14, padding: 12, shadow: false));

        // Favorites (current device only).
        var favs = (s?.CurrentGroup?.Favorites ?? new()).Select(id => s!.FindProfile(id)).OfType<Profile>().ToList();
        if (favs.Count > 0)
        {
            var wrap = new WrapPanel();
            foreach (var p in favs) wrap.Children.Add(Chip(p, p.Id == active?.Id));
            Add(wrap);
        }

        // Easy knobs on the active profile: preview while dragging, save on release.
        if (active is not null) Add(Ui.Glass(Knobs(active), radius: 14, padding: 12, shadow: false));

        // Footer.
        var footer = Ui.Spread(
            Ui.HStack(8, Ui.Button(L("Editor"), _openEditor, prominent: true, glyph: Ui.GEdit), ImportMenu()),
            Ui.Button(L("Quit"), _quit));
        Add(footer);
        if (Ui.Toast(_c) is { } toast) Add(toast);
    }

    private void Add(UIElement e) => Ui.Add(_content, e, 12, horizontal: false);

    private UIElement Chip(Profile p, bool active)
    {
        var t = Ui.T;
        var text = new TextBlock { Text = p.Name, FontSize = 13, FontFamily = Ui.Font, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                                   Foreground = active ? Brushes.White : t.InkBrush(), TextTrimming = TextTrimming.CharacterEllipsis };
        var b = new Border
        {
            Child = text, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 6),
            Background = active ? t.CurveBrush() : t.InkBrush(0.08), BorderBrush = active ? Brushes.Transparent : t.InkBrush(0.12),
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand, MaxWidth = 150,
        };
        if (active) b.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Color = t.AccentColor, Opacity = 0.5 };
        b.MouseLeftButtonUp += (_, _) => _c.Select(p.Id);
        return b;
    }

    private UIElement Knobs(Profile active)
    {
        var col = Ui.VStack(6);
        void Knob(string label, Func<Macros, double> get, Action<Macros, double> set)
        {
            var p = _draft ?? active;
            var l = Ui.Text(label, 13, FontWeights.Medium);
            l.Width = 58;
            var v = Ui.Text($"{get(p.Macros):+0.0;-0.0;0.0}", 12, FontWeights.Medium);
            v.Width = 36;
            v.TextAlignment = TextAlignment.Right;
            var s = new GlowSlider(-12, 12, get(p.Macros)) { Step = 0.5 };
            s.ValueChanged += x =>
            {
                _draft ??= Json.Clone(active);
                set(_draft.Macros, x);
                v.Text = $"{x:+0.0;-0.0;0.0}";
                _c.Preview(_draft);
            };
            s.Editing += on =>
            {
                _dragging = on;
                if (on || _draft is null) return;
                var d = _draft;
                _draft = null;
                // Presets never change: the core turns the tweak into a new profile.
                if (d.Tags.Contains("builtin")) _c.ForkPreset(d, d.Name);
                else _c.Save(d);
                _c.Preview(null);
            };
            var row = new DockPanel();
            DockPanel.SetDock(l, Dock.Left);
            DockPanel.SetDock(v, Dock.Right);
            row.Children.Add(l);
            row.Children.Add(v);
            row.Children.Add(s);
            Ui.Add(col, row, 6, horizontal: false);
        }
        Knob(L("Bass"), m => m.Bass, (m, x) => m.Bass = x);
        Knob(L("Mids"), m => m.Mids, (m, x) => m.Mids = x);
        Knob(L("Clarity"), m => m.Clarity, (m, x) => m.Clarity = x);
        Knob(L("Treble"), m => m.Treble, (m, x) => m.Treble = x);
        return col;
    }

    private UIElement ImportMenu()
    {
        var b = Ui.Button(L("Import"), () => { });
        var menu = new ContextMenu();
        var autoeq = new MenuItem { Header = L("AutoEQ ParametricEQ.txt…") };
        autoeq.Click += (_, _) => ImportDialog.Run(_c, true);
        var apo = new MenuItem { Header = L("Equalizer APO / Peace config…") };
        apo.Click += (_, _) => ImportDialog.Run(_c, false);
        menu.Items.Add(autoeq);
        menu.Items.Add(apo);
        b.ContextMenu = menu;
        b.Click += (_, _) => { menu.PlacementTarget = b; menu.IsOpen = true; };
        return b;
    }
}
