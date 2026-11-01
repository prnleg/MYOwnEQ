using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace MyOwnEQ.UI;

/// <summary>A color theme from themes/*.json — same schema as the Mac app (see themes/README.md).</summary>
public sealed class Theme
{
    public sealed class BackgroundSpec
    {
        public string Base { get; set; } = "#0E0B1A";
        public List<string> Blobs { get; set; } = new();
        public double Intensity { get; set; } = 0.4;
        public double Speed { get; set; } = 1;
    }

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Author { get; set; }
    public bool Dark { get; set; } = true;
    public string Accent { get; set; } = "#FF2E88";
    public List<string> Curve { get; set; } = new();
    public List<string> Spectrum { get; set; } = new();
    public BackgroundSpec Background { get; set; } = new();
    public string? Panel { get; set; }
    public double Glow { get; set; } = 0.8;

    public static readonly Theme Fallback = new()
    {
        Id = "neon-sunset", Name = "Neon Sunset", Author = "MyOwnEQ", Dark = true, Accent = "#FF2E88",
        Curve = new() { "#FF2E88", "#FF7A2E", "#FFD23F" }, Spectrum = new() { "#FF2E88", "#7B2CBF" },
        Background = new() { Base = "#0E0B1A", Blobs = new() { "#FF2E88", "#7B2CBF", "#FF7A2E", "#3A0CA3" }, Intensity = 0.45, Speed = 1 },
        Panel = "#1A1430", Glow = 0.9,
    };

    // ---- resolved colors (mirrors Theme.swift)

    public static Color Hex(string hex)
    {
        var s = hex.Trim().TrimStart('#');
        uint v = uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var x) ? x : 0;
        return s.Length == 8
            ? Color.FromArgb((byte)(v & 0xFF), (byte)(v >> 24), (byte)(v >> 16 & 0xFF), (byte)(v >> 8 & 0xFF))
            : Color.FromRgb((byte)(v >> 16 & 0xFF), (byte)(v >> 8 & 0xFF), (byte)(v & 0xFF));
    }

    public Color AccentColor => Hex(Accent);
    public Color[] CurveColors => Curve.Count >= 2 ? Curve.Select(Hex).ToArray() : new[] { AccentColor, AccentColor };
    public Color[] SpectrumColors => Spectrum.Count >= 2 ? Spectrum.Select(Hex).ToArray() : new[] { AccentColor, AccentColor };
    public Color BaseColor => Hex(Background.Base);
    public Color[] BlobColors => Background.Blobs.Select(Hex).ToArray();
    public Color PanelColor => Hex(Panel ?? Background.Base);
    /// <summary>Text / line color on this theme.</summary>
    public Color Ink => Dark ? Colors.White : Color.FromRgb(0x1B, 0x14, 0x23);

    /// <summary>Color along the curve gradient at t (0 = lows, 1 = highs).</summary>
    public Color CurveColor(double t)
    {
        var cs = CurveColors;
        double x = Math.Clamp(t, 0, 1) * (cs.Length - 1);
        int i = Math.Min((int)x, cs.Length - 2);
        return Mix(cs[i], cs[i + 1], x - i);
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    /// <summary>Horizontal gradient over the curve colors.</summary>
    public LinearGradientBrush CurveBrush(double opacity = 1)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        var cs = CurveColors;
        for (int i = 0; i < cs.Length; i++) b.GradientStops.Add(new GradientStop(WithAlpha(cs[i], opacity), i / (double)(cs.Length - 1)));
        b.Freeze();
        return b;
    }

    public LinearGradientBrush DiagonalBrush()
    {
        var b = CurveBrush().Clone();
        b.StartPoint = new Point(0, 0);
        b.EndPoint = new Point(1, 1);
        b.Freeze();
        return b;
    }

    public SolidColorBrush InkBrush(double opacity = 1) => Frozen(WithAlpha(Ink, opacity));
    public static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

public enum LogoStyle { Wave, Finger, Custom }

/// <summary>Local UI preferences (not synced), %APPDATA%\MyOwnEQ\ui.json.</summary>
public sealed class UiSettings
{
    public string Theme { get; set; } = "neon-sunset";
    public LogoStyle Logo { get; set; } = LogoStyle.Wave;
    public bool Motion { get; set; } = true;
    public bool Reactive { get; set; } = true;
    public bool Shimmer { get; set; } = true;
    public string? Language { get; set; }
    public bool AutoSave { get; set; } = true;
    public bool ShowPresets { get; set; } = true;
    public bool EditorPro { get; set; }
    public bool CustomLogoTemplate { get; set; }

    private static string Path => System.IO.Path.Combine(CoreBridge.DataDir, "ui.json");
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static UiSettings Load()
    {
        try { return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(Path), Opts) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* prefs are best effort */ }
    }
}

/// <summary>Theme list + current theme + motion prefs + custom logo. One per app (<see cref="Ui.Theme"/>).</summary>
// parity: themes
public sealed class ThemeStore
{
    public UiSettings Settings { get; } = UiSettings.Load();
    public List<Theme> Themes { get; private set; } = new();
    public Theme Current { get; private set; } = Theme.Fallback;
    public List<string> LoadErrors { get; private set; } = new();
    public ImageSource? CustomLogo { get; private set; }

    /// <summary>Theme, logo or motion prefs changed: rebuild visuals.</summary>
    public event Action? Changed;

    public ThemeStore(CoreBridge? core = null)
    {
        _core = core;
        CustomLogo = LoadCustomLogo();
        if (Settings.Logo == LogoStyle.Custom && CustomLogo is null) Settings.Logo = LogoStyle.Wave;
        Reload();
    }

    /// <summary>Motion on and Windows "Animation effects" not turned off.</summary>
    public bool MotionAllowed => Settings.Motion && SystemParameters.ClientAreaAnimation;

    public void Select(string id)
    {
        if (Themes.FirstOrDefault(t => t.Id == id) is not { } t) return;
        Current = t;
        Settings.Theme = id;
        Settings.Save();
        Apply();
    }

    public void Update(Action<UiSettings> change)
    {
        change(Settings);
        Settings.Save();
        Changed?.Invoke();
    }

    /// <summary>Parsing, validation and override rules live in the core (same for every app).</summary>
    private readonly CoreBridge? _core;

    public void Reload()
    {
        var themes = new List<Theme> { Theme.Fallback };
        var errors = new List<string>();
        if (_core is not null)
        {
            var r = _core.Command("load_themes", new { dirs = SearchDirs("Themes").ToList() });
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var node in r["themes"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
            {
                try
                {
                    var t = node.Deserialize<Theme>(opts);
                    if (t is null) continue;
                    int i = themes.FindIndex(x => x.Id == t.Id);
                    if (i >= 0) themes[i] = t; else themes.Add(t);
                }
                catch (Exception e) { errors.Add(e.Message); }
            }
            foreach (var e in r["errors"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray()) errors.Add(e?.GetValue<string>() ?? "");
        }
        Themes = themes;
        LoadErrors = errors;
        Current = themes.FirstOrDefault(t => t.Id == Settings.Theme) ?? themes.FirstOrDefault(t => t.Id == Current.Id) ?? Theme.Fallback;
        Apply();
    }

    private void Apply()
    {
        // Native Fluent controls (TextBox, ComboBox, menus) follow the theme's light/dark.
#pragma warning disable WPF0001
        if (Application.Current is { } app) app.ThemeMode = Current.Dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        Changed?.Invoke();
    }

    public static string UserDir(string kind) => System.IO.Path.Combine(CoreBridge.DataDir, kind);

    /// <summary>Bundled (next to the exe), repo checkout (dev runs), then user folder (wins ties).</summary>
    public static IEnumerable<string> SearchDirs(string kind)
    {
        yield return System.IO.Path.Combine(AppContext.BaseDirectory, kind);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var repo = System.IO.Path.Combine(dir.FullName, kind.ToLowerInvariant());
            if (File.Exists(System.IO.Path.Combine(repo, kind == "Themes" ? "neon-sunset.json" : "pt-BR.json"))) { yield return repo; break; }
        }
        yield return UserDir(kind);
    }

    public static void OpenFolder(string kind)
    {
        var d = UserDir(kind);
        Directory.CreateDirectory(d);
        Process.Start(new ProcessStartInfo { FileName = d, UseShellExecute = true });
    }

    // ---- custom logo

    public static string CustomLogoPath => System.IO.Path.Combine(CoreBridge.DataDir, "Logo", "custom.png");

    private static ImageSource? LoadCustomLogo()
    {
        try
        {
            if (!File.Exists(CustomLogoPath)) return null;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(CustomLogoPath);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Copy an image as a 512 px PNG and switch to it.</summary>
    // parity: custom-logo
    public bool SetCustomLogo(string file)
    {
        try
        {
            var src = new System.Windows.Media.Imaging.BitmapImage(new Uri(file));
            const int side = 512;
            double scale = Math.Min(side / src.Width, side / src.Height);
            double w = src.Width * scale, h = src.Height * scale;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect((side - w) / 2, (side - h) / 2, w, h));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CustomLogoPath)!);
            using (var fs = File.Create(CustomLogoPath)) enc.Save(fs);
            CustomLogo = LoadCustomLogo();
            Update(s => s.Logo = LogoStyle.Custom);
            return true;
        }
        catch { return false; }
    }

    public void RemoveCustomLogo()
    {
        try { File.Delete(CustomLogoPath); } catch { }
        CustomLogo = null;
        Update(s => { if (s.Logo == LogoStyle.Custom) s.Logo = LogoStyle.Wave; });
    }
}
