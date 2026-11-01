using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyOwnEQ.Platform;

namespace MyOwnEQ.UI;

/// <summary>Tray flyout: device, on/off, why-this-profile, favorites, Easy knobs.</summary>
public sealed class QuickPanel : Window
{
    private readonly AppController _c;
    private readonly Action _openEditor, _quit, _restartForUpdate;
    private readonly StackPanel _root = new() { Margin = new Thickness(16) };
    private Profile? _draft;
    private bool _dragging;

    public QuickPanel(AppController c, Action openEditor, Action quit, Action restartForUpdate)
    {
        _c = c;
        _openEditor = openEditor;
        _quit = quit;
        _restartForUpdate = restartForUpdate;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "MyOwnEQ";
        Content = new Border { Child = _root, BorderThickness = new Thickness(1), BorderBrush = Ui.Secondary };
        Deactivated += (_, _) => { Hide(); HiddenAt = DateTime.UtcNow; };
        _c.Changed += () => { if (IsVisible && !_dragging) Build(); };
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
    }

    private void Build()
    {
        _root.Children.Clear();
        var s = _c.State;

        // Header: device + switch
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var toggle = new CheckBox { IsChecked = s?.Enabled ?? true, VerticalAlignment = VerticalAlignment.Center, ToolTip = "EQ on/off" };
        if (Application.Current.TryFindResource("ToggleSwitchStyle") is Style ts) toggle.Style = ts;
        toggle.Click += (_, _) => _c.SetEnabled(toggle.IsChecked == true);
        DockPanel.SetDock(toggle, Dock.Right);
        header.Children.Add(toggle);
        if (s?.Sync is { Mode: not "off" } sync)
        {
            var dot = Ui.Text(sync.Status == "connected" ? "⟳" : "⚠", 14, color: sync.Status == "connected" ? Ui.Secondary : Brushes.Orange);
            dot.ToolTip = $"Sync: {sync.Status} {sync.Detail}";
            dot.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(dot, Dock.Right);
            header.Children.Add(dot);
        }
        header.Children.Add(Ui.Column(0,
            Ui.Text(s?.CurrentGroup?.Name ?? _c.Current?.Name ?? "No output", 15, FontWeights.SemiBold),
            Ui.Text(_c.Apo.Status == ApoStatus.Ready ? "Equalizer APO · EQ on" : "EQ not active", 11, color: Ui.Secondary)));
        _root.Children.Add(header);

        if (_c.Apo.Status != ApoStatus.Ready)
        {
            _root.Children.Add(Ui.Text("Equalizer APO needs setup. Open MyOwnEQ to finish.", 12, color: Brushes.Orange));
            _root.Children.Add(Ui.Row(Ui.Button("Open setup", _openEditor, primary: true)));
        }

        if (_c.Update is { IsReady: true } upd)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var restart = Ui.Button("Restart", _restartForUpdate, primary: true);
            DockPanel.SetDock(restart, Dock.Right);
            row.Children.Add(restart);
            row.Children.Add(Ui.Text($"⬇ MyOwnEQ {upd.Version} ready", 12));
            _root.Children.Add(row);
        }

        // Active profile + picker + favorite star
        var active = _c.ActiveProfile;
        var activeRow = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
        var picker = new ComboBox { MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
        foreach (var p in s?.Library.Profiles ?? new()) picker.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id, IsSelected = p.Id == active?.Id });
        picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is ComboBoxItem { Tag: string id } && id != _c.ActiveProfile?.Id) _c.Select(id); };
        DockPanel.SetDock(picker, Dock.Right);
        activeRow.Children.Add(picker);
        if (active is not null)
        {
            bool fav = s?.CurrentGroup?.Favorites.Contains(active.Id) ?? false;
            var star = new Button { Content = fav ? "★" : "☆", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Favorite for this device" };
            star.Click += (_, _) => _c.ToggleFavorite(active.Id);
            DockPanel.SetDock(star, Dock.Right);
            activeRow.Children.Add(star);
        }
        activeRow.Children.Add(Ui.Column(0, Ui.Text(active?.Name ?? "Flat", 18, FontWeights.SemiBold), Ui.Text(_c.Reason, 11, color: Ui.Secondary)));
        _root.Children.Add(activeRow);

        // Favorites (current device only)
        var favs = (s?.CurrentGroup?.Favorites ?? new()).Select(id => s!.FindProfile(id)).Where(p => p is not null).ToList();
        if (favs.Count > 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var p in favs)
            {
                var b = Ui.Button(p!.Name, () => _c.Select(p.Id), primary: p.Id == active?.Id);
                b.Margin = new Thickness(0, 0, 6, 6);
                wrap.Children.Add(b);
            }
            _root.Children.Add(wrap);
        }

        // Easy knobs on the active profile: preview while dragging, save on release.
        if (active is not null)
        {
            _draft = Json.Clone(active);
            void Knob(string label, Func<Macros, double> get, Action<Macros, double> set) =>
                _root.Children.Add(Ui.Slider(label, -12, 12, get(_draft!.Macros), v => Ui.Db(v),
                    v => { _dragging = true; set(_draft!.Macros, Math.Round(v * 2) / 2); _c.Preview(_draft); },
                    () => { if (!_dragging) return; _dragging = false; _c.Save(_draft!); _c.Preview(null); }, 60, 0.5));
            Knob("Bass", m => m.Bass, (m, v) => m.Bass = v);
            Knob("Mids", m => m.Mids, (m, v) => m.Mids = v);
            Knob("Clarity", m => m.Clarity, (m, v) => m.Clarity = v);
            Knob("Treble", m => m.Treble, (m, v) => m.Treble = v);
        }

        // Footer
        var startup = new CheckBox { Content = "Start with Windows", IsChecked = Startup.Enabled, Margin = new Thickness(0, 10, 0, 6) };
        startup.Click += (_, _) => Startup.Enabled = startup.IsChecked == true;
        _root.Children.Add(startup);
        _root.Children.Add(Ui.Row(Ui.Button("Editor…", _openEditor), Ui.Button("Quit", _quit)));
        if (_c.Message is { } m) _root.Children.Add(Ui.Text(m, 11, color: Ui.Secondary));
    }
}
