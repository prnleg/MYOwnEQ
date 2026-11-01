using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MyOwnEQ.UI;

namespace MyOwnEQ;

public partial class App : Application
{
    private const string MutexName = @"Local\MyOwnEQ.SingleInstance";
    /// <summary>Set by a second launch to ask the running copy to show itself.</summary>
    private const string ShowEventName = @"Local\MyOwnEQ.Show";

    private Mutex? _single;
    private EventWaitHandle? _showEvent;
    private AppController? _controller;
    private TrayIcon? _tray;
    private QuickPanel? _panel;
    private EditorWindow? _editor;

    public static string LogPath => Path.Combine(CoreBridge.DataDir, "logs", "myowneq.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ev) =>
        {
            Log("UI thread", ev.Exception);
            ShowError(ev.Exception);
            ev.Handled = true; // keep the EQ running; the error is logged
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log("background", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log("task", ev.Exception); ev.SetObserved(); };

        _single = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running (often hidden in the tray): bring it up instead of
            // silently quitting.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* old version running */ }
            Shutdown();
            return;
        }

        try
        {
            Start(e.Args);
        }
        catch (DllNotFoundException ex)
        {
            Log("startup", ex);
            MessageBox.Show("MyOwnEQ couldn't load its audio core (myowneq_ffi.dll). Reinstall MyOwnEQ.\n\n" + ex.Message +
                            "\n\nLog: " + LogPath, "MyOwnEQ", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
        catch (Exception ex)
        {
            Log("startup", ex);
            ShowError(ex);
            Shutdown(1);
        }
    }

    private void Start(string[] args)
    {
        _controller = new AppController(Dispatcher);

        // A verified update downloaded last session: this launch is the "restart".
        if (_controller.ShouldInstallOnStart && _controller.ApplyUpdate())
        {
            _controller.Dispose();
            Shutdown();
            return;
        }

        _panel = new QuickPanel(_controller, OpenEditor, Quit, RestartForUpdate);
        _tray = new TrayIcon(_controller, TogglePanel, OpenEditor, Quit);
        ListenForShow();

        bool background = args.Contains("--background");
        if (!background) OpenEditor(); // launched by the user: show the window
        _tray.ShowFirstRunHint();
    }

    /// <summary>Wait for a second launch's signal on a background thread.</summary>
    private void ListenForShow()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var t = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(OpenEditor);
            }
        }) { IsBackground = true, Name = "MyOwnEQ show listener" };
        t.Start();
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
        if (_controller is null) return;
        _panel?.Hide();
        if (_editor is null || !_editor.IsLoaded)
        {
            _editor = new EditorWindow(_controller);
            _editor.Closed += (_, _) => _editor = null;
            _editor.Show();
        }
        if (_editor.WindowState == WindowState.Minimized) _editor.WindowState = WindowState.Normal;
        _editor.Activate();
        _editor.Topmost = true; // pull in front of whatever launched us
        _editor.Topmost = false;
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
        _showEvent?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Append to %APPDATA%\MyOwnEQ\logs\myowneq.log (never throws).</summary>
    public static void Log(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {AppController.AppVersion} {where}: {ex}\n\n");
        }
        catch { /* logging must never crash the app */ }
    }

    private static void ShowError(Exception ex) =>
        MessageBox.Show($"MyOwnEQ hit an error:\n\n{ex.Message}\n\nDetails were saved to:\n{LogPath}\n\nPlease attach that file to a GitHub issue.",
                        "MyOwnEQ", MessageBoxButton.OK, MessageBoxImage.Error);
}
