using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MyOwnEQ.Platform;
using static MyOwnEQ.UI.Loc;

namespace MyOwnEQ.UI;

/// <summary>
/// Edits a draft copy of a profile (ProfileEditor in the Mac app). Only the active profile
/// is heard while editing; any other is heard only while "Hold to listen" is held.
/// Editing a preset turns the draft into "Name (mine)". Auto-save on: saved as you go;
/// off: Save/Discard bar and a prompt before leaving.
/// </summary>
public sealed class ProfileEditor : ScrollViewer
{
    private readonly AppController _c;
    private readonly Action<string> _onFork;
    private Profile _draft;
    private string _savedJson;
    private bool _holding;
    private int? _selectedBand;
    private string? _lastActiveId;

    private CurveView? _curve;
    private TextBox? _nameBox;
    private UIElement? _pencil, _presetNote;
    private readonly ContentControl _unsavedHost = new(), _actionsHost = new(), _tagsHost = new(), _panelHost = new();

    public ProfileEditor(AppController c, Profile original, Action<string> onFork)
    {
        _c = c;
        _onFork = onFork;
        _draft = Json.Clone(original);
        _savedJson = Ser(_draft);
        _lastActiveId = c.ActiveProfile?.Id;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Rebuild();
    }

    public string ProfileId => _draft.Id;
    public string DraftName => _draft.Name;
    private bool AutoSave => Ui.Store.Settings.AutoSave;
    private bool Dirty => Ser(_draft) != _savedJson;
    private bool IsActive => _c.ActiveProfile?.Id == _draft.Id;
    private bool IsPreset => _draft.Tags.Contains("builtin");
    /// <summary>Leaving needs a "save changes?" prompt.</summary>
    public bool NeedsAsk => !AutoSave && Dirty;

    private static string Ser(Profile p) => JsonSerializer.Serialize(p, Json.Options);

    // ------------------------------------------------------------ build

    public void Rebuild()
    {
        var t = Ui.T;
        var col = new StackPanel { Margin = new Thickness(24, 20, 24, 28) };

        // Name + pencil, tags; Easy/Pro on the right.
        var name = new TextBox
        {
            Text = _draft.Name, FontSize = 26, FontWeight = FontWeights.Bold, FontFamily = Ui.Display,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = t.InkBrush(),
            IsReadOnly = IsPreset, Padding = new Thickness(0), MinWidth = 120, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = IsPreset ? L("Presets can't be renamed. Change anything and a copy is made for you.") : L("Rename"),
        };
        name.TextChanged += (_, _) => { if (name.Text != _draft.Name) { _draft.Name = name.Text; Edited(); } };
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
        _nameBox = name;
        _pencil = Ui.IconButton(Ui.GEdit, () => { name.Focus(); name.SelectAll(); }, L("Rename"), t.InkBrush(0.55), 16);
        _pencil.Visibility = IsPreset ? Visibility.Collapsed : Visibility.Visible;
        var nameRow = Ui.HStack(8, name, _pencil);
        var pill = new PillPicker<bool>(new[] { (false, L("Easy")), (true, L("Pro")) }, Ui.Store.Settings.EditorPro);
        pill.Changed += pro => { Ui.Store.Settings.EditorPro = pro; Ui.Store.Settings.Save(); BuildPanel(animate: true); };
        pill.VerticalAlignment = VerticalAlignment.Center;
        var left = Ui.VStack(4, nameRow, _tagsHost);
        col.Children.Add(Ui.Spread(left, pill));

        if (IsPreset)
        {
            var note = Ui.HStack(6, Ui.Glyph(Ui.GDoc, 12, t.InkBrush(0.55)),
                Ui.Secondary(L("This is a preset. Change anything and a copy is created for you; the preset stays untouched."), 12));
            note.Margin = new Thickness(0, 10, 0, 0);
            _presetNote = note;
            col.Children.Add(note);
        }
        _unsavedHost.Margin = new Thickness(0, 12, 0, 0);
        col.Children.Add(_unsavedHost);
        _actionsHost.Margin = new Thickness(0, 12, 0, 0);
        col.Children.Add(_actionsHost);

        if (ApoSetupCard.Needed(_c) is { } apo)
        {
            apo.Margin = new Thickness(0, 14, 0, 0);
            col.Children.Add(apo);
        }

        // Curve card.
        _curve = new CurveView(_c, _draft, Ui.Store.Settings.EditorPro ? CurveView.Edit.Pro : CurveView.Edit.Easy) { Height = 300, Selected = _selectedBand };
        _curve.ProfileEdited += () => Edited();
        _curve.SelectionChanged += i => { _selectedBand = i; if (_panelHost.Content is ProPanel pp) pp.Select(i); };
        var hint = Ui.Secondary(Hint(), 11);
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        hint.VerticalAlignment = VerticalAlignment.Bottom;
        hint.Margin = new Thickness(0, 0, 12, 8);
        hint.IsHitTestVisible = false;
        var curveGrid = new Grid { Children = { _curve, hint } };
        // No shadow here: an effect over 60 fps content forces a full repaint every frame.
        var card = Ui.Glass(curveGrid, radius: 16, padding: 0, shadow: false);
        card.ClipToBounds = true;
        curveGrid.Clip = null;
        card.Margin = new Thickness(0, 14, 0, 0);
        col.Children.Add(card);

        _panelHost.Margin = new Thickness(0, 16, 0, 0);
        col.Children.Add(_panelHost);
        Content = col;

        RefreshStatus();
        BuildPanel(animate: false);
    }

    private string Hint()
    {
        if (!Ui.Store.Settings.EditorPro) return L("Drag the dots: Bass, Mids, Clarity, Treble");
        if (_draft.Eq is GraphicLayer) return L("Drag the sliders up and down on the graph");
        return L("Drag the dots · Shift-drag or wheel changes Q · double-click adds a band");
    }

    private void BuildPanel(bool animate)
    {
        bool pro = Ui.Store.Settings.EditorPro;
        FrameworkElement panel = pro
            ? new ProPanel(_c, _draft, _selectedBand, structural => Edited(structural), i => { _selectedBand = i; if (_curve is not null) _curve.Selected = i; })
            : new EasyPanel(_c, _draft, () => Edited());
        _panelHost.Content = panel;
        if (animate)
        {
            // Mode switch also changes what the curve lets you drag.
            Rebuild();
            Ui.Appear((FrameworkElement)_panelHost.Content);
        }
    }

    /// <summary>Tags, Use Now / In use, hold-to-listen, unsaved bar.</summary>
    private void RefreshStatus()
    {
        var t = Ui.T;
        var tags = Ui.HStack(6);
        if (IsActive) tags.Children.Add(Ui.Tag(L("PLAYING"), t.AccentColor));
        if (IsPreset) tags.Children.Add(Ui.Tag(L("PRESET"), Theme.WithAlpha(t.Ink, 0.6)));
        if (_draft.CorrectionSource is { } src) tags.Children.Add(Ui.Tag("AUTOEQ · " + src.Name.ToUpperInvariant(), Theme.WithAlpha(t.Ink, 0.6)));
        _tagsHost.Content = tags;

        UIElement right = IsActive
            ? Ui.HStack(6, Ui.Glyph(Ui.GCheck, 13, Theme.Frozen(t.AccentColor)), Ui.Text(L("In use"), 13, FontWeights.SemiBold, color: Theme.Frozen(t.AccentColor)))
            : Ui.Button(L("Use Now"), () => { if (Dirty) Commit(); _c.Select(_draft.Id); }, prominent: true);
        _actionsHost.Content = IsActive ? Ui.Spread(new Border(), right) : Ui.Spread(HoldToListen(), right);

        if (NeedsAsk)
        {
            var bar = Ui.Spread(
                Ui.HStack(8, Ui.Glyph(Ui.GEdit, 13, Theme.Frozen(t.AccentColor)), Ui.Text(L("Unsaved changes"), 13, FontWeights.SemiBold, color: Theme.Frozen(t.AccentColor))),
                Ui.HStack(8, Ui.Button(L("Discard"), Discard), Ui.Button(L("Save"), Commit, prominent: true)));
            if (_unsavedHost.Content is null) { _unsavedHost.Content = Ui.Glass(bar, radius: 12, highlighted: true, padding: 10, shadow: false); Ui.Appear((FrameworkElement)_unsavedHost.Content); }
        }
        else _unsavedHost.Content = null;
    }

    private UIElement HoldToListen()
    {
        var t = Ui.T;
        var label = Ui.Text(L("Hold to listen"), 13, FontWeights.SemiBold);
        var icon = Ui.Glyph(Ui.GEar, 13);
        var b = new Border
        {
            CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 7, 14, 7), Cursor = Cursors.Hand,
            Background = t.InkBrush(0.08), BorderBrush = t.InkBrush(0.12), BorderThickness = new Thickness(1),
            Child = Ui.HStack(6, icon, label), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = L("Hear this profile while you hold the button. Use Now switches to it for real."),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        void Set(bool on)
        {
            if (_holding == on) return;
            _holding = on;
            _c.Preview(on ? _draft : null);
            b.Background = on ? t.CurveBrush() : t.InkBrush(0.08);
            label.Text = on ? L("Listening…") : L("Hold to listen");
            label.Foreground = icon.Foreground = on ? Brushes.White : t.InkBrush();
            var st = (ScaleTransform)b.RenderTransform;
            st.ScaleX = st.ScaleY = on ? 0.97 : 1;
            b.Effect = on ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 0, Color = t.AccentColor, Opacity = 0.7 } : null;
        }
        b.MouseLeftButtonDown += (_, e) => { b.CaptureMouse(); Set(true); e.Handled = true; };
        b.MouseLeftButtonUp += (_, _) => { b.ReleaseMouseCapture(); Set(false); };
        b.LostMouseCapture += (_, _) => Set(false);
        return b;
    }

    // ------------------------------------------------------------ edits

    /// <summary>Draft changed (from the curve, panels or name). structural = rebuild the panel.</summary>
    private void Edited(bool structural = false)
    {
        bool wasActive = IsActive;
        ForkIfPreset(wasActive);
        if (IsActive || _holding) _c.Preview(_draft);
        if (AutoSave)
        {
            _c.SaveDebounced(Json.Clone(_draft));
            _savedJson = Ser(_draft);
        }
        _curve?.Invalidate();
        if (structural) { _curve?.SetProfile(_draft); BuildPanel(animate: false); }
        RefreshStatus();
    }

    /// <summary>Presets are read-only (the core ignores writes): the first edit makes a copy.</summary>
    private void ForkIfPreset(bool wasActive)
    {
        if (!IsPreset) return;
        var original = _c.State?.FindProfile(_draft.Id);
        _draft.Id = Profile.NewId();
        _draft.Name = L("%@ (mine)", original?.Name ?? _draft.Name);
        _draft.Tags.Remove("builtin");
        _c.Save(Json.Clone(_draft));
        _savedJson = Ser(_draft);
        if (wasActive) _c.Select(_draft.Id);
        _onFork(_draft.Id);
        // Update in place (no rebuild: a drag on the curve may be in progress).
        if (_nameBox is not null) { _nameBox.IsReadOnly = false; _nameBox.Text = _draft.Name; _nameBox.ToolTip = L("Rename"); }
        if (_pencil is not null) _pencil.Visibility = Visibility.Visible;
        if (_presetNote is not null) _presetNote.Visibility = Visibility.Collapsed;
    }

    public void Commit()
    {
        if (!Dirty) return;
        _c.Save(Json.Clone(_draft));
        _savedJson = Ser(_draft);
        RefreshStatus();
    }

    public void Discard()
    {
        _draft = JsonSerializer.Deserialize<Profile>(_savedJson, Json.Options)!;
        if (IsActive || _holding) _c.Preview(_draft);
        Rebuild();
    }

    /// <summary>Controller state changed: refresh status; take outside changes (sidebar
    /// rename, sync from the other machine) unless we have our own pending edits.</summary>
    public void OnStateChanged()
    {
        var active = _c.ActiveProfile?.Id;
        if (active != _lastActiveId)
        {
            if (active != _draft.Id && !_holding) _c.Preview(null);
            _lastActiveId = active;
        }
        if (_c.State?.FindProfile(_draft.Id) is { } latest && !Dirty && _c.PendingSave?.Id != _draft.Id && !IsKeyboardFocusWithin && Mouse.LeftButton != MouseButtonState.Pressed)
        {
            var json = Ser(latest);
            if (json != _savedJson)
            {
                _draft = latest;
                _savedJson = json;
                Rebuild();
                return;
            }
        }
        RefreshStatus();
    }

    /// <summary>Leaving the profile: write pending edits (auto-save) and stop any audition.</summary>
    public void Close()
    {
        if (AutoSave) _c.FlushSave();
        if (_holding) _holding = false;
        _c.Preview(null);
    }
}

/// <summary>Bass / Mids / Clarity / Treble, headphone correction, comfort (EasyPanel).</summary>
public sealed class EasyPanel : StackPanel
{
    public EasyPanel(AppController c, Profile p, Action edited)
    {
        var tone = Ui.VStack(10);
        void Row(string name, string hint, Func<Macros, double> get, Action<Macros, double> set)
        {
            var value = Ui.Text(Ui.Db(get(p.Macros)), 13, FontWeights.Medium);
            value.Width = 66;
            value.TextAlignment = TextAlignment.Right;
            var s = new GlowSlider(-12, 12, get(p.Macros)) { Step = 0.5 };
            s.ValueChanged += v => { set(p.Macros, v); value.Text = Ui.Db(v); edited(); };
            var label = Ui.VStack(0, Ui.Text(name, 13, FontWeights.SemiBold), Ui.Secondary(hint, 11));
            label.Width = 110;
            var row = new DockPanel();
            DockPanel.SetDock(label, Dock.Left);
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(label);
            row.Children.Add(value);
            row.Children.Add(s);
            Ui.Add(tone, row, 10, horizontal: false);
        }
        Row(L("Bass"), L("105 Hz shelf"), m => m.Bass, (m, v) => m.Bass = v);
        Row(L("Mids"), "1 kHz", m => m.Mids, (m, v) => m.Mids = v);
        Row(L("Clarity"), L("3 kHz presence"), m => m.Clarity, (m, v) => m.Clarity = v);
        Row(L("Treble"), L("8 kHz shelf"), m => m.Treble, (m, v) => m.Treble = v);
        Children.Add(Ui.Card(L("Tone"), Ui.GDial, tone));

        UIElement corr;
        if (p.CorrectionSource is { } src)
            corr = Ui.VStack(6, Ui.Text($"{src.Provider}: {src.Name}", 13, FontWeights.SemiBold),
                               Ui.Secondary(L("%@ filters, applied before your tone", p.Correction.Count)),
                               Ui.Button(L("Remove"), () => { p.Correction.Clear(); p.CorrectionSource = null; edited(); }));
        else
            corr = Ui.VStack(8, Ui.Secondary(L("Import an AutoEQ ParametricEQ.txt to make your headphones sound neutral."), 12.5),
                               Ui.Button(L("Import AutoEQ…"), () => ImportDialog.Run(c, true)));
        foreach (FrameworkElement fe in ((Panel)corr).Children) fe.HorizontalAlignment = HorizontalAlignment.Left;

        var loudVal = Ui.Text(p.Loudness == 0 ? L("off") : $"{p.Loudness * 100:0}%", 12);
        loudVal.Width = 40;
        loudVal.TextAlignment = TextAlignment.Right;
        var loud = new GlowSlider(0, 1, p.Loudness) { Bipolar = false };
        loud.ValueChanged += v => { p.Loudness = v; loudVal.Text = v == 0 ? L("off") : $"{v * 100:0}%"; edited(); };
        var loudLabel = Ui.Text(L("Loudness"), 13);
        loudLabel.Width = 76;
        var loudRow = new DockPanel();
        DockPanel.SetDock(loudLabel, Dock.Left);
        DockPanel.SetDock(loudVal, Dock.Right);
        loudRow.Children.Add(loudLabel);
        loudRow.Children.Add(loudVal);
        loudRow.Children.Add(loud);
        var comfort = Ui.VStack(8, loudRow, ToggleSwitch.Row(L("Clipping protection"), p.Limiter, on => { p.Limiter = on; edited(); }));

        var two = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        two.ColumnDefinitions.Add(new ColumnDefinition());
        two.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        two.ColumnDefinitions.Add(new ColumnDefinition());
        var a = Ui.Card(L("Headphone correction"), Ui.GCheck, corr);
        var b = Ui.Card(L("Comfort"), Ui.GEar, comfort);
        Grid.SetColumn(b, 2);
        two.Children.Add(a);
        two.Children.Add(b);
        Children.Add(two);
    }
}

/// <summary>Open-file dialog for AutoEQ / Equalizer APO imports.</summary>
public static class ImportDialog
{
    public static void Run(AppController c, bool asCorrection)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Text (*.txt)|*.txt|All files|*.*",
            Multiselect = true,
            Title = asCorrection ? L("Choose AutoEQ ParametricEQ.txt files") : L("Choose Equalizer APO / Peace config files"),
        };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames) c.ImportFile(f, asCorrection);
    }
}
