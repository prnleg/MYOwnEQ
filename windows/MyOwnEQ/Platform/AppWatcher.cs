using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MyOwnEQ.Platform;

/// <summary>Running processes + foreground app, for app rules (matched by exe name).</summary>
public sealed class AppWatcher : IDisposable
{
    public sealed record App(string Exe, string Name, bool Focused);

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private const uint EVENT_SYSTEM_FOREGROUND = 3;
    private const uint WINEVENT_OUTOFCONTEXT = 0;

    private readonly Action<List<App>> _onChange;
    private readonly WinEventProc _proc; // keep delegate alive
    private readonly IntPtr _hook;
    private readonly DispatcherTimer _timer;
    private List<App> _last = new();

    public AppWatcher(Action<List<App>> onChange)
    {
        _onChange = onChange;
        _proc = (_, _, _, _, _, _, _) => Fire();
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        // Launch/exit has no cheap event; poll.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Fire();
        _timer.Start();
    }

    public void Fire()
    {
        var snap = Snapshot();
        if (snap.SequenceEqual(_last)) return;
        _last = snap;
        _onChange(snap);
    }

    private static string? ForegroundExe()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant() + ".exe";
        }
        catch { return null; }
    }

    public static List<App> Snapshot()
    {
        var front = ForegroundExe();
        var seen = new HashSet<string>();
        var list = new List<App>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string exe = p.ProcessName.ToLowerInvariant() + ".exe";
                if (!seen.Add(exe)) continue;
                list.Add(new App(exe, p.ProcessName, exe == front));
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Exe, b.Exe));
        return list;
    }

    /// <summary>Apps with a visible main window, for the rule editor picker.</summary>
    public static List<App> Pickable()
    {
        var seen = new HashSet<string>();
        var list = new List<App>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    string exe = p.ProcessName.ToLowerInvariant() + ".exe";
                    if (!seen.Add(exe)) continue;
                    string title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.ProcessName;
                    list.Add(new App(exe, title, false));
                }
                catch { /* access denied for some system processes */ }
            }
        }
        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }
}
