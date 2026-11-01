using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Threading;
using MyOwnEQ.Platform;

namespace MyOwnEQ;

/// <summary>Glue between the Rust core, Windows audio devices, app watcher and Equalizer APO.</summary>
public sealed class AppController : IDisposable
{
    public CoreBridge Core { get; }
    public ApoBackend Apo { get; } = new();
    public CoreState? State { get; private set; }
    public List<OutputDevice> Devices { get; private set; } = new();
    public OutputDevice? Current { get; private set; }
    public string? Message { get; set; }
    public List<FoundHost> Discovered { get; private set; } = new();
    public bool Discovering { get; private set; }

    /// <summary>Raised on the UI thread whenever anything visible changed.</summary>
    public event Action? Changed;

    private readonly Dispatcher _ui;
    private readonly AudioDevices _audio;
    private readonly AppWatcher _apps;
    private LoopbackAnalyzer? _loopback;
    private readonly DispatcherTimer _syncTimer;
    private readonly DispatcherTimer _apoDebounce;
    private readonly DispatcherTimer _saveDebounce;
    private Profile? _pendingSave;
    private readonly float[] _spectrum = new float[160];
    private readonly DispatcherTimer _updateTimer;
    public UpdateInfo? Update { get; private set; }

    public static string AppVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";

    public AppController(Dispatcher ui)
    {
        _ui = ui;
        Core = new CoreBridge(CoreBridge.DataDir, CoreBridge.MachineId());
        Reload();

        Apo.Detect();
        _audio = new AudioDevices(() => _ui.BeginInvoke(DevicesChanged));
        _audio.VolumeChanged += v => _ui.BeginInvoke(() =>
        {
            Core.Command("set_volume", new { volume = v });
            ScheduleApo();
        });
        _apps = new AppWatcher(AppsChanged);

        _apoDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _apoDebounce.Tick += (_, _) => { _apoDebounce.Stop(); WriteApo(); };
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            if (_pendingSave is { } p) { _pendingSave = null; Save(p); }
        };
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _syncTimer.Tick += (_, _) => SyncTick();
        _syncTimer.Start();

        UpdateReply(Core.Command("update_configure", new { version = AppVersion, platform = "windows" }));
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _updateTimer.Tick += (_, _) => UpdateReply(Core.Command("update_tick"));
        _updateTimer.Start();

        DevicesChanged();
        _apps.Fire();
    }

    // ---------------------------------------------------------------- updates

    private void UpdateReply(JsonObject r)
    {
        var info = Core.Decode<UpdateInfo>(r["update"]);
        if (info is null) return;
        bool changed = Update is null || info.State != Update.State || info.Version != Update.Version || info.Auto != Update.Auto;
        Update = info;
        if (changed) Changed?.Invoke();
    }

    /// <summary>A verified update from the last session is waiting and auto-install is on.</summary>
    public bool ShouldInstallOnStart => Update is { IsReady: true } && Update.Auto != false;

    public void CheckForUpdates()
    {
        UpdateReply(Core.Command("update_check"));
        foreach (var delay in new[] { 2, 6, 15, 40 })
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            t.Tick += (_, _) => { t.Stop(); UpdateReply(Core.Command("update_tick")); };
            t.Start();
        }
    }

    public void SetAutoUpdate(bool on) => UpdateReply(Core.Command("update_set_auto", new { auto = on }));

    /// <summary>Start the silent installer (it waits for us to exit, then relaunches us). True if started.</summary>
    public bool ApplyUpdate()
    {
        if (Update is not { IsReady: true }) return false;
        var r = Core.Command("update_apply");
        if (r["ok"]?.GetValue<bool>() == true) { Update = null; return true; }
        Message = r["error"]?.GetValue<string>();
        Changed?.Invoke();
        return false;
    }

    // ---------------------------------------------------------------- core plumbing

    private void Apply(JsonObject reply)
    {
        if (reply["ok"]?.GetValue<bool>() == false)
        {
            Message = reply["error"]?.GetValue<string>();
            Changed?.Invoke();
            return;
        }
        if (reply.ContainsKey("library"))
        {
            var s = Core.DecodeLast<CoreState>();
            if (s is not null) State = s;
            ScheduleApo();
            Changed?.Invoke();
        }
    }

    public void Reload() => Apply(Core.Command("get_state"));

    private void ScheduleApo()
    {
        _apoDebounce.Stop();
        _apoDebounce.Start();
    }

    private void WriteApo()
    {
        if (Apo.Status != ApoStatus.Ready) return;
        var text = Core.Command("apo_config")["text"]?.GetValue<string>();
        if (text is not null && !Apo.Write(text) && Apo.LastError is { } err)
        {
            Message = "Equalizer APO: " + err;
            Changed?.Invoke();
        }
    }

    // ---------------------------------------------------------------- devices

    private static object DeviceJson(OutputDevice d) => new { uid = d.Id, name = d.Name, kind = d.Kind };

    private void DevicesChanged()
    {
        Devices = _audio.Outputs();
        Core.Command("set_connected", new { devices = Devices.Select(DeviceJson).ToList() });
        var def = _audio.Default();
        if (def?.Id != Current?.Id)
        {
            Current = def;
            Core.Command("set_device", new { device = def is null ? null : DeviceJson(def) });
            RestartLoopback();
        }
        Reload();
    }

    private void RestartLoopback()
    {
        _loopback?.Dispose();
        _loopback = null;
        var mm = _audio.DefaultMMDevice();
        var vol = _audio.WatchVolume(mm);
        if (vol is { } v) Core.Command("set_volume", new { volume = v });
        if (mm is null) return;
        try { _loopback = new LoopbackAnalyzer(mm, Core); }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine("loopback: " + e.Message); }
    }

    private void AppsChanged(List<AppWatcher.App> apps)
    {
        var r = Core.Command("set_apps", new { apps = apps.Select(a => new { id = a.Exe, focused = a.Focused }).ToList() });
        if (r.ContainsKey("library")) Apply(r);
    }

    // ---------------------------------------------------------------- user actions

    public void SetEnabled(bool on) => Apply(Core.Command("set_enabled", new { enabled = on }));

    public void Select(string profileId)
    {
        if (State?.Group is { } g) Apply(Core.Command("select", new { group = g, profile = profileId }));
    }

    public void Save(Profile p) => Apply(Core.Command("upsert_profile", new { profile = p }));

    public void SaveDebounced(Profile p)
    {
        _pendingSave = p;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    public void FlushSave()
    {
        _saveDebounce.Stop();
        if (_pendingSave is { } p) { _pendingSave = null; Save(p); }
    }

    public void Delete(string id) => Apply(Core.Command("delete_profile", new { id }));

    /// <summary>Audition an unsaved profile on the current device (null stops).</summary>
    public void Preview(Profile? p)
    {
        Core.Command("preview", new Dictionary<string, object?> { ["profile"] = p });
        ScheduleApo();
    }

    public void UpdateGroup(DeviceGroup g) => Apply(Core.Command("update_group", new { group = g }));

    public void ToggleFavorite(string profileId)
    {
        if (State?.CurrentGroup is not { } g) return;
        if (!g.Favorites.Remove(profileId)) g.Favorites.Add(profileId);
        UpdateGroup(g);
    }

    public void UpsertRule(Rule r) => Apply(Core.Command("upsert_rule", new { rule = r }));
    public void DeleteRule(string id) => Apply(Core.Command("delete_rule", new { id }));

    public void ImportFile(string path, bool asCorrection)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (asCorrection && name.EndsWith(" ParametricEQ", StringComparison.Ordinal)) name = name[..^" ParametricEQ".Length];
        var r = Core.Command("import_apo", new { text = System.IO.File.ReadAllText(path), name, as_correction = asCorrection });
        Apply(r);
        if (r["warnings"] is JsonArray w && w.Count > 0) { Message = $"{name}: {w.Count} line(s) skipped"; Changed?.Invoke(); }
    }

    public string? ExportApo(string profileId) => Core.Command("export_apo", new { profile = profileId })["text"]?.GetValue<string>();

    public Curve? CurveFor(Profile p, int points = 256)
    {
        Core.Command("curve", new { profile = p, points });
        return Core.DecodeLast<Curve>();
    }

    public float[]? Spectrum() => Core.Spectrum(_spectrum) ? _spectrum : null;

    public void Link(string otherGroup)
    {
        if (State?.Group is { } g) Apply(Core.Command("link_groups", new { into = g, other = otherGroup }));
    }

    public void Unlink(DeviceKey key) => Apply(Core.Command("unlink_device", new { key }));

    // ---------------------------------------------------------------- Equalizer APO

    public void ApoSetup(bool takeOver)
    {
        try { Apo.Setup(takeOver); Message = null; }
        catch (Exception e) { Message = "Setup failed: " + e.Message; Apo.Detect(); }
        ScheduleApo();
        Changed?.Invoke();
    }

    public void ApoFixPermissions()
    {
        if (!Apo.FixPermissions()) Message = "Could not change permissions (UAC cancelled?)";
        Changed?.Invoke();
    }

    public void ApoRestore()
    {
        Message = Apo.Restore() ? "Restored your previous Equalizer APO config" : "No backup found";
        Changed?.Invoke();
    }

    public void ApoRedetect()
    {
        Apo.Detect();
        ScheduleApo();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- sync

    private void SyncTick()
    {
        if (State is null || State.Sync.Mode == "off") return;
        var r = Core.Command("sync_tick");
        if (r.ContainsKey("library")) { Apply(r); return; }
        var info = Core.Decode<SyncInfo>(r["sync"]);
        if (info is not null && (info.Status != State.Sync.Status || info.Pending != State.Sync.Pending || info.Detail != State.Sync.Detail))
        {
            State.Sync = info;
            Changed?.Invoke();
        }
    }

    public void ConfigureSync(string mode, string url = "", string token = "", int port = 7878)
    {
        object cfg = mode switch
        {
            "server" => new { mode, url = url.Trim(), token = token.Trim() },
            "host" => new { mode, port, token = string.IsNullOrEmpty(token) ? PairingCode() : token },
            _ => new { mode = "off" },
        };
        Apply(Core.Command("sync_configure", new { config = cfg }));
    }

    public async void Discover()
    {
        Discovering = true;
        Changed?.Invoke();
        Discovered = await Task.Run(() => CoreBridge.Discover());
        Discovering = false;
        Changed?.Invoke();
    }

    public static string PairingCode()
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        var chars = Enumerable.Range(0, 8).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]).ToArray();
        return new string(chars, 0, 4) + "-" + new string(chars, 4, 4);
    }

    // ---------------------------------------------------------------- derived

    public Profile? ActiveProfile => State?.ActiveProfile;

    public string Reason
    {
        get
        {
            if (State is null) return "";
            var r = State.Resolution;
            return r.Kind switch
            {
                "rule" => $"because {State.FindRule(r.Rule)?.App.Name ?? "an app"} is running",
                "rule_override" => $"manual pick while {State.FindRule(r.Rule)?.App.Name ?? "an app"} runs",
                "selected" => "selected",
                "default" => "device default",
                _ => "no profile (flat)",
            };
        }
    }

    public void Dispose()
    {
        _syncTimer.Stop();
        _updateTimer.Stop();
        _loopback?.Dispose();
        _apps.Dispose();
        _audio.Dispose();
        Core.Dispose();
    }
}
