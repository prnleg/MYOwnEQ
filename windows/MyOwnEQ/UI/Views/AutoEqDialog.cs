using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using static MyOwnEQ.UI.Loc;

namespace MyOwnEQ.UI;

/// <summary>
/// Search the AutoEQ headphone list (Mac: AutoEqSheet). With <c>target</c> the picked
/// correction goes into that profile; without it a new profile is made. Returns the
/// new profile's id (create mode) or null.
/// </summary>
// parity: autoeq-search
public static class AutoEqDialog
{
    public static string? Run(Window? owner, AppController c, Profile? target, Action? onApplied = null)
    {
        var t = Ui.T;
        string? created = null;
        var w = new Window
        {
            Title = "MyOwnEQ",
            Width = 560,
            Height = 600,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
            Background = Theme.Frozen(t.PanelColor),
        };

        var title = Ui.Text(L("Find your headphones"), 15, FontWeights.SemiBold);
        var hint = Ui.Secondary(L("AutoEQ corrections make headphones sound neutral. Your tone goes on top."), 12);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 4, 0, 12);

        var box = new TextBox
        {
            FontSize = 14, Padding = new Thickness(8, 6, 8, 6), Background = t.InkBrush(0.06),
            Foreground = t.InkBrush(), BorderThickness = new Thickness(0), CaretBrush = t.InkBrush(),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, L("Headphone model, e.g. HD 600"));
        box.ToolTip = L("Headphone model, e.g. HD 600");

        var status = new ContentControl { Margin = new Thickness(0, 8, 0, 8) };
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var source = Ui.Secondary(L("Data: AutoEQ project"), 11);
        source.Cursor = System.Windows.Input.Cursors.Hand;
        source.MouseLeftButtonUp += (_, _) => OpenUrl("https://github.com/jaakkopasanen/AutoEq");
        var fromFile = Ui.Button(L("From a file…"), () => { w.Close(); ImportDialog.Run(c, true); });
        var cancel = Ui.Button(L("Cancel"), w.Close);
        cancel.IsCancel = true;
        var buttons = Ui.Spread(source, Ui.HStack(8, fromFile, cancel));
        buttons.Margin = new Thickness(0, 12, 0, 0);

        var root = new DockPanel { Margin = new Thickness(22) };
        var head = new StackPanel { Children = { title, hint, box, status } };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(buttons);
        root.Children.Add(scroll);
        w.Content = root;

        var picked = new Dictionary<string, string>(); // model → measurement path
        string? busy = null;
        AutoEqSearchResult result = new();
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        var fetchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        (AutoEqModel model, string path)? fetching = null;

        void Render()
        {
            status.Content = result.State switch
            {
                "loading" => Ui.HStack(8, Ui.Spinner(), Ui.Secondary(L("Downloading the headphone list…"), 11.5)),
                "error" => Ui.HStack(8, Ui.Text(result.Error ?? L("Couldn't download the headphone list."), 11.5, color: Brushes.Orange),
                                     Ui.Button(L("Try again"), () => { c.AutoEqRefresh(); Search(); })),
                _ => Ui.Secondary(L("%@ headphones", result.Count.ToString("N0")), 11.5),
            };
            list.Children.Clear();
            if (result.Results.Count == 0 && result.State is "ready" or "updating" && box.Text.Trim().Length > 0)
            {
                var none = Ui.Secondary(L("No headphones match “%@”", box.Text.Trim()), 12.5);
                none.Margin = new Thickness(4, 12, 0, 0);
                list.Children.Add(none);
            }
            foreach (var m in result.Results) list.Children.Add(Row(m));
        }

        UIElement Row(AutoEqModel m)
        {
            string path = picked.TryGetValue(m.Name, out var p) ? p : m.Measurements.FirstOrDefault()?.Path ?? "";
            var name = Ui.Text(m.Name, 13, FontWeights.SemiBold);
            var meta = Ui.HStack(6, Ui.Tag(m.KindLabel.ToUpperInvariant(), Theme.WithAlpha(t.Ink, 0.6)));
            if (m.Measurements.Count > 1)
            {
                var combo = new ComboBox { FontSize = 11.5, MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
                foreach (var x in m.Measurements)
                    combo.Items.Add(new ComboBoxItem { Content = x.Label, Tag = x.Path, IsSelected = x.Path == path });
                combo.ToolTip = L("Who measured it. The first one is usually the most reliable.");
                combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: string s }) picked[m.Name] = s; };
                meta.Children.Add(combo);
            }
            else if (m.Measurements.FirstOrDefault() is { } only)
            {
                var l = Ui.Secondary(only.Label, 11.5);
                l.VerticalAlignment = VerticalAlignment.Center;
                meta.Children.Add(l);
            }
            UIElement action = busy is not null && busy == path
                ? Ui.Spinner()
                : Ui.Button(L("Use"), () => Use(m));
            if (action is Button b) b.IsEnabled = busy is null;
            ((FrameworkElement)action).VerticalAlignment = VerticalAlignment.Center;
            var row = Ui.Spread(Ui.VStack(4, name, meta), action);
            row.Margin = new Thickness(2, 6, 6, 6);
            return row;
        }

        void Search(string? device = null)
        {
            result = c.AutoEqSearch(box.Text, device);
            if (device is not null && result.Query.Length > 0 && box.Text.Length == 0)
            {
                box.Text = result.Query; // fires TextChanged → another (identical) search
                box.CaretIndex = box.Text.Length;
            }
            Render();
            poll.IsEnabled = result.State is "loading" or "updating";
        }

        void Use(AutoEqModel m)
        {
            if (busy is not null) return;
            string? path = picked.TryGetValue(m.Name, out var p) ? p : m.Measurements.FirstOrDefault()?.Path;
            if (path is null) return;
            busy = path;
            fetching = (m, path);
            Render();
            FetchStep();
        }

        void FetchStep()
        {
            if (fetching is not { } f) return;
            var r = c.AutoEqFetch(f.path, create: target is null);
            switch (r.State)
            {
                case "loading":
                    fetchTimer.Start();
                    return;
                case "ready" when target is null:
                    created = r.ImportedId;
                    c.Notify(L("Added “%@”", f.model.Name));
                    w.Close();
                    break;
                case "ready":
                    target!.Correction = r.Correction;
                    target.CorrectionSource = r.Source;
                    onApplied?.Invoke();
                    w.Close();
                    break;
                default:
                    c.Notify(L("Couldn't get the correction: %@", r.Error ?? "?"), error: true);
                    break;
            }
            fetching = null;
            busy = null;
            if (w.IsVisible) Render();
        }

        poll.Tick += (_, _) => Search();
        debounce.Tick += (_, _) => { debounce.Stop(); Search(); };
        fetchTimer.Tick += (_, _) => { fetchTimer.Stop(); FetchStep(); };
        box.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && result.Results.FirstOrDefault() is { } first) Use(first);
        };
        w.Closed += (_, _) => { poll.Stop(); debounce.Stop(); fetchTimer.Stop(); };
        w.SourceInitialized += (_, _) => Dwm.Style(w);
        w.Loaded += (_, _) =>
        {
            // First open: start from the current output's name ("Momentum 4 Headphones").
            Search(c.Current?.Name ?? "");
            debounce.Stop();
            box.Focus();
        };
        w.ShowDialog();
        return created;
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}

public sealed class AutoEqMeasurement
{
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class AutoEqModel
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<AutoEqMeasurement> Measurements { get; set; } = new();

    public string KindLabel => Kind switch
    {
        "over-ear" => L("Over-ear"),
        "in-ear" => L("In-ear"),
        "earbud" => L("Earbud"),
        _ => L("Headphones"),
    };
}

public sealed class AutoEqSearchResult
{
    public string Query { get; set; } = "";
    public string State { get; set; } = "loading";
    public int Count { get; set; }
    public string? Error { get; set; }
    public List<AutoEqModel> Results { get; set; } = new();
}

public sealed class AutoEqFetchResult
{
    public string State { get; set; } = "error";
    public string? Error { get; set; }
    public List<Band> Correction { get; set; } = new();
    public CorrectionSource? Source { get; set; }
    public string? ImportedId { get; set; }
}
