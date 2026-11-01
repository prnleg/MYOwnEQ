using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace MyOwnEQ;

/// <summary>Wrapper over the Rust core. UI thread only (except <see cref="Discover"/>).</summary>
public sealed class CoreBridge : IDisposable
{
    private IntPtr _core;

    public CoreBridge(string dataDir, string machineId)
    {
        Directory.CreateDirectory(dataDir);
        _core = Native.meq_core_new(dataDir, machineId);
        if (_core == IntPtr.Zero) throw new InvalidOperationException("MyOwnEQ core failed to start");
    }

    public IntPtr Handle => _core;

    /// <summary>Run a command; args are serialized with the snake_case model options.</summary>
    public JsonObject Command(string name, object? args = null)
    {
        var obj = args is null ? new JsonObject() : (JsonSerializer.SerializeToNode(args, Json.Options) as JsonObject ?? new JsonObject());
        obj["cmd"] = name;
        var reply = Native.TakeString(Native.meq_core_command(_core, obj.ToJsonString()));
        LastReply = reply;
        return JsonNode.Parse(reply) as JsonObject ?? new JsonObject { ["ok"] = false };
    }

    public string LastReply { get; private set; } = "";

    public T? Decode<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(Json.Options);

    public T? DecodeLast<T>()
    {
        try { return JsonSerializer.Deserialize<T>(LastReply, Json.Options); }
        catch (JsonException e) { System.Diagnostics.Debug.WriteLine($"decode {typeof(T).Name}: {e.Message}"); return default; }
    }

    public IntPtr MakeAudio(double sampleRate) => Native.meq_core_make_audio(_core, sampleRate);

    public bool Spectrum(float[] buf) => Native.meq_core_spectrum(_core, buf, (uint)buf.Length) != 0;

    /// <summary>LAN discovery; safe on a background thread.</summary>
    public static List<FoundHost> Discover(uint timeoutMs = 1500)
    {
        var json = Native.TakeString(Native.meq_discover(timeoutMs));
        return JsonSerializer.Deserialize<List<FoundHost>>(json, Json.Options) ?? new();
    }

    /// <summary>Stable per-PC id from MachineGuid, hashed so the raw id never leaves the PC.</summary>
    public static string MachineId()
    {
        string raw = Environment.MachineName;
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (k?.GetValue("MachineGuid") is string g) raw = g;
        }
        catch { /* fall back to machine name */ }
        ulong hash = 0xcbf29ce484222325; // FNV-1a
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(raw)) { hash ^= b; hash *= 0x100000001b3; }
        return "win-" + hash.ToString("x");
    }

    /// <summary>%APPDATA%\MyOwnEQ, or MYOWNEQ_DATA_DIR (CI self-test).</summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("MYOWNEQ_DATA_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyOwnEQ");

    public void Dispose()
    {
        if (_core != IntPtr.Zero) { Native.meq_core_free(_core); _core = IntPtr.Zero; }
    }
}
