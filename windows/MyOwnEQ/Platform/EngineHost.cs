using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MyOwnEQ.Platform;

/// <summary>
/// The MyOwnEQ engine: our Rust DSP (EQ + lookahead limiter + boost) as a plugin that
/// Equalizer APO loads per output. No admin needed: everything lives in
/// <c>%ProgramData%\MyOwnEQ</c>, which users may create and the audio service can read.
/// <list type="bullet">
/// <item><c>engine\MyOwnEQEngine-&lt;ver&gt;.dll</c> — the plugin (versioned: a loaded DLL is locked)</item>
/// <item><c>engine.json</c> — per-output parameters, rewritten on every change (no APO reload)</item>
/// <item><c>engine-status-&lt;output&gt;.json</c> — heartbeat written by the plugin</item>
/// </list>
/// </summary>
public sealed class EngineHost
{
    public const string ShippedDll = "myowneq_engine.dll";

    public static string Dir =>
        Environment.GetEnvironmentVariable("MYOWNEQ_ENGINE_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyOwnEQ");

    private string? _lastParams;
    public string? LastError { get; private set; }

    /// <summary>The plugin shipped with this app version (next to the exe).</summary>
    public static string? Shipped => File.Exists(Path.Combine(AppContext.BaseDirectory, ShippedDll))
        ? Path.Combine(AppContext.BaseDirectory, ShippedDll) : null;

    /// <summary>Copy the shipped plugin into ProgramData (once per app version) and
    /// return the path APO should load; null if it can't be installed.</summary>
    public string? Install(string version)
    {
        if (Shipped is not { } src) { LastError = "engine plugin missing from the app folder"; return null; }
        try
        {
            var dir = Path.Combine(Dir, "engine");
            Directory.CreateDirectory(dir);
            var safe = new string(version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-').ToArray());
            var dst = Path.Combine(dir, $"MyOwnEQEngine-{(safe.Length > 0 ? safe : "dev")}.dll");
            if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length)
            {
                var tmp = dst + ".tmp";
                File.Copy(src, tmp, overwrite: true);
                File.Move(tmp, dst, overwrite: true);
            }
            // Older versions: delete the ones no longer loaded (locked ones stay until next time).
            foreach (var old in Directory.GetFiles(dir, "MyOwnEQEngine-*.dll").Where(f => !f.Equals(dst, StringComparison.OrdinalIgnoreCase)))
                try { File.Delete(old); } catch { }
            LastError = null;
            return dst;
        }
        catch (Exception e) { LastError = e.Message; return null; }
    }

    /// <summary>Write per-output parameters if they changed (atomic replace).</summary>
    public bool WriteParams(string json)
    {
        if (json == _lastParams) return true;
        try
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, "engine.json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            _lastParams = json;
            return true;
        }
        catch (Exception e) { LastError = e.Message; return false; }
    }

    /// <summary>Output id as used in APO's <c>Device:</c> lines ("{guid}"), same rule as the core.</summary>
    public static string Selector(string endpointId)
    {
        int i = endpointId.LastIndexOf("}.", StringComparison.Ordinal);
        return i >= 0 ? endpointId[(i + 2)..] : endpointId;
    }

    /// <summary>Delete <see cref="Dir"/> (uninstall). The audio service may keep the
    /// plugin loaded for a moment after APO's config changes, so retry for a few
    /// seconds; whatever is still locked is harmless once APO no longer references it.</summary>
    public static bool RemoveAll(int waitMs = 5000)
    {
        var deadline = Environment.TickCount64 + waitMs;
        while (true)
        {
            try
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
                return true;
            }
            catch when (Environment.TickCount64 < deadline) { System.Threading.Thread.Sleep(250); }
            catch { return false; }
        }
    }

    public enum State { Off, Waiting, Loaded, Processing }

    /// <summary>What the plugin reports for this output: loaded (heartbeat fresh) and
    /// processing (audio went through in the last few seconds).</summary>
    public static State Status(string endpointId)
    {
        var id = new string(Selector(endpointId).Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        var file = Path.Combine(Dir, $"engine-status-{id}.json");
        try
        {
            if (!File.Exists(file)) return State.Waiting;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long written = doc.RootElement.TryGetProperty("written_ms", out var w) ? w.GetInt64() : 0;
            long processed = doc.RootElement.TryGetProperty("last_process_ms", out var p) ? p.GetInt64() : 0;
            if (now - written > 4000) return State.Waiting;
            return now - processed < 4000 ? State.Processing : State.Loaded;
        }
        catch { return State.Waiting; }
    }
}
