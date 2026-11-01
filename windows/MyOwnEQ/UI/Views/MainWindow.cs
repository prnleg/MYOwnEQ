using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using static MyOwnEQ.UI.Loc;

namespace MyOwnEQ.UI;

/// <summary>Where the main window is (Nav in the Mac app).</summary>
public sealed record Nav(string Kind, string? Id = null)
{
    public static Nav Profile(string id) => new("profile", id);
    public static readonly Nav Devices = new("devices"), Sync = new("sync"), Preferences = new("preferences");
}

/// <summary>Main window (EditorWindow in the Mac app): sidebar + page over the themed background.</summary>
public sealed class MainWindow : Window
{
    private readonly AppController _c;
    private readonly Grid _root = new();
    private readonly ContentControl _sidebarHost = new();
    private readonly ContentControl _detail = new();
    private Nav? _nav;
    private ProfileEditor? _editor;
    private UIElement? _page;
    private string _search = "";
    /// <summary>Sidebar mini curves, recomputed only when a profile changes.</summary>
    private readonly Dictionary<string, (string json, double[] values)> _minis = new();
    private TextBox? _searchBox;

    public MainWindow(AppController c)
    {
        _c = c;
        Title = "MyOwnEQ";
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/MyOwnEQ.ico")); } catch { }
        Width = 1180;
        Height = 800;
        MinWidth = 960;
        MinHeight = 660;
        FontFamily = Ui.Font;
        UseLayoutRounding = true;

        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Content = _root;

        _c.Changed += OnStateChanged;
        Ui.Store.Changed += Restyle;
        Ui.Lang.Changed += Restyle;
        SourceInitialized += (_, _) => Dwm.Style(this);
        Closing += (_, e) => { if (!ConfirmLeave(allowCancel: true)) e.Cancel = true; };
        Closed += (_, _) =>
        {
            _c.Changed -= OnStateChanged;
            Ui.Store.Changed -= Restyle;
            Ui.Lang.Changed -= Restyle;
            _editor?.Close();
            _c.FlushSave();
            _c.Preview(null);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { _editor?.Commit(); e.Handled = true; }
        };

        Build();
        var first = _c.ActiveProfile?.Id ?? _c.State?.Library.Profiles.FirstOrDefault()?.Id;
        Navigate(first is null ? Nav.Preferences : Nav.Profile(first));
    }

    public Nav? Current => _nav;

    // ------------------------------------------------------------ layout

    private void Build()
    {
        // _sidebarHost / _detail are reused across rebuilds: detach them first
        // (a WPF element can have only one parent).
        if (_sidebarHost.Parent is Border oldSide) oldSide.Child = null;
        _root.Children.Clear();
        Background = Theme.Frozen(Ui.T.BaseColor);
        var bg = new ThemeBackground(_c);
        Grid.SetColumnSpan(bg, 2);
        _root.Children.Add(bg);

        var side = new Border
        {
            Background = Theme.Frozen(Theme.WithAlpha(Ui.T.PanelColor, Ui.T.Dark ? 0.55 : 0.6)),
            BorderBrush = Ui.T.InkBrush(0.08),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _sidebarHost,
        };
        Grid.SetColumn(side, 0);
        _root.Children.Add(side);
        Grid.SetColumn(_detail, 1);
        _root.Children.Add(_detail);
        BuildSidebar();
    }

    /// <summary>Theme or language changed: rebuild everything, keep the page and any draft.</summary>
    private void Restyle()
    {
        Dwm.Style(this);
        Build();
        if (_editor is not null) { _editor.Rebuild(); _detail.Content = _editor; }
        else if (_nav is not null) ShowPage(_nav, animate: false);
    }

    private void OnStateChanged()
    {
        BuildSidebar();
        if (_editor is not null) _editor.OnStateChanged();
        else if (_page is FrameworkElement fe && !fe.IsKeyboardFocusWithin && _nav is not null && _nav.Kind != "preferences")
            ShowPage(_nav, animate: false); // forms: refresh unless the user is typing
    }

    // ------------------------------------------------------------ navigation

    /// <summary>Asks to save unsaved changes (auto-save off). False = stay.</summary>
    private bool ConfirmLeave(bool allowCancel)
    {
        if (_editor is null || !_editor.NeedsAsk) return true;
        switch (Dialogs.AskSave(this, _editor.DraftName, allowCancel))
        {
            case Dialogs.SaveChoice.Save: _editor.Commit(); return true;
            case Dialogs.SaveChoice.DontSave: _editor.Discard(); return true;
            default: return false;
        }
    }

    public void Navigate(Nav n)
    {
        if (n == _nav) return;
        if (!ConfirmLeave(allowCancel: true)) return;
        ShowPage(n, animate: true);
    }

    private void ShowPage(Nav n, bool animate)
    {
        _nav = n;
        if (_editor is not null && !(n.Kind == "profile" && n.Id == _editor.ProfileId))
        {
            _editor.Close();
            _editor = null;
        }
        FrameworkElement page;
        switch (n.Kind)
        {
            case "profile" when _c.State?.FindProfile(n.Id) is { } p:
                if (_editor is null)
                {
                    _editor = new ProfileEditor(_c, p, onFork: id => { _nav = Nav.Profile(id); BuildSidebar(); });
                }
                page = _editor;
                break;
            case "devices": page = new DeviceSettings(_c); break;
            case "sync": page = new SyncSettings(_c); break;
            case "preferences": page = new PreferencesPage(_c); break;
            default: page = Empty(); break;
        }
        _page = page;
        _detail.Content = page;
        if (animate) Ui.Appear(page);
        BuildSidebar();
    }

    private FrameworkElement Empty()
    {
        var logo = new LogoMark { Width = 84, Height = 84 };
        var col = Ui.VStack(14, logo, Ui.Text(L("Pick a profile"), 18, FontWeights.SemiBold), Ui.Secondary(L("or make a new one with +"), 13));
        col.HorizontalAlignment = HorizontalAlignment.Center;
        col.VerticalAlignment = VerticalAlignment.Center;
        foreach (FrameworkElement ch in col.Children) ch.HorizontalAlignment = HorizontalAlignment.Center;
        return col;
    }

    // ------------------------------------------------------------ sidebar

    private void BuildSidebar()
    {
        var s = _c.State;
        var t = Ui.T;
        var favIds = new HashSet<string>(s?.CurrentGroup?.Favorites ?? new());
        var all = (s?.Library.Profiles ?? new()).Where(p => _search.Length == 0 || p.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var favs = all.Where(p => favIds.Contains(p.Id)).ToList();
        var mine = all.Where(p => !favIds.Contains(p.Id) && !p.Tags.Contains("builtin")).ToList();
        var presets = all.Where(p => !favIds.Contains(p.Id) && p.Tags.Contains("builtin")).ToList();

        var col = new StackPanel { Margin = new Thickness(12, 12, 12, 16) };
        col.Children.Add(NowPlaying());

        // Search + new profile
        bool hadFocus = _searchBox?.IsKeyboardFocusWithin == true;
        _searchBox = new TextBox { Text = _search, Padding = new Thickness(26, 6, 8, 6), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
        _searchBox.TextChanged += (_, _) => { _search = _searchBox.Text; BuildSidebar(); };
        var searchGrid = new Grid();
        searchGrid.Children.Add(_searchBox);
        var mag = Ui.Glyph(Ui.GSearch, 12, t.InkBrush(0.5));
        mag.HorizontalAlignment = HorizontalAlignment.Left;
        mag.Margin = new Thickness(9, 0, 0, 0);
        mag.IsHitTestVisible = false;
        searchGrid.Children.Add(mag);
        if (_search.Length == 0)
        {
            var ph = Ui.Secondary(L("Search profiles"), 13);
            ph.Margin = new Thickness(28, 0, 0, 0);
            ph.IsHitTestVisible = false;
            searchGrid.Children.Add(ph);
        }
        var add = Ui.IconButton(Ui.GAdd, NewProfile, L("New profile"), t.InkBrush(), 14);
        add.Margin = new Thickness(6, 0, 0, 0);
        var searchRow = Ui.Spread(searchGrid, add);
        searchRow.Margin = new Thickness(0, 12, 0, 4);
        col.Children.Add(searchRow);

        if (favs.Count > 0) Section(col, L("Favorites"), null, favs.Select(ProfileRow));
        var mineRows = mine.Select(ProfileRow).ToList();
        if (mineRows.Count == 0)
            mineRows.Add(Hint(_search.Length == 0 ? L("Edit a preset or press + to make your own") : L("Nothing matches “%@”", _search)));
        Section(col, L("My Profiles"), null, mineRows);
        if (presets.Count > 0)
        {
            bool show = Ui.Store.Settings.ShowPresets || _search.Length > 0;
            var chevron = Ui.IconButton(Ui.GChevron, () => Ui.Store.Update(x => x.ShowPresets = !x.ShowPresets),
                                        Ui.Store.Settings.ShowPresets ? L("Hide presets") : L("Show presets"), t.InkBrush(0.6), 10);
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            chevron.RenderTransform = new RotateTransform(Ui.Store.Settings.ShowPresets ? 90 : 0);
            Section(col, L("Presets"), chevron, show ? presets.Select(ProfileRow) : Enumerable.Empty<UIElement>());
        }
        Section(col, L("Settings"), null, new[]
        {
            NavRow(Ui.GHeadphones, L("Devices & Rules"), Nav.Devices),
            NavRow(Ui.GSync, L("Sync & Updates"), Nav.Sync),
            NavRow(Ui.GSettings, L("Preferences"), Nav.Preferences),
        });

        _sidebarHost.Content = new ScrollViewer { Content = col, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (hadFocus) Dispatcher.BeginInvoke(() => { _searchBox.Focus(); _searchBox.CaretIndex = _searchBox.Text.Length; });
    }

    private static UIElement Hint(string s)
    {
        var tb = Ui.Secondary(s, 12);
        tb.Margin = new Thickness(10, 4, 0, 4);
        return tb;
    }

    private static void Section(StackPanel col, string title, UIElement? accessory, IEnumerable<UIElement> rows)
    {
        var caps = Ui.Caps(title);
        UIElement head = accessory is null ? caps : Ui.Spread(caps, accessory);
        ((FrameworkElement)head).Margin = new Thickness(6, 16, 0, 6);
        col.Children.Add(head);
        foreach (var r in rows) col.Children.Add(r);
    }

    private Border SelectableRow(UIElement content, bool selected, Action onClick)
    {
        var t = Ui.T;
        var b = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            Background = selected ? Theme.Frozen(Theme.WithAlpha(t.AccentColor, t.Dark ? 0.28 : 0.18)) : Brushes.Transparent,
        };
        b.MouseEnter += (_, _) => { if (!selected) b.Background = t.InkBrush(0.06); };
        b.MouseLeave += (_, _) => { if (!selected) b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }

    private UIElement NavRow(string glyph, string label, Nav target)
    {
        var row = Ui.HStack(10, Ui.Glyph(glyph, 14, Ui.T.InkBrush(0.85)), Ui.Text(label, 13));
        return SelectableRow(row, _nav == target, () => Navigate(target));
    }

    private UIElement ProfileRow(Profile p)
    {
        var t = Ui.T;
        bool active = p.Id == _c.ActiveProfile?.Id;
        var mini = new MiniCurve { Width = 34, Height = 18 };
        var json = System.Text.Json.JsonSerializer.Serialize(p, Json.Options);
        if (!_minis.TryGetValue(p.Id, out var cached) || cached.json != json)
        {
            var cv = _c.CurveFor(p, 40);
            cached = (json, cv is null ? Array.Empty<double>() : cv.Total.Select(v => v - cv.Preamp).ToArray());
            _minis[p.Id] = cached;
        }
        mini.Values = cached.values;
        var miniBox = new Border { Child = mini, Padding = new Thickness(4), CornerRadius = new CornerRadius(6), Background = t.InkBrush(0.07) };
        var name = Ui.Text(p.Name, 13);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        name.Margin = new Thickness(10, 0, 4, 0);
        var grid = new DockPanel();
        DockPanel.SetDock(miniBox, Dock.Left);
        grid.Children.Add(miniBox);
        if (active)
        {
            var playing = Ui.Glyph(Ui.GVolume, 12, Theme.Frozen(t.AccentColor));
            playing.ToolTip = L("Playing now");
            DockPanel.SetDock(playing, Dock.Right);
            grid.Children.Add(playing);
        }
        grid.Children.Add(name);
        var row = SelectableRow(grid, _nav?.Kind == "profile" && _nav.Id == p.Id, () => Navigate(Nav.Profile(p.Id)));
        row.ContextMenu = ProfileMenu(p);
        return row;
    }

    private ContextMenu ProfileMenu(Profile p)
    {
        bool preset = p.Tags.Contains("builtin");
        bool fav = _c.State?.CurrentGroup?.Favorites.Contains(p.Id) == true;
        var m = new ContextMenu();
        MenuItem Item(string label, Action a, bool enabled = true)
        {
            var i = new MenuItem { Header = label, IsEnabled = enabled };
            i.Click += (_, _) => a();
            m.Items.Add(i);
            return i;
        }
        Item(L("Use Now"), () => _c.Select(p.Id));
        Item(L("Rename…"), () => { if (Dialogs.AskName(this, L("Rename “%@”", p.Name), p.Name) is { } n) _c.Rename(p.Id, n); }, !preset);
        Item(fav ? L("Remove from Favorites") : L("Add to Favorites"), () => _c.ToggleFavorite(p.Id));
        Item(L("Duplicate"), () => Duplicate(p));
        Item(L("Export Equalizer APO…"), () => Export(p));
        m.Items.Add(new Separator());
        Item(L("Delete"), () =>
        {
            if (MessageBox.Show(this, L("Delete “%@”? This syncs to your other machines.", p.Name), "MyOwnEQ", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            bool open = _nav?.Id == p.Id;
            if (open && _editor is not null) { _editor.Close(); _editor = null; }
            _c.Delete(p.Id);
            if (open) ShowPage(_c.ActiveProfile is { } a ? Nav.Profile(a.Id) : Nav.Preferences, animate: true);
        }, !preset);
        return m;
    }

    private void NewProfile()
    {
        var p = new Profile { Name = L("New Profile") };
        _c.Save(p);
        Navigate(Nav.Profile(p.Id));
    }

    private void Duplicate(Profile p)
    {
        var d = Json.Clone(p);
        d.Id = Profile.NewId();
        d.Name = p.Name + L(" copy");
        d.Tags.Remove("builtin");
        _c.Save(d);
        Navigate(Nav.Profile(d.Id));
    }

    private void Export(Profile p)
    {
        if (_c.ExportApo(p.Id) is not { } text) return;
        var dlg = new SaveFileDialog { FileName = p.Name + ".txt", Filter = "Text (*.txt)|*.txt" };
        if (dlg.ShowDialog(this) == true) System.IO.File.WriteAllText(dlg.FileName, text);
    }

    /// <summary>Top of the sidebar: logo, output device, what's playing, master switch.</summary>
    private UIElement NowPlaying()
    {
        var t = Ui.T;
        var s = _c.State;
        var logo = new LogoMark { Width = 30, Height = 30 };
        var title = Ui.Text("MyOwnEQ", 15, FontWeights.Heavy);
        title.Margin = new Thickness(10, 0, 0, 0);
        var sw = new ToggleSwitch(s?.Enabled ?? true);
        sw.Toggled += on => _c.SetEnabled(on);
        var top = Ui.Spread(Ui.HStack(0, logo, title), sw);

        var devName = Ui.Text(s?.CurrentGroup?.Name ?? _c.Current?.Name ?? L("No output"), 13, FontWeights.SemiBold);
        devName.TextTrimming = TextTrimming.CharacterEllipsis;
        devName.TextWrapping = TextWrapping.NoWrap;
        var why = Ui.Secondary($"{_c.ActiveProfile?.Name ?? L("Flat")} · {_c.Reason}", 11.5);
        why.TextTrimming = TextTrimming.CharacterEllipsis;
        why.TextWrapping = TextWrapping.NoWrap;
        var dev = Ui.HStack(8, Ui.Glyph(Ui.GHeadphones, 14, t.CurveBrush()), Ui.VStack(1, devName, why));
        dev.Margin = new Thickness(0, 10, 0, 0);
        bool on = (s?.Enabled ?? true) && _c.Current is not null;
        return Ui.Glass(Ui.VStack(0, top, dev), radius: 12, highlighted: on, padding: 12, shadow: false);
    }
}
