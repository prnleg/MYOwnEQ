using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static MyOwnEQ.UI.Loc;

namespace MyOwnEQ.UI;

/// <summary>
/// Make or change a theme inside the app (Mac: ThemeEditor). The draft is shown live
/// everywhere (ThemeStore.Preview); Cancel puts the saved theme back.
/// </summary>
// parity: theme-editor
public static class ThemeEditor
{
    /// <summary>Built-in themes are copied (new id on save); user themes are edited in place.</summary>
    public static void Open(Window? owner, AppController c, Theme source)
    {
        bool existing = ThemeStore.IsUserTheme(source);
        var draft = Clone(source);
        if (!existing)
        {
            draft.Id = "";
            draft.Name = L("%@ (mine)", source.Name);
            draft.Author = null;
        }
        Run(owner, c, draft, existing);
    }

    private static Theme Clone(Theme t) =>
        System.Text.Json.JsonSerializer.Deserialize<Theme>(System.Text.Json.JsonSerializer.Serialize(t))!;

    private static void Run(Window? owner, AppController c, Theme draft, bool existing)
    {
        var st = Ui.Store;
        bool saved = false;
        var w = new Window
        {
            Title = "MyOwnEQ",
            Width = 640,
            Height = 720,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
            Background = Theme.Frozen(Ui.T.PanelColor),
        };

        var body = new StackPanel { Margin = new Thickness(22) };
        void Changed() => st.Preview(Clone(draft));

        Ui.Add(body, Ui.Text(existing ? L("Edit theme") : L("New theme"), 15, FontWeights.SemiBold), 0, false);

        var name = Field(draft.Name, v => { draft.Name = v; Changed(); });
        var author = Field(draft.Author ?? "", v => { draft.Author = v.Length == 0 ? null : v; Changed(); });
        var names = new Grid();
        names.ColumnDefinitions.Add(new ColumnDefinition());
        names.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        names.ColumnDefinitions.Add(new ColumnDefinition());
        var nameCol = Ui.VStack(4, Ui.Secondary(L("Name"), 11.5), name);
        var authorCol = Ui.VStack(4, Ui.Secondary(L("Author (optional)"), 11.5), author);
        Grid.SetColumn(authorCol, 2);
        names.Children.Add(nameCol);
        names.Children.Add(authorCol);
        Ui.Add(body, names, 14, false);

        var mode = new PillPicker<bool>(new[] { (true, L("Dark")), (false, L("Light")) }, draft.Dark) { HorizontalAlignment = HorizontalAlignment.Left };
        mode.Changed += v => { draft.Dark = v; Changed(); };
        Ui.Add(body, mode, 12, false);

        // Colors
        Ui.Add(body, Ui.Caps(L("Colors")), 18, false);
        Ui.Add(body, Ui.HStack(18,
            Swatch(w, L("Accent"), () => draft.Accent, v => { draft.Accent = v; Changed(); }),
            Swatch(w, L("Cards"), () => draft.Panel ?? draft.Background.Base, v => { draft.Panel = v; Changed(); }),
            Swatch(w, L("Background"), () => draft.Background.Base, v => { draft.Background.Base = v; Changed(); })), 10, false);
        Ui.Add(body, ColorList(w, L("EQ curve (lows → highs)"), draft.Curve, 2, 6, Changed), 10, false);
        Ui.Add(body, ColorList(w, L("Live spectrum (top → bottom)"), draft.Spectrum, 2, 2, Changed), 8, false);

        // Background
        Ui.Add(body, Ui.Caps(L("Background")), 18, false);
        Ui.Add(body, ColorList(w, L("Floating color spots"), draft.Background.Blobs, 0, 6, Changed), 10, false);
        Ui.Add(body, Slider(L("Spots strength"), 0, 1, draft.Background.Intensity, v => { draft.Background.Intensity = v; Changed(); }), 8, false);
        Ui.Add(body, Slider(L("Movement speed"), 0, 3, draft.Background.Speed, v => { draft.Background.Speed = v; Changed(); }), 8, false);
        Ui.Add(body, Slider(L("Glow around the curve"), 0, 1, draft.Glow, v => { draft.Glow = v; Changed(); }), 8, false);

        // Background image
        Ui.Add(body, Ui.Caps(L("Background image")), 18, false);
        var imageHost = new ContentControl();
        void BuildImage()
        {
            var col = Ui.VStack(8);
            bool has = draft.Background.Image is not null;
            var choose = Ui.Button(has ? L("Change image…") : L("Choose image…"), () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L("Images") + " (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", Title = L("Choose a background image") };
                if (dlg.ShowDialog(w) != true) return;
                if (ThemeStore.ImportBackgroundImage(dlg.FileName) is not { } file) { c.Notify(L("That image couldn't be read."), error: true); return; }
                draft.Background.Image = file;
                if (draft.Background.ImageOpacity >= 0.99) draft.Background.ImageOpacity = draft.Dark ? 0.55 : 0.7;
                Changed();
                BuildImage();
            });
            var row = Ui.HStack(8, choose);
            if (has) row.Children.Add(Ui.Button(L("Remove"), () => { draft.Background.Image = null; Changed(); BuildImage(); }));
            col.Children.Add(row);
            if (has)
            {
                Ui.Add(col, Slider(L("Image strength"), 0, 1, draft.Background.ImageOpacity, v => { draft.Background.ImageOpacity = v; Changed(); }), 4, false);
                Ui.Add(col, Slider(L("Blur"), 0, 40, draft.Background.ImageBlur, v => { draft.Background.ImageBlur = v; Changed(); }, whole: true), 4, false);
                var note = Ui.Secondary(L("Lower strength or more blur keeps text readable. The image isn't included in share codes."), 11.5);
                note.TextWrapping = TextWrapping.Wrap;
                col.Children.Add(note);
            }
            imageHost.Content = col;
        }
        BuildImage();
        Ui.Add(body, imageHost, 10, false);

        // Buttons
        var left = Ui.HStack(8);
        if (existing)
        {
            left.Children.Add(Ui.Button(L("Delete"), () =>
            {
                if (MessageBox.Show(w, L("Delete “%@”?", draft.Name), "MyOwnEQ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                st.Delete(draft);
                saved = true;
                w.Close();
            }, glyph: Ui.GDelete));
            left.Children.Add(Ui.Button(L("Share…"), () => Dialogs.ShareTheme(w, c, draft), glyph: Ui.GLink));
        }
        var cancel = Ui.Button(L("Cancel"), w.Close);
        cancel.IsCancel = true;
        var save = Ui.Button(L("Save"), () =>
        {
            if (draft.Name.Trim().Length == 0) draft.Name = L("My theme");
            if (st.Save(draft) is not null) { saved = true; w.Close(); }
            else c.Notify(st.LoadErrors.FirstOrDefault() ?? "?", error: true);
        }, prominent: true);
        save.IsDefault = true;
        var buttons = Ui.Spread(left, Ui.HStack(8, cancel, save));
        buttons.Margin = new Thickness(16, 12, 16, 14);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        w.Content = root;
        w.SourceInitialized += (_, _) => Dwm.Style(w);
        w.Loaded += (_, _) => Changed();
        w.Closed += (_, _) => { if (!saved) st.EndPreview(); };
        w.ShowDialog();
    }

    private static TextBox Field(string value, Action<string> changed)
    {
        var t = Ui.T;
        var box = new TextBox
        {
            Text = value, FontSize = 13, Padding = new Thickness(8, 5, 8, 5), Background = t.InkBrush(0.06),
            Foreground = t.InkBrush(), BorderThickness = new Thickness(0), CaretBrush = t.InkBrush(),
        };
        box.TextChanged += (_, _) => changed(box.Text);
        return box;
    }

    private static DockPanel Slider(string label, double min, double max, double value, Action<double> changed, bool whole = false) =>
        ProPanel.LabeledSlider(label, min, max, value, false, v => whole ? $"{v:0}" : $"{v:0.00}", changed, step: whole ? 1 : 0.01, labelWidth: 190);

    /// <summary>Color square that opens the Windows color dialog.</summary>
    private static UIElement Swatch(Window owner, string label, Func<string> get, Action<string> set)
    {
        var square = SwatchButton(owner, get, set);
        var text = Ui.Text(label, 13);
        text.VerticalAlignment = VerticalAlignment.Center;
        return Ui.HStack(6, square, text);
    }

    private static Border SwatchButton(Window owner, Func<string> get, Action<string> set)
    {
        var b = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1), BorderBrush = Ui.T.InkBrush(0.25),
            Background = Theme.Frozen(Theme.Hex(get())),
            ToolTip = get(),
        };
        b.MouseLeftButtonUp += (_, _) =>
        {
            var cur = Theme.Hex(get());
            using var dlg = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B),
            };
            var handle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            if (dlg.ShowDialog(new Win32Owner(handle)) != System.Windows.Forms.DialogResult.OK) return;
            var hex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            set(hex);
            b.Background = Theme.Frozen(Theme.Hex(hex));
            b.ToolTip = hex;
        };
        return b;
    }

    /// <summary>A row of colors with − / + (between <paramref name="min"/> and <paramref name="max"/>).</summary>
    private static UIElement ColorList(Window owner, string label, List<string> list, int min, int max, Action changed)
    {
        var host = new ContentControl();
        void Build()
        {
            var l = Ui.Text(label, 13);
            l.Width = 190;
            l.VerticalAlignment = VerticalAlignment.Center;
            var row = Ui.HStack(8, l);
            for (int i = 0; i < list.Count; i++)
            {
                int k = i;
                row.Children.Add(SwatchButton(owner, () => list[k], v => { list[k] = v; changed(); }));
            }
            if (max > min)
            {
                var minus = Ui.IconButton("", () => { if (list.Count > min) { list.RemoveAt(list.Count - 1); changed(); Build(); } }, L("Remove a color"), Ui.T.InkBrush(), 12);
                var plus = Ui.IconButton(Ui.GAdd, () => { if (list.Count < max) { list.Add(list.LastOrDefault() ?? "#FF2E88"); changed(); Build(); } }, L("Add a color"), Ui.T.InkBrush(), 12);
                minus.IsEnabled = list.Count > min;
                plus.IsEnabled = list.Count < max;
                row.Children.Add(minus);
                row.Children.Add(plus);
            }
            host.Content = row;
        }
        Build();
        return host;
    }

    private sealed class Win32Owner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
