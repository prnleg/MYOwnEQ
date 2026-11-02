using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyOwnEQ;

// C# mirrors of the Rust model JSON (core/model). snake_case on the wire.

public enum FilterKind { Peak, LowShelf, HighShelf, LowPass, HighPass, Notch, BandPass }
public enum DeviceKind { BuiltIn, Bluetooth, Usb, Hdmi, Analog, Virtual, Other }
public enum Trigger { Running, Focused }

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static T Clone<T>(T v) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Options), Options)!;
}

public static class FilterKindExt
{
    public static bool HasGain(this FilterKind k) => k is FilterKind.Peak or FilterKind.LowShelf or FilterKind.HighShelf;

    public static string Label(this FilterKind k) => k switch
    {
        FilterKind.Peak => "Peak",
        FilterKind.LowShelf => "Low Shelf",
        FilterKind.HighShelf => "High Shelf",
        FilterKind.LowPass => "Low Pass",
        FilterKind.HighPass => "High Pass",
        FilterKind.Notch => "Notch",
        _ => "Band Pass",
    };
}

public sealed class Band
{
    public FilterKind Kind { get; set; } = FilterKind.Peak;
    public double Freq { get; set; } = 1000;
    public double GainDb { get; set; }
    public double Q { get; set; } = 1;
    public bool Enabled { get; set; } = true;
}

public sealed class GraphicPoint
{
    public double Freq { get; set; }
    public double GainDb { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ParametricLayer), "parametric")]
[JsonDerivedType(typeof(GraphicLayer), "graphic")]
public abstract class EqLayer { }

public sealed class ParametricLayer : EqLayer
{
    public List<Band> Bands { get; set; } = new();
}

public sealed class GraphicLayer : EqLayer
{
    public List<GraphicPoint> Points { get; set; } = new();
}

public sealed class Preamp
{
    public string Mode { get; set; } = "auto";
    public double? Db { get; set; }
    [JsonIgnore] public bool IsAuto => Mode != "manual";
    public static Preamp Auto() => new();
    public static Preamp Manual(double db) => new() { Mode = "manual", Db = db };
}

public sealed class Macros
{
    public double Bass { get; set; }
    public double Mids { get; set; }
    public double Clarity { get; set; }
    public double Treble { get; set; }
    /// <summary>Whole-spectrum slope (dB): + brighter, − warmer.</summary>
    public double Tilt { get; set; }
}

/// <summary>Headphone crossfeed strength (core: myowneq_dsp::Crossfeed).</summary>
public enum Crossfeed { Off, Light, Normal, Strong }

public sealed class CorrectionSource
{
    public string Provider { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class Profile
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "";
    public List<Band> Correction { get; set; } = new();
    public CorrectionSource? CorrectionSource { get; set; }
    public EqLayer Eq { get; set; } = new ParametricLayer();
    public Macros Macros { get; set; } = new();
    public Preamp Preamp { get; set; } = new();
    public double Balance { get; set; }
    public bool Limiter { get; set; } = true;
    public double Loudness { get; set; }
    public Crossfeed Crossfeed { get; set; }
    public List<string> Tags { get; set; } = new();

    public static string NewId() => Guid.NewGuid().ToString().ToLowerInvariant();
}

public sealed class DeviceKey : IEquatable<DeviceKey>
{
    public string Machine { get; set; } = "";
    public string Uid { get; set; } = "";
    public bool Equals(DeviceKey? o) => o is not null && o.Machine == Machine && o.Uid == Uid;
    public override bool Equals(object? o) => Equals(o as DeviceKey);
    public override int GetHashCode() => HashCode.Combine(Machine, Uid);
}

public sealed class DeviceMember
{
    public DeviceKey Key { get; set; } = new();
    public string OsName { get; set; } = "";
    public DeviceKind Kind { get; set; } = DeviceKind.Other;
}

public sealed class DeviceGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<DeviceMember> Members { get; set; } = new();
    public string? DefaultProfile { get; set; }
    public List<string> Favorites { get; set; } = new();
    public bool Enabled { get; set; } = true;
    /// Volume boost in dB (set on the Mac; Equalizer APO has no limiter, so
    /// Windows doesn't apply it). Must round-trip so saving here keeps it.
    public double BoostDb { get; set; }
}

public sealed class AppMatch
{
    public string Name { get; set; } = "";
    public string? MacBundleId { get; set; }
    public string? WinExe { get; set; }
}

public sealed class Rule
{
    public string Id { get; set; } = global::MyOwnEQ.Profile.NewId();
    public string Group { get; set; } = "";
    public AppMatch App { get; set; } = new();
    public Trigger Trigger { get; set; } = Trigger.Running;
    public string Profile { get; set; } = "";
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class Library
{
    public int Schema { get; set; }
    public List<Profile> Profiles { get; set; } = new();
    public List<DeviceGroup> Groups { get; set; } = new();
    public List<Rule> Rules { get; set; } = new();
    public Dictionary<string, string> Selected { get; set; } = new();
}

public sealed class ResolutionInfo
{
    public string Kind { get; set; } = "flat";
    public string? Profile { get; set; }
    public string? Rule { get; set; }
}

public sealed class SyncInfo
{
    public string Mode { get; set; } = "off";
    public string Status { get; set; } = "off";
    public string Detail { get; set; } = "";
    public string? Url { get; set; }
    public string? Token { get; set; }
    public int? HostPort { get; set; }
    public List<string>? HostAddresses { get; set; }
    public int Pending { get; set; }
    /// <summary>Machines using this sync (from the server), online or seen before.</summary>
    public List<Peer>? Peers { get; set; }
}

/// <summary>A machine in the sync, as the server reports it.</summary>
public sealed class Peer
{
    public string Machine { get; set; } = "";
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Online { get; set; }
    public ulong LastSeen { get; set; }
    [JsonIgnore] public string DisplayName => Name.Length > 0 ? Name : Machine;
}

/// <summary>Shared constants from the core (<c>ui_constants</c>).</summary>
public sealed class UiConstants
{
    public sealed class Spot { public string Name { get; set; } = ""; public double Freq { get; set; } }
    public List<double> Graphic10 { get; set; } = new();
    public List<double> Graphic31 { get; set; } = new();
    public List<Spot> Macros { get; set; } = new();
    public int MaxBands { get; set; } = 64;
}

/// <summary>Updater status. State: off | idle | checking | up_to_date | downloading | ready | error.</summary>
public sealed class UpdateInfo
{
    public string State { get; set; } = "off";
    public string? Version { get; set; }
    public string? NotesUrl { get; set; }
    public string? Message { get; set; }
    public string? Current { get; set; }
    public bool? Auto { get; set; }
    public long? CheckedAt { get; set; }

    [JsonIgnore] public bool IsReady => State == "ready";
    /// <summary>Checking or downloading right now (show a spinner).</summary>
    [JsonIgnore] public bool IsBusy => State is "checking" or "downloading";
    [JsonIgnore] public string Label => State switch
    {
        "checking" => "Checking for updates…",
        "up_to_date" => "Up to date",
        "downloading" => $"Downloading {Version}…",
        "ready" => $"Version {Version} is ready. Restart to install.",
        "error" => $"Update check failed: {Message}",
        _ => "Updates idle",
    };
}

public sealed class FoundHost
{
    public string Name { get; set; } = "";
    public string Addr { get; set; } = "";
}

public sealed class ControlInfo
{
    public bool Enabled { get; set; } = true;
    public bool Running { get; set; }
}

public sealed class CoreState
{
    public string Machine { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DeviceKey? Device { get; set; }
    public string? Group { get; set; }
    public List<string> ConnectedGroups { get; set; } = new();
    public ResolutionInfo Resolution { get; set; } = new();
    public double PreampDb { get; set; }
    public double SampleRate { get; set; }
    public Library Library { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public SyncInfo Sync { get; set; } = new();
    public UpdateInfo? Update { get; set; }
    /// <summary>Local control channel (CLI, links). Null from older cores.</summary>
    public ControlInfo? Control { get; set; }

    public Profile? FindProfile(string? id) => id is null ? null : Library.Profiles.FirstOrDefault(p => p.Id == id);
    public DeviceGroup? FindGroup(string? id) => id is null ? null : Library.Groups.FirstOrDefault(g => g.Id == id);
    public Rule? FindRule(string? id) => id is null ? null : Library.Rules.FirstOrDefault(r => r.Id == id);
    [JsonIgnore] public Profile? ActiveProfile => FindProfile(Resolution.Profile);
    [JsonIgnore] public DeviceGroup? CurrentGroup => FindGroup(Group);
}

public sealed class Curve
{
    public List<double> Freqs { get; set; } = new();
    public List<double> Total { get; set; } = new();
    public double Preamp { get; set; }
    public List<List<double>> Bands { get; set; } = new();
}
