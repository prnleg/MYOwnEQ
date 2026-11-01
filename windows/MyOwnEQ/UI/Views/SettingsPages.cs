using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MyOwnEQ.Platform;
using static MyOwnEQ.UI.Loc;

namespace MyOwnEQ.UI;

/// <summary>Scrollable settings page: header + cards.</summary>
public abstract class Page : ScrollViewer
{
    protected readonly AppController C;
    protected readonly StackPanel Body = new() { Margin = new Thickness(24, 20, 24, 28) };

    protected Page(AppController c, string title, string subtitle, string glyph)
    {
        C = c;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Body.Children.Add(Ui.PageHeader(title, subtitle, glyph));
        Content = Body;
    }

    protected void Card(string title, string glyph, UIElement content)
    {
        var card = Ui.Card(title, glyph, content);
        card.Margin = new Thickness(0, 16, 0, 0);
        Body.Children.Add(card);
    }

    protected static TextBox Field(string text, double width, string? tooltip = null) => new()
    {
        Text = text, Width = width, Padding = new Thickness(8, 5, 8, 5), FontSize = 13, ToolTip = tooltip,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center,
    };

    protected static DockPanel Labeled(string label, UIElement control, double width = 130)
    {
        var l = Ui.Text(label, 13);
        l.Width = width;
        var d = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(l, Dock.Left);
        d.Children.Add(l);
        d.Children.Add(control);
        return d;
    }
}

/// <summary>System volume + boost (VolumeCard). Boost is shown but disabled on Windows:
/// Equalizer APO has no limiter, so a boost there would clip.</summary>
public sealed class VolumeCard : StackPanel
{
    public VolumeCard(AppController c, bool compact = true)
    {
        var t = Ui.T;
        if (c.Volume is double v)
        {
            var pct = Ui.Text($"{v * 100:0}%", 12, FontWeights.Medium);
            pct.Width = 40;
            pct.TextAlignment = TextAlignment.Right;
            var icon = Ui.Glyph(v == 0 ? Ui.GMute : Ui.GVolume, 15, t.CurveBrush());
            icon.Width = 24;
            var s = new GlowSlider(0, 1, v) { Bipolar = false };
            bool dragging = false;
            s.Editing += on => dragging = on;
            s.ValueChanged += x => { c.SetVolume(x); pct.Text = $"{x * 100:0}%"; icon.Text = x == 0 ? Ui.GMute : Ui.GVolume; };
            // Follow volume keys / mixer without rebuilding (and not while dragging).
            Action<double> follow = x => { if (dragging) return; s.Value = x; pct.Text = $"{x * 100:0}%"; icon.Text = x == 0 ? Ui.GMute : Ui.GVolume; };
            Loaded += (_, _) => c.VolumeUpdated += follow;
            Unloaded += (_, _) => c.VolumeUpdated -= follow;
            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(pct, Dock.Right);
            row.Children.Add(icon);
            row.Children.Add(pct);
            row.Children.Add(s);
            Children.Add(row);
        }
        else Children.Add(Ui.Secondary(L("This output has no software volume. Use the device's own controls."), 12));

        double boost = c.State?.CurrentGroup?.BoostDb ?? 0;
        var bolt = Ui.Glyph(Ui.GBolt, 14, t.InkBrush(0.4));
        bolt.Width = 24;
        var bs = new GlowSlider(0, 12, boost) { Bipolar = false, Step = 0.5, IsEnabled = false, Opacity = 0.4 };
        var bv = Ui.Text(boost == 0 ? L("off") : $"+{boost:0.0} dB", 12, opacity: 0.5);
        bv.Width = 52;
        bv.TextAlignment = TextAlignment.Right;
        var brow = new DockPanel { Margin = new Thickness(0, 8, 0, 0), ToolTip = L("Boost") };
        DockPanel.SetDock(bolt, Dock.Left);
        DockPanel.SetDock(bv, Dock.Right);
        brow.Children.Add(bolt);
        brow.Children.Add(bv);
        brow.Children.Add(bs);
        Children.Add(brow);
        var note = Ui.Secondary(compact ? L("Boost needs a limiter, which Equalizer APO doesn't have.")
                                        : L("Boost needs a limiter so it never clips. Equalizer APO doesn't have one, so on Windows it stays off (it still works on the Mac)."), 11);
        note.Margin = new Thickness(24, 4, 0, 0);
        Children.Add(note);
    }
}

/// <summary>Equalizer APO setup steps as a glass card (Windows only).</summary>
public static class ApoSetupCard
{
    public static Border? Needed(AppController c)
    {
        var apo = c.Apo;
        if (apo.Status == ApoStatus.Ready && c.Message is null) return null;
        var col = Ui.VStack(8);
        switch (apo.Status)
        {
            case ApoStatus.NotInstalled:
                col.Children.Add(Ui.Text(L("Equalizer APO is not installed. MyOwnEQ uses it as the Windows audio engine (free, open source)."), 13, FontWeights.SemiBold));
                Ui.Add(col, Ui.Secondary(L("1. Install it  2. In its Configurator, tick the outputs you use  3. Reboot if it asks  4. Click Check again."), 12), 8, false);
                Ui.Add(col, Ui.HStack(8, Ui.Button(L("Download Equalizer APO"), ApoBackend.OpenDownloadPage, prominent: true, glyph: Ui.GDownload),
                                         Ui.Button(L("Check again"), c.ApoRedetect)), 8, false);
                break;
            case ApoStatus.NoWriteAccess:
                col.Children.Add(Ui.Text(L("Can't write to %@. Allow it once (Windows will ask for admin).", apo.ConfigPath ?? ""), 13, FontWeights.SemiBold));
                Ui.Add(col, Ui.HStack(8, Ui.Button(L("Allow"), c.ApoFixPermissions, prominent: true), Ui.Button(L("Check again"), c.ApoRedetect)), 8, false);
                break;
            case ApoStatus.NeedsSetup:
                col.Children.Add(Ui.Text(L("Equalizer APO found. Let MyOwnEQ manage it?"), 13, FontWeights.SemiBold));
                Ui.Add(col, Ui.Secondary(L("Your current config.txt is backed up as %@. “Take over” turns off other EQ (e.g. Peace) so you don't get double EQ; “Keep mine” adds MyOwnEQ on top.", ApoBackend.BackupFile), 12), 8, false);
                Ui.Add(col, Ui.HStack(8, Ui.Button(L("Take over"), () => c.ApoSetup(true), prominent: true),
                                         Ui.Button(L("Keep mine + add MyOwnEQ"), () => c.ApoSetup(false)),
                                         Ui.Button(L("Device selector…"), apo.OpenConfigurator)), 8, false);
                break;
        }
        if (c.Message is { } m) Ui.Add(col, Ui.Secondary(m, 12), 8, false);
        var head = Ui.HStack(8, Ui.Glyph(Ui.GWarning, 14, Theme.Frozen(Colors.Orange)), Ui.Caps(L("Windows audio engine")));
        head.Margin = new Thickness(0, 0, 0, 8);
        return Ui.Glass(Ui.VStack(0, head, col), highlighted: apo.Status != ApoStatus.Ready);
    }
}

/// <summary>Current output: name, default profile, volume, links, favorites, app rules.</summary>
public sealed class DeviceSettings : Page
{
    public DeviceSettings(AppController c) : base(c, L("Devices & Rules"), L("Default profile, favorites, linked headphones and app rules"), Ui.GHeadphones)
    {
        if (ApoSetupCard.Needed(c) is { } apo) { apo.Margin = new Thickness(0, 16, 0, 0); Body.Children.Add(apo); }
        var s = c.State;
        if (s?.CurrentGroup is not { } g)
        {
            Card(L("Device"), Ui.GHeadphones, Ui.Secondary(L("No output device"), 13));
            return;
        }

        var name = Field(g.Name, 300);
        name.LostFocus += (_, _) => { if (name.Text != g.Name) { g.Name = name.Text; c.UpdateGroup(g); } };
        var def = new ComboBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        def.Items.Add(new ComboBoxItem { Content = L("None (flat)"), Tag = null, IsSelected = g.DefaultProfile is null });
        foreach (var p in s.Library.Profiles) def.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id, IsSelected = p.Id == g.DefaultProfile });
        def.SelectionChanged += (_, _) => { if (def.SelectedItem is ComboBoxItem it) { g.DefaultProfile = it.Tag as string; c.UpdateGroup(g); } };
        var vol = new VolumeCard(c, compact: false) { Margin = new Thickness(0, 10, 0, 0) };
        Card(L("Device"), Ui.GHeadphones, Ui.VStack(6,
            Labeled(L("Name"), name),
            ToggleSwitch.Row(L("EQ enabled for this device"), g.Enabled, on => { g.Enabled = on; c.UpdateGroup(g); }),
            Labeled(L("Default profile"), def),
            Ui.Text(L("Volume & boost"), 13, FontWeights.SemiBold), vol));

        // Same headphones on other machines.
        var link = Ui.VStack(6);
        foreach (var m in g.Members)
        {
            bool mine = m.Key.Machine == s.Machine;
            var who = Ui.HStack(8, Ui.Glyph(Ui.GHeadphones, 13), Ui.Text(m.OsName, 13), Ui.Secondary(mine ? L("this PC") : m.Key.Machine, 11));
            var key = m.Key;
            link.Children.Add(g.Members.Count > 1 ? Ui.Spread(who, Ui.Button(L("Unlink"), () => c.Unlink(key))) : who);
        }
        var others = s.Library.Groups.Where(o => o.Id != g.Id && o.Members.Count > 0 && o.Members.All(m => m.Key.Machine != s.Machine)).ToList();
        if (others.Count > 0)
        {
            var pick = new ComboBox { Width = 300 };
            foreach (var o in others) pick.Items.Add(new ComboBoxItem { Content = $"{o.Name} ({o.Members[0].Key.Machine})", Tag = o.Id });
            Ui.Add(link, Ui.HStack(8, pick, Ui.Button(L("Link"), () => { if (pick.SelectedItem is ComboBoxItem { Tag: string id }) c.Link(id); }, glyph: Ui.GLink)), 6, false);
        }
        Ui.Add(link, Ui.Secondary(L("Linked devices share default profile, favorites, rules and the selected profile. Pick a profile here and the other machine switches too."), 11.5), 6, false);
        Card(L("Same headphones on other machines"), Ui.GLink, link);

        var favs = new WrapPanel();
        foreach (var p in s.Library.Profiles)
        {
            var id = p.Id;
            var row = ToggleSwitch.Row(p.Name, g.Favorites.Contains(p.Id), _ => c.ToggleFavorite(id));
            row.Width = 240;
            row.Margin = new Thickness(0, 2, 24, 2);
            favs.Children.Add(row);
        }
        Card(L("Favorites (shown in the tray panel while this device is connected)"), Ui.GStar, favs);

        var rules = Ui.VStack(6);
        foreach (var r in s.Library.Rules.Where(r => r.Group == g.Id)) Ui.Add(rules, RuleRow(r, s.Library.Profiles), 6, false);
        var apps = AppWatcher.Pickable();
        var appPick = new ComboBox { Width = 280 };
        foreach (var a in apps) appPick.Items.Add(new ComboBoxItem { Content = $"{a.Name} ({a.Exe})", Tag = a });
        Ui.Add(rules, Ui.HStack(8, appPick, Ui.Button(L("Add rule for app…"), () =>
        {
            if (appPick.SelectedItem is not ComboBoxItem { Tag: AppWatcher.App a } || s.Library.Profiles.Count == 0) return;
            c.UpsertRule(new Rule { Group = g.Id, App = new AppMatch { Name = a.Name, WinExe = a.Exe }, Profile = s.Library.Profiles[0].Id });
        }, glyph: Ui.GAdd)), 6, false);
        Ui.Add(rules, Ui.Secondary(L("When the app runs (or is focused) and this device is the output, its profile takes over. Rules made on the Mac need the Windows .exe filled in."), 11.5), 6, false);
        Card(L("App rules"), Ui.GSettings, rules);
    }

    private UIElement RuleRow(Rule r, System.Collections.Generic.List<Profile> profiles)
    {
        var en = new ToggleSwitch(r.Enabled) { Margin = new Thickness(0, 0, 10, 0) };
        en.Toggled += on => { r.Enabled = on; C.UpsertRule(r); };
        var exe = Field(r.App.WinExe ?? "", 130, L("Windows executable, e.g. cs2.exe"));
        exe.LostFocus += (_, _) =>
        {
            var v = exe.Text.Trim().ToLowerInvariant();
            if (v != (r.App.WinExe ?? "")) { r.App.WinExe = v.Length == 0 ? null : v; C.UpsertRule(r); }
        };
        var trig = new ComboBox { Margin = new Thickness(8, 0, 8, 0) };
        trig.Items.Add(new ComboBoxItem { Content = L("while running"), Tag = Trigger.Running, IsSelected = r.Trigger == Trigger.Running });
        trig.Items.Add(new ComboBoxItem { Content = L("while focused"), Tag = Trigger.Focused, IsSelected = r.Trigger == Trigger.Focused });
        trig.SelectionChanged += (_, _) => { if (trig.SelectedItem is ComboBoxItem { Tag: Trigger t }) { r.Trigger = t; C.UpsertRule(r); } };
        var prof = new ComboBox { Width = 160, Margin = new Thickness(8, 0, 8, 0) };
        foreach (var p in profiles) prof.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id, IsSelected = p.Id == r.Profile });
        prof.SelectionChanged += (_, _) => { if (prof.SelectedItem is ComboBoxItem { Tag: string id }) { r.Profile = id; C.UpsertRule(r); } };
        var name = Ui.Text(r.App.Name, 13, FontWeights.SemiBold);
        name.Margin = new Thickness(0, 0, 8, 0);
        var row = Ui.HStack(0, en, name, exe, trig, Ui.Glyph("", 12, Ui.T.InkBrush(0.5)), prof,
                            Ui.IconButton(Ui.GDelete, () => C.DeleteRule(r.Id), L("Delete"), Ui.T.InkBrush(0.6)));
        return Ui.Glass(row, radius: 10, padding: 8, shadow: false);
    }
}

/// <summary>Sync mode, server / LAN host, updates, this PC, Equalizer APO tools.</summary>
public sealed class SyncSettings : Page
{
    private static string? _mode;
    private static string _url = "", _token = "";
    private static int _port = 7878;

    public SyncSettings(AppController c) : base(c, L("Sync & Updates"), L("Keep your Mac and PC in step"), Ui.GSync)
    {
        var t = Ui.T;
        var info = c.State?.Sync ?? new SyncInfo();
        if (_mode is null) { _mode = info.Mode; _url = info.Url ?? ""; _token = info.Mode == "server" ? info.Token ?? "" : ""; _port = info.HostPort ?? 7878; }

        string status = (info.Mode, info.Status) switch
        {
            ("off", _) => L("Sync is off"),
            (_, "connected") => info.Mode == "host" ? L("Hosting · connected") : L("Connected"),
            (_, "connecting") => L("Connecting…"),
            (_, "error") => L("Problem"),
            _ => info.Status,
        };
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 10, Height = 10,
            Fill = info.Status switch { "connected" => Brushes.LimeGreen, "connecting" => Brushes.Gold, "error" => Brushes.Red, _ => Brushes.Gray },
        };
        var head = Ui.HStack(10, dot, Ui.Text(status, 15, FontWeights.SemiBold));
        if (info.Pending > 0) head.Children.Add(Ui.Secondary(info.Pending == 1 ? L("1 change waiting") : L("%@ changes waiting", info.Pending), 12));
        var statusCol = Ui.VStack(6, head);
        if (!string.IsNullOrEmpty(info.Detail)) Ui.Add(statusCol, Ui.Secondary(info.Detail, 11.5), 6, false);
        var mode = new PillPicker<string>(new[] { ("off", L("Off")), ("server", L("Connect to a server")), ("host", L("Host on this PC (LAN)")) }, _mode);
        mode.Changed += m => { _mode = m; Refresh(); };
        mode.Margin = new Thickness(0, 12, 0, 0);
        statusCol.Children.Add(mode);
        Card(L("Sync"), Ui.GSync, statusCol);

        if (_mode == "server")
        {
            var url = Field(_url, 360, "192.168.0.10:7878 / https://eq.example.com");
            url.TextChanged += (_, _) => _url = url.Text;
            var token = new PasswordBox { Password = _token, Width = 360, Padding = new Thickness(8, 5, 8, 5), HorizontalAlignment = HorizontalAlignment.Left };
            token.PasswordChanged += (_, _) => _token = token.Password;
            var col = Ui.VStack(6, Labeled(L("Address"), url), Labeled(L("Token / code"), token),
                Ui.HStack(8, Ui.Button(c.Discovering ? L("Searching…") : L("Find on my network"), c.Discover, glyph: Ui.GSearch),
                             Ui.Button(L("Connect"), () => c.ConfigureSync("server", _url, _token), prominent: true)));
            foreach (var h in c.Discovered)
            {
                var host = h;
                Ui.Add(col, Ui.Button($"{host.Name} — {host.Addr}", () => { _url = host.Addr; Refresh(); }, glyph: Ui.GHeadphones), 6, false);
            }
            Ui.Add(col, Ui.Secondary(L("Your Docker server (see server/docker-compose.yml), any cloud host running it, or another machine hosting on the LAN."), 11.5), 6, false);
            Card(L("Server"), Ui.GGlobe, col);
        }
        else if (_mode == "host")
        {
            var col = Ui.VStack(6);
            if (info.Mode == "host" && info.Token is { } code)
            {
                var codeBox = new TextBox { Text = code, IsReadOnly = true, FontSize = 22, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
                col.Children.Add(Labeled(L("Code"), codeBox));
                foreach (var a in info.HostAddresses ?? new()) col.Children.Add(Labeled(L("Address"), new TextBox { Text = a, IsReadOnly = true, Width = 220, HorizontalAlignment = HorizontalAlignment.Left }));
                col.Children.Add(Ui.Button(L("New code"), () => c.ConfigureSync("host", port: _port)));
            }
            else
            {
                var port = Field(_port.ToString(), 90);
                port.TextChanged += (_, _) => { if (int.TryParse(port.Text, out var p)) _port = p; };
                col.Children.Add(Ui.HStack(8, Labeled(L("Port %@", ""), port, 60), Ui.Button(L("Start hosting"), () => c.ConfigureSync("host", port: _port), prominent: true)));
            }
            Ui.Add(col, Ui.Secondary(L("On the other machine choose \"Connect to a server\", find this PC (or type the address) and enter the code. Windows Firewall will ask to allow MyOwnEQ on private networks."), 11.5), 6, false);
            Card(L("LAN host"), Ui.GHeadphones, col);
        }
        else if (info.Mode != "off")
        {
            Card(L("Sync"), Ui.GSync, Ui.Button(L("Turn sync off"), () => c.ConfigureSync("off")));
        }

        var u = c.Update;
        var upd = Ui.VStack(8,
            ToggleSwitch.Row(L("Download updates automatically, install on restart"), u?.Auto ?? true, on => c.SetAutoUpdate(on)),
            Ui.Spread(Ui.Secondary(UpdateLabel(u), 12),
                      u?.IsReady == true ? Ui.Button(L("Restart now"), () => { if (c.ApplyUpdate()) Application.Current.Shutdown(); }, prominent: true)
                                         : Ui.Button(L("Check now"), c.CheckForUpdates)),
            Labeled(L("Version"), Ui.Secondary(AppController.AppVersion, 12)));
        Card(L("Updates"), Ui.GDownload, upd);

        Card(L("This PC"), Ui.GInfo, Labeled(L("Machine id"), Ui.Secondary(c.State?.Machine ?? "", 12)));
        if (c.Apo.Status == ApoStatus.Ready)
            Card("Equalizer APO", Ui.GSettings, Ui.HStack(8, Ui.Button(L("Device selector…"), c.Apo.OpenConfigurator), Ui.Button(L("Restore my old config"), c.ApoRestore)));
    }

    private void Refresh()
    {
        if (Parent is ContentControl host) host.Content = new SyncSettings(C);
    }

    public static string UpdateLabel(UpdateInfo? u) => u?.State switch
    {
        "checking" => L("Checking for updates…"),
        "up_to_date" => L("Up to date"),
        "downloading" => L("Downloading %@…", u.Version ?? ""),
        "ready" => L("Version %@ is ready. Restart to install.", u.Version ?? ""),
        "error" => L("Update check failed: %@", u.Message ?? ""),
        _ => L("Updates idle"),
    };
}

/// <summary>Editing, language, themes, logo, motion, startup (AppearanceSettings in the Mac app).</summary>
public sealed class PreferencesPage : Page
{
    public PreferencesPage(AppController c) : base(c, L("Preferences"), L("Editing, language, themes, logo and motion"), Ui.GSettings)
    {
        var st = Ui.Store;
        var s = st.Settings;

        Card(L("Editing"), Ui.GEdit, Ui.VStack(6,
            ToggleSwitch.Row(L("Auto-save profile changes"), s.AutoSave, on => st.Update(x => x.AutoSave = on)),
            Ui.Secondary(s.AutoSave ? L("Every change is saved as you go.")
                                    : L("Changes wait for Save (Ctrl+S). You'll be asked before leaving a profile with unsaved changes."), 11.5),
            Ui.Secondary(L("Editing a preset always creates a copy, so presets never change."), 11.5)));

        var lang = new ComboBox { Width = 260 };
        lang.Items.Add(new ComboBoxItem { Content = L("Same as the system"), Tag = "", IsSelected = Ui.Lang.Choice is null });
        foreach (var p in Ui.Lang.Packs) lang.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Code, IsSelected = Ui.Lang.Choice == p.Code });
        lang.SelectionChanged += (_, _) =>
        {
            if (lang.SelectedItem is not ComboBoxItem { Tag: string code }) return;
            st.Settings.Language = code.Length == 0 ? null : code;
            st.Settings.Save();
            Ui.Lang.Select(st.Settings.Language);
        };
        var langCol = Ui.VStack(8,
            Ui.Spread(lang, Ui.HStack(8, Ui.Button(L("Open Languages Folder"), () => ThemeStore.OpenFolder("Lang")), Ui.Button(L("Reload"), Ui.Lang.Reload))),
            Ui.HStack(8, Ui.Secondary(L("Missing your language? Translate one file and send a PR."), 11.5), Link(L("How to translate"), "https://github.com/prnleg/MYOwnEQ/tree/main/lang")));
        foreach (var e in Ui.Lang.LoadErrors) Ui.Add(langCol, Ui.Text(e, 11, color: Brushes.Orange), 8, false);
        Card(L("Language"), Ui.GGlobe, langCol);

        var gallery = new WrapPanel();
        foreach (var th in st.Themes) gallery.Children.Add(ThemeCard(th, th.Id == st.Current.Id));
        var themeCol = Ui.VStack(10, gallery,
            Ui.Spread(Ui.HStack(8, Ui.Button(L("Open Themes Folder"), () => ThemeStore.OpenFolder("Themes")), Ui.Button(L("Reload"), st.Reload)),
                      Link(L("How to make a theme"), "https://github.com/prnleg/MYOwnEQ/tree/main/themes")));
        foreach (var e in st.LoadErrors) Ui.Add(themeCol, Ui.Text(e, 11, color: Brushes.Orange), 10, false);
        Card(L("Theme"), Ui.GPalette, themeCol);

        var logos = Ui.HStack(14);
        foreach (var style in new[] { LogoStyle.Wave, LogoStyle.Finger, LogoStyle.Custom }) logos.Children.Add(LogoTile(style));
        var logoCol = Ui.VStack(10, logos);
        if (st.CustomLogo is not null)
            logoCol.Children.Add(Ui.HStack(8, Ui.Button(L("Change Image…"), ChooseLogo), Ui.Button(L("Remove"), st.RemoveCustomLogo)));
        Ui.Add(logoCol, Ui.Secondary(L("Changes the tray icon and the logo inside the app. The .exe icon is the wave."), 11.5), 10, false);
        Card(L("Logo"), Ui.GPhoto, logoCol);

        bool reduce = !SystemParameters.ClientAreaAnimation;
        var motion = Ui.VStack(4,
            ToggleSwitch.Row(L("Smooth animations (springy curve, morphing, ripples)"), s.Motion, on => st.Update(x => x.Motion = on)),
            ToggleSwitch.Row(L("React to music (glow and background pulse)"), s.Reactive, on => st.Update(x => x.Reactive = on)),
            ToggleSwitch.Row(L("Shimmer along the curve"), s.Shimmer, on => st.Update(x => x.Shimmer = on), enabled: s.Motion));
        if (reduce) Ui.Add(motion, Ui.Secondary(L("Animation effects are off in Windows settings, so animations stay off."), 11.5), 4, false);
        Card(L("Motion"), Ui.GSparkle, motion);

        Card(L("Windows"), Ui.GSettings, ToggleSwitch.Row(L("Start MyOwnEQ with Windows"), Startup.Enabled, on => Startup.Enabled = on));
    }

    private static UIElement Link(string text, string url)
    {
        var tb = Ui.Text(text, 12, color: Theme.Frozen(Ui.T.AccentColor));
        tb.Cursor = Cursors.Hand;
        tb.TextDecorations = TextDecorations.Underline;
        tb.MouseLeftButtonUp += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        return tb;
    }

    private static UIElement ThemeCard(Theme th, bool selected)
    {
        var preview = new DrawLayer((dc, size) =>
        {
            dc.DrawRectangle(Theme.Frozen(th.BaseColor), null, new Rect(size));
            double[] xs = { 0.2, 0.8, 0.55, 0.1 }, ys = { 0.3, 0.2, 0.9, 0.85 };
            var blobs = th.BlobColors;
            for (int i = 0; i < blobs.Length; i++)
            {
                double r = size.Width * 0.55;
                dc.DrawEllipse(new RadialGradientBrush(Theme.WithAlpha(blobs[i], th.Background.Intensity), Theme.WithAlpha(blobs[i], 0)), null,
                               new Point(size.Width * xs[i % 4], size.Height * ys[i % 4]), r, r);
            }
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                for (int i = 0; i <= 48; i++)
                {
                    double x = i / 48.0;
                    var p = new Point(size.Width * (0.06 + 0.88 * x), size.Height * (0.55 - 0.32 * LogoMark.WaveY(x)));
                    if (i == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, true);
                }
            }
            var brush = th.CurveBrush();
            dc.DrawGeometry(null, new Pen(th.CurveBrush(0.35), 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
            dc.DrawGeometry(null, new Pen(brush, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
        }) { Height = 74 };
        var name = new TextBlock { Text = th.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = th.InkBrush(), FontFamily = Ui.Font };
        var author = new TextBlock { Text = th.Author ?? "", FontSize = 10.5, Foreground = th.InkBrush(0.55), FontFamily = Ui.Font };
        var foot = new Border { Background = Theme.Frozen(th.PanelColor), Padding = new Thickness(10, 7, 10, 8), Child = new StackPanel { Children = { name, author } } };
        if (selected)
        {
            var check = Ui.Glyph(Ui.GCheck, 14, Theme.Frozen(th.AccentColor));
            check.HorizontalAlignment = HorizontalAlignment.Right;
            foot.Child = new Grid { Children = { new StackPanel { Children = { name, author } }, check } };
        }
        var card = new Border
        {
            Width = 180, Margin = new Thickness(0, 0, 12, 12), CornerRadius = new CornerRadius(12), ClipToBounds = true, Cursor = Cursors.Hand,
            BorderThickness = new Thickness(selected ? 2.5 : 1),
            BorderBrush = selected ? th.CurveBrush() : Ui.T.InkBrush(0.12),
            Child = new StackPanel { Children = { preview, foot } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = selected ? 14 : 6, ShadowDepth = 2, Color = selected ? th.AccentColor : Colors.Black, Opacity = selected ? 0.5 : 0.2 },
        };
        card.MouseLeftButtonUp += (_, _) => Ui.Store.Select(th.Id);
        return card;
    }

    private UIElement LogoTile(LogoStyle style)
    {
        bool on = Ui.Store.Settings.Logo == style;
        var mark = new LogoMark(style) { Width = 70, Height = 70 };
        string label = style switch { LogoStyle.Wave => L("Wave"), LogoStyle.Finger => L("The Finger"), _ => L("Your own") };
        var col = Ui.VStack(8, mark, Ui.Text(label, 13, on ? FontWeights.Bold : FontWeights.Normal));
        foreach (FrameworkElement fe in col.Children) fe.HorizontalAlignment = HorizontalAlignment.Center;
        var tile = Ui.Glass(col, radius: 14, highlighted: on, padding: 12, shadow: false);
        tile.Opacity = on ? 1 : 0.7;
        tile.Cursor = Cursors.Hand;
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (style == LogoStyle.Custom && Ui.Store.CustomLogo is null) { ChooseLogo(); return; }
            Ui.Store.Update(s => s.Logo = style);
        };
        return tile;
    }

    private static void ChooseLogo()
    {
        var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*", Title = L("Choose an image for your logo (square, transparent PNG works best)") };
        if (dlg.ShowDialog() == true && !Ui.Store.SetCustomLogo(dlg.FileName))
            MessageBox.Show(L("That image couldn't be read."), "MyOwnEQ", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
