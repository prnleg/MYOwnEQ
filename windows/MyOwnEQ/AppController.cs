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
    /// <summary>Toast message; clears itself after a few seconds (see <see cref="Notify"/>).</summary>
    public (string Text, bool IsError)? Notice { get; private set; }
    private DispatcherTimer? _noticeTimer;
    /// <summary>Shared constants from the core (graphic frequencies, knob spots…).</summary>
    public UiConstants? Constants { get; private set; }
    private readonly DispatcherTimer _apoWatch;
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
    /// <summary>Master volume of the current output (0…1), null when unknown.</summary>
    public double? Volume { get; private set; }
    /// <summary>Volume moved (keys, mixer, our slider). Separate from <see cref="Changed"/>
    /// so a volume drag doesn't rebuild the UI under the mouse.</summary>
    public event Action<double>? VolumeUpdated;

    public static string AppVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";

    public AppController(Dispatcher ui)
    {
        _ui = ui;
        Core = new CoreBridge(CoreBridge.DataDir, CoreBridge.MachineId());
        Core.Command("ui_constants");
        Constants = Core.DecodeLast<UiConstants>();
        // Who we are for the other machines in sync ("connected machines" list).
        Core.Command("set_app_info", new { name = Environment.MachineName, platform = "windows", version = AppVersion });
        // Timers first: Reload() → Apply() → ScheduleApo() uses them.
        _apoDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _apoDebounce.Tick += (_, _) => { _apoDebounce.Stop(); WriteApo(); };
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            if (_pendingSave is { } p) { _pendingSave = null; Save(p); }
        };
        Reload();

        Apo.Detect();
        _audio = new AudioDevices(() => _ui.BeginInvoke(DevicesChanged));
        _audio.VolumeChanged += v => _ui.BeginInvoke(() =>
        {
            Core.Command("set_volume", new { volume = v });
            Volume = v;
            ScheduleApo();
            VolumeUpdated?.Invoke(v);
        });
        _apps = new AppWatcher(AppsChanged);

        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _syncTimer.Tick += (_, _) => SyncTick();
        _syncTimer.Start();

        // Equalizer APO gets installed/configured outside the app: look again
        // every 30 s until it's ready, so the setup card goes away by itself.
        _apoWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _apoWatch.Tick += (_, _) => { if (Apo.Status != ApoStatus.Ready || ApoOnCurrent == ApoEndpoint.NotEnabled) ApoRedetect(); };
        _apoWatch.Start();

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

    /// <summary>Check now: the core answers "checking" right away; poll quickly while it
    /// works so the spinner is live, then say how it went.</summary>
    public void CheckForUpdates()
    {
        UpdateReply(Core.Command("update_check"));
        int n = 0;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        t.Tick += (_, _) =>
        {
            UpdateReply(Core.Command("update_tick"));
            if (Update is { IsBusy: true } && ++n < 600) return; // up to 3 min (downloads)
            t.Stop();
            switch (Update?.State)
            {
                case "up_to_date": Notify(UI.Loc.L("Up to date")); break;
                case "ready": Notify(UI.Loc.L("Version %@ is ready. Restart to install.", Update.Version ?? "")); break;
                case "error": Notify(UI.Loc.L("Update check failed: %@", Update.Message ?? ""), error: true); break;
            }
        };
        t.Start();
    }

    public void SetAutoUpdate(bool on) => UpdateReply(Core.Command("update_set_auto", new { auto = on }));

    /// <summary>Start the silent installer (it waits for us to exit, then relaunches us). True if started.</summary>
    public bool ApplyUpdate()
    {
        if (Update is not { IsReady: true }) return false;
        var r = Core.Command("update_apply");
        if (r["ok"]?.GetValue<bool>() == true) { Update = null; return true; }
        Notify(r["error"]?.GetValue<string>() ?? "?", error: true);
        return false;
    }

    // ---------------------------------------------------------------- core plumbing

    private void Apply(JsonObject reply)
    {
        if (reply["ok"]?.GetValue<bool>() == false)
        {
            Notify(reply["error"]?.GetValue<string>() ?? "?", error: true);
            return;
        }
        if (reply.ContainsKey("library"))
        {
            var s = Core.DecodeLast<CoreState>();
            if (s is not null) State = s;
            // Never fail silently: an empty UI here looks like lost data.
            else Notify(UI.Loc.L("Couldn't read the profile library (%@). Please report this bug.", Core.LastDecodeError ?? "?"), error: true);
            ScheduleApo();
            Changed?.Invoke();
        }
    }

    public void Reload() => Apply(Core.Command("get_state"));

    private void ScheduleApo()
    {
        if (_apoDebounce is null) return; // during construction
        _apoDebounce.Stop();
        _apoDebounce.Start();
    }

    private void WriteApo()
    {
        if (Apo.Status != ApoStatus.Ready) return;
        string? text = null;
        if (EngineMode)
        {
            // Engine: parameters go to engine.json (no APO reload); APO's include file only
            // changes when outputs or the plugin version change.
            _engineDll ??= Engine.Install(AppVersion);
            if (_engineDll is null)
            {
                SetEngineMode(false);
                Notify(UI.Loc.L("The MyOwnEQ engine couldn't start (%@). Using Equalizer APO's filters.", Engine.LastError ?? "?"), error: true);
            }
            else
            {
                Engine.WriteParams(Core.Command("engine_config")["text"]?.GetValue<string>() ?? "");
                text = Core.Command("apo_config", new { engine_dll = _engineDll })["text"]?.GetValue<string>();
            }
        }
        text ??= Core.Command("apo_config")["text"]?.GetValue<string>();
        if (text is not null && !Apo.Write(text) && Apo.LastError is { } err)
        {
            Notify("Equalizer APO: " + err, error: true);
        }
    }

    // ---------------------------------------------------------------- MyOwnEQ engine (our DSP inside APO)

    public EngineHost Engine { get; } = new();
    /// <summary>Our DSP runs inside Equalizer APO (boost + limiter). Opt-in for now.</summary>
    public bool EngineMode { get; private set; }
    /// <summary>Persist the choice (set by the UI layer).</summary>
    public Action<bool>? EngineModeChanged { get; set; }
    private string? _engineDll;
    private DispatcherTimer? _engineWatch;
    private int _engineSilentTicks;
    private EngineHost.State _lastEngineState;

    // parity: engine
    public void SetEngineMode(bool on)
    {
        if (EngineMode == on) return;
        EngineMode = on;
        _engineDll = null;
        _engineSilentTicks = 0;
        EngineModeChanged?.Invoke(on);
        if (on)
        {
            _engineWatch ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _engineWatch.Tick -= EngineTick;
            _engineWatch.Tick += EngineTick;
            _engineWatch.Start();
        }
        else _engineWatch?.Stop();
        ScheduleApo();
        Changed?.Invoke();
    }

    /// <summary>Engine state for the output in use (Off when engine mode is off).</summary>
    public EngineHost.State EngineState =>
        !EngineMode || Current is null ? EngineHost.State.Off : EngineHost.Status(Current.Id);

    /// <summary>Watchdog: if audio is playing but the engine isn't processing it for
    /// ~10 s, go back to Equalizer APO's own filters so the user never loses the EQ.</summary>
    private void EngineTick(object? sender, EventArgs e)
    {
        var st = EngineState;
        if (st != _lastEngineState) { _lastEngineState = st; Changed?.Invoke(); }
        bool audio = Spectrum() is { } s && s.Max() > -70;
        bool apoOnOutput = ApoOnCurrent != ApoEndpoint.NotEnabled;
        if (audio && apoOnOutput && st != EngineHost.State.Processing) _engineSilentTicks++;
        else _engineSilentTicks = 0;
        if (_engineSilentTicks >= 5)
        {
            SetEngineMode(false);
            Notify(UI.Loc.L("The MyOwnEQ engine isn't processing audio on this output. Switched back to Equalizer APO's filters."), error: true);
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
        Volume = vol;
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

    /// <summary>A debounced save not written yet (the editor won't adopt outside changes over it).</summary>
    public Profile? PendingSave => _pendingSave;

    /// <summary>Rename a profile (presets can't be renamed).</summary>
    public void Rename(string id, string name)
    {
        var n = name.Trim();
        if (n.Length == 0 || State?.FindProfile(id) is not { } p || p.Tags.Contains("builtin")) return;
        var copy = Json.Clone(p);
        copy.Name = n;
        Save(copy);
    }

    /// <summary>Change the system volume of the current output.</summary>
    public void SetVolume(double v)
    {
        if (!_audio.SetVolume(v)) return;
        Volume = v;
        Core.Command("set_volume", new { volume = v });
    }

    /// <summary>Set the current device's boost (dB). Saved and synced; not applied by
    /// Equalizer APO (no limiter), but kept so the Mac side keeps it.</summary>
    public void SetBoost(double db)
    {
        if (State?.CurrentGroup is not { } g) return;
        g.BoostDb = Math.Clamp(db, 0, 12);
        UpdateGroup(g);
    }

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
        if (r["warnings"] is JsonArray w && w.Count > 0) Notify(UI.Loc.L("%@: %@ lines skipped", name, w.Count));
    }

    public string? ExportApo(string profileId) => Core.Command("export_apo", new { profile = profileId })["text"]?.GetValue<string>();

    public Curve? CurveFor(Profile p, int points = 256)
    {
        Core.Command("curve", new { profile = p, points });
        return Core.DecodeLast<Curve>();
    }

    public float[]? Spectrum() => Core.Spectrum(_spectrum) ? _spectrum : null;

    /// <summary>Link the current device with the same headphones on another machine; says so.</summary>
    // parity: link-feedback
    public void Link(string otherGroup)
    {
        if (State?.Group is not { } g || State.FindGroup(otherGroup) is not { } o) return;
        string who = $"{o.Name} · {MachineName(o.Members.FirstOrDefault()?.Key.Machine ?? "")}";
        var r = Core.Command("link_groups", new { into = g, other = otherGroup });
        Apply(r);
        if (r["ok"]?.GetValue<bool>() != false) Notify(UI.Loc.L("Linked with %@. Profile and favorites now follow on both.", who));
    }

    public void Unlink(DeviceKey key)
    {
        var name = State?.CurrentGroup?.Members.FirstOrDefault(m => m.Key.Equals(key))?.OsName ?? key.Uid;
        var r = Core.Command("unlink_device", new { key });
        Apply(r);
        if (r["ok"]?.GetValue<bool>() != false) Notify(UI.Loc.L("Unlinked %@.", $"{name} · {MachineName(key.Machine)}"));
    }

    // ---------------------------------------------------------------- Equalizer APO

    public void ApoSetup(bool takeOver)
    {
        try { Apo.Setup(takeOver); }
        catch (Exception e) { Notify(UI.Loc.L("Setup failed: %@", e.Message), error: true); Apo.Detect(); }
        ScheduleApo();
        Changed?.Invoke();
    }

    public void ApoFixPermissions()
    {
        if (!Apo.FixPermissions()) Notify(UI.Loc.L("Could not change permissions (UAC cancelled?)"), error: true);
        Changed?.Invoke();
    }

    public void ApoRestore()
    {
        Notify(Apo.Restore() ? UI.Loc.L("Restored your previous Equalizer APO config") : UI.Loc.L("No backup found"));
        Changed?.Invoke();
    }

    /// <summary>Is Equalizer APO actually enabled on the output in use right now?</summary>
    public ApoEndpoint ApoOnCurrent =>
        Apo.Status != ApoStatus.Ready || Current is null ? ApoEndpoint.Unknown : ApoBackend.CheckEndpoint(Current.Id);

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
        if (info is not null && (info.Status != State.Sync.Status || info.Pending != State.Sync.Pending || info.Detail != State.Sync.Detail
                                 || PeersKey(info) != PeersKey(State.Sync)))
        {
            State.Sync = info;
            Changed?.Invoke();
        }
    }

    private static string PeersKey(SyncInfo s) =>
        string.Join("|", (s.Peers ?? new()).Select(p => $"{p.Machine}:{p.Online}:{p.LastSeen}:{p.Name}"));

    public void ConfigureSync(string mode, string url = "", string token = "", int port = 7878)
    {
        object cfg = mode switch
        {
            "server" => new { mode, url = url.Trim(), token = token.Trim() },
            "host" => new { mode, port, token }, // empty: the core makes a pairing code
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


    // ---------------------------------------------------------------- notices

    /// <summary>Show a toast; it clears itself (errors stay longer).</summary>
    public void Notify(string text, bool error = false)
    {
        Notice = (text, error);
        _noticeTimer?.Stop();
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 12 : 6) };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); Notice = null; Changed?.Invoke(); };
        _noticeTimer.Start();
        Changed?.Invoke();
    }

    public void DismissNotice() { Notice = null; Changed?.Invoke(); }

    // ---------------------------------------------------------------- shared editing logic (in the core)

    /// <summary>Edited preset → new profile "Name (mine)" (presets are read-only). Returns it.</summary>
    public Profile? ForkPreset(Profile draft, string originalName)
    {
        var r = Core.Command("fork_preset", new { draft, name = UI.Loc.L("%@ (mine)", originalName) });
        Apply(r);
        return Core.Decode<Profile>(r["profile"]);
    }

    /// <summary>Switch EQ layout keeping the sound ("parametric" | "graphic10" | "graphic31").</summary>
    public EqLayer? ConvertEq(EqLayer eq, string to, string profileId) =>
        // Anonymous type (not object/dictionary): keeps EqLayer's "type" discriminator.
        Core.Decode<EqLayer>(Core.Command("convert_eq", new { profile = profileId, eq, to })["eq"]);

    /// <summary>Where a new band should go (null when it doesn't fit).</summary>
    public Band? SuggestBand(EqLayer eq) =>
        Core.Decode<Band>(Core.Command("suggest_band", new { eq })["band"]);

    /// <summary>Friendly name of a machine id (from the sync's machine list).</summary>
    public string MachineName(string id) =>
        id == State?.Machine ? Environment.MachineName : State?.Sync.Peers?.FirstOrDefault(p => p.Machine == id)?.DisplayName ?? id;

    // ---------------------------------------------------------------- share codes

    /// <summary>Share code (MEQ1-…) and QR rows ("1" = dark) for a profile.</summary>
    // parity: share-codes
    public (string Code, List<string> Qr)? ShareCode(string profileId)
    {
        var r = Core.Command("share_code", new { profile = profileId });
        if (r["code"]?.GetValue<string>() is not { } code) { Apply(r); return null; }
        var qr = r["qr"]?.AsArray().Select(x => x?.GetValue<string>() ?? "").ToList() ?? new();
        return (code, qr);
    }

    /// <summary>Import the first MEQ1-… code on the clipboard; returns the new profile id.</summary>
    public string? ImportCodeFromClipboard()
    {
        string text = "";
        try { text = System.Windows.Clipboard.GetText(); } catch { }
        var r = Core.Command("import_code", new { text });
        string? why = r["reason"]?.GetValue<string>() switch
        {
            "not_a_code" => UI.Loc.L("No MyOwnEQ share code on the clipboard. Copy one (MEQ1-…) and try again."),
            "damaged" => UI.Loc.L("That share code is damaged or incomplete. Copy it again."),
            "newer" => UI.Loc.L("That share code is from a newer MyOwnEQ. Update the app."),
            _ => null,
        };
        if (why is not null) { Notify(why, error: true); return null; }
        Apply(r);
        if (r["imported_id"]?.GetValue<string>() is not { } id) return null;
        Notify(UI.Loc.L("Imported “%@” from a share code", State?.FindProfile(id)?.Name ?? ""));
        return id;
    }

    // ---------------------------------------------------------------- diagnostics

    /// <summary>Report for bug reports: the core's shared part + Windows sections
    /// (system, Equalizer APO per output, engine, generated config, log tail).
    /// The user name is masked; sync secrets never leave the core.</summary>
    // parity: diagnostics
    public string Diagnostics()
    {
        static string Mask(string s) => s.Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase);
        var sys = new List<string>
        {
            $"os: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})",
            $"runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}",
            $"language: {System.Globalization.CultureInfo.CurrentUICulture.Name}",
        };
        var apo = new List<string>
        {
            $"status: {Apo.Status}",
            $"install: {Mask(Apo.InstallPath ?? "-")}",
            $"config: {Mask(Apo.ConfigPath ?? "-")}",
            $"engine mode: {EngineMode} ({EngineState})",
        };
        if (Apo.LastError is { } ae) apo.Add($"last error: {Mask(ae)}");
        if (Engine.LastError is { } ee) apo.Add($"engine error: {Mask(ee)}");
        foreach (var d in Devices)
        {
            string engine = EngineMode ? $", engine {EngineHost.Status(d.Id)}" : "";
            apo.Add($"output: {d.Name} [{d.Kind}]{(d.Id == Current?.Id ? " (default)" : "")} -> APO {ApoBackend.CheckEndpoint(d.Id)}{engine}");
        }
        var cfg = new List<string>();
        try { if (Apo.OurConfigFile is { } f && System.IO.File.Exists(f)) cfg.AddRange(System.IO.File.ReadAllLines(f).Take(80).Select(Mask)); }
        catch (Exception e) { cfg.Add("unreadable: " + e.Message); }
        var log = new List<string>();
        try { if (System.IO.File.Exists(App.LogPath)) log.AddRange(System.IO.File.ReadAllLines(App.LogPath).TakeLast(40).Select(Mask)); }
        catch (Exception e) { log.Add("unreadable: " + e.Message); }
        var sections = new[]
        {
            new { title = "windows", lines = sys },
            new { title = "equalizer apo", lines = apo },
            new { title = "MyOwnEQ.txt", lines = cfg },
            new { title = "log (last 40 lines)", lines = log },
        };
        return Core.Command("diagnostics", new { sections })["text"]?.GetValue<string>() ?? "";
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
                "rule" => UI.Loc.L("because %@ is running", State.FindRule(r.Rule)?.App.Name ?? UI.Loc.L("app")),
                "rule_override" => UI.Loc.L("manual pick while %@ runs", State.FindRule(r.Rule)?.App.Name ?? UI.Loc.L("app")),
                "selected" => UI.Loc.L("selected"),
                "default" => UI.Loc.L("device default"),
                _ => UI.Loc.L("no profile (flat)"),
            };
        }
    }

    public void Dispose()
    {
        _syncTimer.Stop();
        _updateTimer.Stop();
        _apoWatch.Stop();
        _loopback?.Dispose();
        _apps.Dispose();
        _audio.Dispose();
        Core.Dispose();
    }
}
