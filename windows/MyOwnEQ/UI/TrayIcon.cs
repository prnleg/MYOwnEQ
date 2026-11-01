using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using static MyOwnEQ.UI.Loc;

namespace MyOwnEQ.UI;

/// <summary>Notification-area icon. Left click: quick panel. Right click: menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly AppController _c;
    private readonly Action _openEditor, _quit;
    private Icon? _on, _off;

    public TrayIcon(AppController c, Action togglePanel, Action openEditor, Action quit)
    {
        _c = c;
        _openEditor = openEditor;
        _quit = quit;
        _icon = new NotifyIcon { Text = "MyOwnEQ", Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) togglePanel(); };
        Restyle();
        _c.Changed += Update;
        Ui.Store.Changed += Restyle;
        Ui.Lang.Changed += Restyle;
    }

    /// <summary>Logo (theme colors / custom image) and menu language.</summary>
    private void Restyle()
    {
        _on?.Dispose();
        _off?.Dispose();
        _on = Draw(true);
        _off = Draw(false);
        var menu = new ContextMenuStrip();
        menu.Items.Add(L("Open MyOwnEQ"), null, (_, _) => _openEditor());
        menu.Items.Add(L("EQ on/off"), null, (_, _) => _c.SetEnabled(!(_c.State?.Enabled ?? true)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L("Quit"), null, (_, _) => _quit());
        _icon.ContextMenuStrip?.Dispose();
        _icon.ContextMenuStrip = menu;
        Update();
    }

    private void Update()
    {
        bool on = _c.State?.Enabled ?? true;
        _icon.Icon = on ? _on : _off;
        string text = $"MyOwnEQ · {_c.ActiveProfile?.Name ?? L("Flat")}{(on ? "" : " · " + L("off"))}";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private static Icon Draw(bool on)
    {
        try { return LogoRenderer.TrayIcon(on); }
        catch { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
    }

    /// <summary>Once per install: tell the user where the icon lives (Windows 11
    /// hides new tray icons behind the ^ arrow).</summary>
    public void ShowFirstRunHint()
    {
        var flag = Path.Combine(CoreBridge.DataDir, "tray-hint-shown");
        if (File.Exists(flag)) return;
        _icon.ShowBalloonTip(8000, L("MyOwnEQ is running"),
            L("Find it in the taskbar corner (click ^ if hidden). Tip: drag the icon next to the clock to keep it visible."),
            ToolTipIcon.Info);
        try { File.WriteAllText(flag, DateTime.Now.ToString("o")); } catch { }
    }

    public void Dispose()
    {
        _c.Changed -= Update;
        Ui.Store.Changed -= Restyle;
        Ui.Lang.Changed -= Restyle;
        _icon.Visible = false;
        _icon.Dispose();
        _on?.Dispose();
        _off?.Dispose();
    }
}
