using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace MyOwnEQ.UI;

/// <summary>Notification-area icon. Left click: quick panel. Right click: menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly AppController _c;
    private readonly Icon _on, _off;

    public TrayIcon(AppController c, Action togglePanel, Action openEditor, Action quit)
    {
        _c = c;
        _on = Draw(true);
        _off = Draw(false);
        _icon = new NotifyIcon { Icon = _on, Text = "MyOwnEQ", Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) togglePanel(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open MyOwnEQ", null, (_, _) => openEditor());
        menu.Items.Add("EQ on/off", null, (_, _) => _c.SetEnabled(!(_c.State?.Enabled ?? true)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());
        _icon.ContextMenuStrip = menu;
        _c.Changed += Update;
        Update();
    }

    private void Update()
    {
        bool on = _c.State?.Enabled ?? true;
        _icon.Icon = on ? _on : _off;
        string text = $"MyOwnEQ · {_c.ActiveProfile?.Name ?? "Flat"}{(on ? "" : " (off)")}";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>The app logo (Assets/MyOwnEQ.ico) at tray size; grayscale when off.</summary>
    private static Icon Draw(bool on)
    {
        var size = SystemInformation.SmallIconSize;
        Icon logo;
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/MyOwnEQ.ico"));
            using var stream = res!.Stream;
            logo = new Icon(stream, size);
        }
        catch
        {
            logo = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        }
        if (on) return logo;
        using var color = logo.ToBitmap();
        using var gray = new Bitmap(color.Width, color.Height);
        using (var g = Graphics.FromImage(gray))
        {
            var m = new ColorMatrix(new[]
            {
                new[] { .3f, .3f, .3f, 0f, 0f },
                new[] { .59f, .59f, .59f, 0f, 0f },
                new[] { .11f, .11f, .11f, 0f, 0f },
                new[] { 0f, 0f, 0f, .6f, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f },
            });
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(m);
            g.DrawImage(color, new Rectangle(0, 0, gray.Width, gray.Height), 0, 0, color.Width, color.Height, GraphicsUnit.Pixel, attrs);
        }
        logo.Dispose();
        return Icon.FromHandle(gray.GetHicon());
    }

    /// <summary>Once per install: tell the user where the icon lives (Windows 11
    /// hides new tray icons behind the ^ arrow).</summary>
    public void ShowFirstRunHint()
    {
        var flag = Path.Combine(CoreBridge.DataDir, "tray-hint-shown");
        if (File.Exists(flag)) return;
        _icon.ShowBalloonTip(8000, "MyOwnEQ is running",
            "Find it in the taskbar corner (click ^ if hidden). Tip: drag the icon next to the clock to keep it visible.",
            ToolTipIcon.Info);
        try { File.WriteAllText(flag, DateTime.Now.ToString("o")); } catch { }
    }

    public void Dispose()
    {
        _c.Changed -= Update;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
