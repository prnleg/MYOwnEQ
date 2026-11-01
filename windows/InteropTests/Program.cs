using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyOwnEQ;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

var dir = Path.Combine(Path.GetTempPath(), "meq-interop-" + Guid.NewGuid().ToString("N"));
using (var core = new CoreBridge(dir, "win-test"))
{
    core.Command("get_state");
    var s = core.DecodeLast<CoreState>();
    Check(s is not null && s.Library.Profiles.Count == 18 && s.Library.Profiles.All(p => p.Tags.Contains("builtin")),
          "decode initial state (18 presets, all tagged builtin)");
    Check(s!.Library.Profiles.Any(p => p.Eq is ParametricLayer pl && pl.Bands.Count > 0), "polymorphic EqLayer decodes");

    var kz = new { uid = "{0.0.0.00000000}.{aaaa-bbbb}", name = "Realtek Speakers", kind = DeviceKind.Analog };
    var bt = new { uid = "{0.0.0.00000000}.{cccc}", name = "Momentum 4", kind = DeviceKind.Bluetooth };
    core.Command("set_connected", new { devices = new[] { kz, bt } });
    core.Command("set_device", new { device = kz });
    s = core.DecodeLast<CoreState>()!;
    Check(s.Group is not null && s.CurrentGroup?.Members[0].Kind == DeviceKind.Analog, "device registered, enum kind round-trips");
    Check(s.ConnectedGroups.Count == 2, "two connected groups");

    // Rich profile round-trip through Rust.
    var p = new Profile { Name = "Interop" };
    p.Eq = new ParametricLayer { Bands = { new Band { Kind = FilterKind.LowShelf, Freq = 105, GainDb = 4, Q = 0.7 }, new Band { Kind = FilterKind.HighPass, Freq = 30, Q = 0.707, Enabled = false } } };
    p.Correction.Add(new Band { Kind = FilterKind.Peak, Freq = 2300, GainDb = -3, Q = 2 });
    p.CorrectionSource = new CorrectionSource { Provider = "AutoEQ", Name = "KZ ZSN Pro" };
    p.Preamp = Preamp.Manual(-5.5);
    p.Macros.Clarity = 2;
    p.Balance = -0.25;
    p.Loudness = 0.5;
    var r = core.Command("upsert_profile", new { profile = p });
    Check(r["ok"]?.GetValue<bool>() == true, "upsert_profile accepted: " + (r["error"]?.ToString() ?? "ok"));
    s = core.DecodeLast<CoreState>()!;
    var back = s.FindProfile(p.Id);
    Check(back is not null && JsonSerializer.Serialize(back, Json.Options) == JsonSerializer.Serialize(p, Json.Options), "profile identical after Rust round-trip");

    var g = new Profile { Name = "Graphic", Eq = new GraphicLayer { Points = { new GraphicPoint { Freq = 1000, GainDb = 3 } } } };
    r = core.Command("upsert_profile", new { profile = g });
    Check(r["ok"]?.GetValue<bool>() == true && core.DecodeLast<CoreState>()!.FindProfile(g.Id)?.Eq is GraphicLayer, "graphic layer round-trip");

    r = core.Command("select", new { group = s.Group, profile = p.Id });
    s = core.DecodeLast<CoreState>()!;
    Check(s.Resolution.Kind == "selected" && s.ActiveProfile?.Name == "Interop", "select -> resolution");

    var apo = core.Command("apo_config")["text"]?.GetValue<string>() ?? "";
    Check(apo.Contains("Device: {aaaa-bbbb}") && apo.Contains("Preamp: -5.5 dB") && apo.Contains("Channel: L"), "apo_config: device section, manual preamp, balance");
    Check(apo.Contains("Device: {cccc}"), "apo_config: second connected device");

    r = core.Command("preview", new System.Collections.Generic.Dictionary<string, object?> { ["profile"] = null });
    Check(r["ok"]?.GetValue<bool>() == true, "preview null accepted");

    r = core.Command("set_apps", new { apps = new[] { new { id = "cs2.exe", focused = true } } });
    Check(r["ok"]?.GetValue<bool>() == true, "set_apps accepted");

    var fps = s.Library.Profiles.First(x => x.Name == "FPS");
    r = core.Command("upsert_rule", new { rule = new Rule { Group = s.Group!, App = new AppMatch { Name = "CS2", WinExe = "cs2.exe" }, Profile = fps.Id } });
    s = core.DecodeLast<CoreState>()!;
    // Platform::current() is Mac when running this test on macOS, so the rule won't fire here; just check it stored.
    Check(r["ok"]?.GetValue<bool>() == true && s.Library.Rules.Count == 1 && s.Library.Rules[0].App.WinExe == "cs2.exe", "rule with win_exe stored");

    var grp = s.CurrentGroup!;
    grp.Favorites.Add(fps.Id);
    grp.DefaultProfile = fps.Id;
    r = core.Command("update_group", new { group = grp });
    s = core.DecodeLast<CoreState>()!;
    Check(r["ok"]?.GetValue<bool>() == true && s.CurrentGroup!.Favorites.Contains(fps.Id), "update_group (favorites/default)");

    // Boost set elsewhere (Mac) survives a save from Windows.
    grp = s.CurrentGroup!;
    grp.BoostDb = 6;
    core.Command("update_group", new { group = grp });
    s = core.DecodeLast<CoreState>()!;
    grp = s.CurrentGroup!;
    grp.Name = "Renamed on PC";
    core.Command("update_group", new { group = grp });
    s = core.DecodeLast<CoreState>()!;
    Check(s.CurrentGroup!.BoostDb == 6 && s.CurrentGroup.Name == "Renamed on PC", "boost_db round-trips through a Windows save");

    r = core.Command("sync_configure", new { config = new { mode = "host", port = 0, token = "ABCD-EFGH" } });
    s = core.DecodeLast<CoreState>()!;
    Check(s.Sync.Mode == "host" && s.Sync.HostPort > 0 && s.Sync.Token == "ABCD-EFGH", $"host mode starts (port {s.Sync.HostPort})");
    r = core.Command("sync_tick");
    Check(r["ok"]?.GetValue<bool>() == true, "sync_tick");
    core.Command("sync_configure", new { config = new { mode = "off" } });

    core.Command("curve", new { profile = p, points = 64 });
    var curve = core.DecodeLast<Curve>();
    Check(curve is not null && curve.Freqs.Count == 64 && curve.Bands.Count == 4, "curve decode (correction + 2 bands + clarity macro)");

    var buf = new float[32];
    Check(!core.Spectrum(buf), "spectrum without audio handle returns false");
    var audio = core.MakeAudio(48000);
    unsafe
    {
        var samples = new float[2048];
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)Math.Sin(i * 0.13);
        fixed (float* ptr = samples) Native.meq_audio_analyze(audio, ptr, 1024, 2);
    }
    Check(core.Spectrum(buf) && buf.Max() > -60, "analyze-only feeds spectrum");
    Native.meq_audio_free(audio);

    r = core.Command("unlink_device", new { key = s.Device });
    Check(r["ok"]?.GetValue<bool>() == true, "unlink_device key shape");

    Check(CoreBridge.MachineId().Length > 4, "machine id");
}
Directory.Delete(dir, true);
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
