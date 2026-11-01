using System;
using System.Linq;
using System.Threading;
using System.Windows;
using MyOwnEQ.UI;

namespace MyOwnEQ;

public partial class App : Application
{
    private Mutex? _single;
    private AppController? _controller;
    private TrayIcon? _tray;
    private QuickPanel? _panel;
    private EditorWindow? _editor;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\MyOwnEQ.SingleInstance", out bool first);
        if (!first)
        {
            Shutdown();
            return;
        }

        try
        {
            _controller = new AppController(Dispatcher);
        }
        catch (DllNotFoundException)
        {
            MessageBox.Show("myowneq_ffi.dll is missing next to MyOwnEQ.exe. Reinstall MyOwnEQ.", "MyOwnEQ", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // A verified update downloaded last session: this launch is the "restart".
        if (_controller.ShouldInstallOnStart && _controller.ApplyUpdate())
        {
            _controller.Dispose();
            Shutdown();
            return;
        }

        _panel = new QuickPanel(_controller, OpenEditor, Quit, RestartForUpdate);
        _tray = new TrayIcon(_controller, TogglePanel, OpenEditor, Quit);

        bool background = e.Args.Contains("--background");
        if (!background) OpenEditorIfFirstRun();
    }

    private void OpenEditorIfFirstRun()
    {
        // First run (or APO not ready): show the editor so the user sees the setup steps.
        if (_controller!.Apo.Status != Platform.ApoStatus.Ready) OpenEditor();
        else TogglePanel();
    }

    private void TogglePanel()
    {
        if (_panel is null) return;
        if (_panel.IsVisible) _panel.Hide();
        // The click that hid it via Deactivated shouldn't immediately reopen it.
        else if ((DateTime.UtcNow - _panel.HiddenAt).TotalMilliseconds > 300) _panel.ShowNearTray();
    }

    private void OpenEditor()
    {
        _panel?.Hide();
        if (_editor is null || !_editor.IsLoaded)
        {
            _editor = new EditorWindow(_controller!);
            _editor.Closed += (_, _) => _editor = null;
            _editor.Show();
        }
        if (_editor.WindowState == WindowState.Minimized) _editor.WindowState = WindowState.Normal;
        _editor.Activate();
    }

    private void RestartForUpdate()
    {
        if (_controller?.ApplyUpdate() != true) return;
        _editor?.Close();
        _tray?.Dispose();
        _controller.Dispose();
        Shutdown();
    }

    private void Quit()
    {
        // "Install on restart": quitting with a ready update installs it.
        if (_controller?.Update is { IsReady: true } u && u.Auto != false) _controller.ApplyUpdate();
        _editor?.Close();
        _tray?.Dispose();
        _controller?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
