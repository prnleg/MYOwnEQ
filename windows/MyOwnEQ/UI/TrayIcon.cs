using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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

    /// <summary>Three slider lines with knobs, drawn at runtime (no icon asset needed).</summary>
    private static Icon Draw(bool on)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var line = on ? Color.FromArgb(235, 235, 235) : Color.FromArgb(140, 140, 140);
            var knob = on ? Color.FromArgb(0, 120, 215) : Color.FromArgb(120, 120, 120);
            using var pen = new Pen(line, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var brush = new SolidBrush(knob);
            int[] ys = { 8, 16, 24 };
            int[] xs = { 20, 10, 16 };
            for (int i = 0; i < 3; i++)
            {
                g.DrawLine(pen, 4, ys[i], 28, ys[i]);
                g.FillEllipse(brush, xs[i] - 5, ys[i] - 5, 10, 10);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _c.Changed -= Update;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
