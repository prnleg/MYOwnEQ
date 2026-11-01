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
    private MainWindow? _editor;
    /// <summary>`--selftest`: start everything, open the windows, exit 0/1. Used by CI.</summary>
    private bool _selfTest;

    public static string LogPath => Path.Combine(CoreBridge.DataDir, "logs", "myowneq.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _selfTest = e.Args.Contains("--selftest");
        DispatcherUnhandledException += (_, ev) =>
        {
            Log("UI thread", ev.Exception);
            ev.Handled = true; // keep the EQ running; the error is logged
            if (_selfTest) Shutdown(1); else ShowError(ev.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log("background", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log("task", ev.Exception); ev.SetObserved(); };

        if (_selfTest)
        {
            RunSelfTest();
            return;
        }

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
        Ui.Init(_controller.Core); // themes, language, UI prefs (loaded by the core)

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

    /// <summary>Start everything, visit every page (Easy + Pro editor, Devices, Sync,
    /// Preferences, tray panel), save a screenshot of each to logs/selftest-*.png and exit
    /// 0, or 1 on any error. Runs in CI with a throwaway data folder.</summary>
    // parity: selftest
    private void RunSelfTest()
    {
        try
        {
            Start(new[] { "--background" });
            OpenEditor();
            var c = _controller!;
            var profiles = c.State?.Library.Profiles ?? new();
            var preset = profiles.FirstOrDefault(p => p.Name == "FPS") ?? profiles.FirstOrDefault();
            var steps = new System.Collections.Generic.List<(string name, Action act)>();
            if (preset is not null)
            {
                steps.Add(("01-easy", () => { Ui.Store.Settings.EditorPro = false; _editor!.Navigate(Nav.Preferences); _editor.Navigate(Nav.Profile(preset.Id)); }));
                steps.Add(("02-pro", () => { Ui.Store.Settings.EditorPro = true; _editor!.Navigate(Nav.Devices); _editor.Navigate(Nav.Profile(preset.Id)); }));
            }
            steps.Add(("03-devices", () => _editor!.Navigate(Nav.Devices)));
            steps.Add(("04-sync", () => _editor!.Navigate(Nav.Sync)));
            steps.Add(("05-preferences", () => _editor!.Navigate(Nav.Preferences)));
            steps.Add(("06-light-theme", () => Ui.Store.Select("peach-daylight")));
            steps.Add(("07-pt-br", () => { Ui.Store.Select("neon-sunset"); Ui.Lang.Select("pt-BR"); if (preset is not null) _editor!.Navigate(Nav.Profile(preset.Id)); }));
            steps.Add(("08-tray-panel", () => { Ui.Lang.Select(null); _panel!.ShowNearTray(); }));
            int k = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += (_, _) =>
            {
                try
                {
                    if (k > 0) Snapshot(steps[k - 1].name, k - 1 == steps.Count - 1 ? _panel : _editor);
                    if (k < steps.Count) { steps[k].act(); k++; return; }
                    t.Stop();
                    _editor?.Close();
                    _panel?.Hide();
                    _tray?.Dispose();
                    _controller?.Dispose();
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    t.Stop();
                    Log("selftest " + (k > 0 ? steps[k - 1].name : "start"), ex);
                    Shutdown(1);
                }
            };
            t.Start();
        }
        catch (Exception ex)
        {
            Log("selftest", ex);
            Shutdown(1);
        }
    }

    private static void Snapshot(string name, Window? w)
    {
        if (w?.Content is not FrameworkElement root || root.ActualWidth <= 0) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
            System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        var dir = Path.GetDirectoryName(LogPath)!;
        Directory.CreateDirectory(dir);
        using var fs = File.Create(Path.Combine(dir, $"selftest-{name}.png"));
        enc.Save(fs);
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
            _editor = new MainWindow(_controller);
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
