using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace MyOwnEQ.Platform;

/// <summary>Is Equalizer APO hooked into a given output (endpoint)?</summary>
public enum ApoEndpoint
{
    Unknown,    // couldn't read the registry: don't nag
    Enabled,
    NotEnabled, // installed, but not ticked for this output in its Configurator
}

public enum ApoStatus
{
    NotInstalled,
    NeedsSetup,     // installed, config.txt doesn't include our file yet
    NoWriteAccess,  // config folder not writable by this user
    Ready,
}

/// <summary>
/// Windows engine v1: Equalizer APO does the DSP; we generate its config.
/// Our filters live in <c>config\MyOwnEQ.txt</c>; <c>config.txt</c> includes it.
/// Equalizer APO watches the folder and reloads within milliseconds.
/// </summary>
public sealed class ApoBackend
{
    public const string OurFile = "MyOwnEQ.txt";
    public const string BackupFile = "config.before-myowneq.txt";
    public const string DownloadUrl = "https://sourceforge.net/projects/equalizerapo/";

    public string? InstallPath { get; private set; }
    public string? ConfigPath { get; private set; }
    public ApoStatus Status { get; private set; } = ApoStatus.NotInstalled;
    public string? LastError { get; private set; }

    private string? _lastWritten;

    public ApoStatus Detect()
    {
        InstallPath = ConfigPath = null;
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\EqualizerAPO");
            InstallPath = k?.GetValue("InstallPath") as string;
            ConfigPath = k?.GetValue("ConfigPath") as string;
        }
        catch { /* treat as not installed */ }
        if (ConfigPath is null && InstallPath is not null) ConfigPath = Path.Combine(InstallPath, "config");
        if (ConfigPath is null || !Directory.Exists(ConfigPath))
            return Status = ApoStatus.NotInstalled;
        if (!CanWrite())
            return Status = ApoStatus.NoWriteAccess;
        return Status = IsIncluded() ? ApoStatus.Ready : ApoStatus.NeedsSetup;
    }

    private string MainConfig => Path.Combine(ConfigPath!, "config.txt");

    private bool CanWrite()
    {
        try
        {
            string probe = Path.Combine(ConfigPath!, ".myowneq-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private bool IsIncluded()
    {
        try
        {
            return File.Exists(MainConfig) && File.ReadAllLines(MainConfig)
                .Select(l => l.Trim())
                .Any(l => !l.StartsWith('#') && l.StartsWith("Include:", StringComparison.OrdinalIgnoreCase)
                          && l.Contains(OurFile, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// Point config.txt at our file. The previous config.txt is kept as
    /// <see cref="BackupFile"/> (only the first time) so it can be restored.
    /// With <paramref name="takeOver"/> other EQ (e.g. Peace) is disabled to avoid double EQ.
    /// </summary>
    public void Setup(bool takeOver)
    {
        if (ConfigPath is null) throw new InvalidOperationException("Equalizer APO not found");
        string backup = Path.Combine(ConfigPath, BackupFile);
        if (File.Exists(MainConfig) && !File.Exists(backup)) File.Copy(MainConfig, backup);
        string include = $"Include: {OurFile}";
        string text = takeOver || !File.Exists(MainConfig)
            ? $"# Managed by MyOwnEQ. Your previous config is in {BackupFile}.\n{include}\n"
            : File.ReadAllText(MainConfig).TrimEnd() + $"\n\n# Added by MyOwnEQ\n{include}\n";
        AtomicWrite(MainConfig, text);
        if (!File.Exists(Path.Combine(ConfigPath, OurFile))) AtomicWrite(Path.Combine(ConfigPath, OurFile), "# MyOwnEQ\n");
        Detect();
    }

    /// <summary>Put the user's original config.txt back and stop managing APO.</summary>
    public bool Restore()
    {
        if (ConfigPath is null) return false;
        string backup = Path.Combine(ConfigPath, BackupFile);
        if (!File.Exists(backup)) return false;
        AtomicWrite(MainConfig, File.ReadAllText(backup));
        File.Delete(backup);
        Detect();
        return true;
    }

    /// <summary>
    /// Undo everything MyOwnEQ did to Equalizer APO (used by the uninstaller):
    /// strip our lines from config.txt; if nothing of the user's is left (we took it
    /// over), put the backup back instead. Then delete our include file. APO reloads and
    /// drops our filters / the engine.
    /// </summary>
    public bool Unhook()
    {
        if (ConfigPath is null) return false;
        if (File.Exists(MainConfig))
        {
            string stripped = StripOurLines(File.ReadAllText(MainConfig));
            bool onlyOurs = !stripped.Split('\n').Any(l => l.Trim() is { Length: > 0 } t && !t.StartsWith('#'));
            if (!(onlyOurs && Restore())) AtomicWrite(MainConfig, stripped);
        }
        try { File.Delete(Path.Combine(ConfigPath, OurFile)); } catch { /* APO may hold it; it's inert once not included */ }
        Detect();
        return true;
    }

    /// <summary>config.txt without the lines <see cref="Setup"/> adds.</summary>
    public static string StripOurLines(string config)
    {
        var kept = config.Replace("\r\n", "\n").Split('\n').Where(l =>
        {
            var t = l.Trim();
            bool ourInclude = t.StartsWith("Include:", StringComparison.OrdinalIgnoreCase) && t.Contains(OurFile, StringComparison.OrdinalIgnoreCase);
            bool ourComment = t is "# Added by MyOwnEQ" || t.StartsWith("# Managed by MyOwnEQ", StringComparison.Ordinal);
            return !ourInclude && !ourComment;
        });
        return string.Join("\n", kept).TrimEnd() + "\n";
    }

    /// <summary>Write our include file if the content changed.</summary>
    public bool Write(string text)
    {
        if (Status != ApoStatus.Ready || ConfigPath is null || text == _lastWritten) return false;
        try
        {
            AtomicWrite(Path.Combine(ConfigPath, OurFile), text);
            _lastWritten = text;
            LastError = null;
            return true;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Detect();
            return false;
        }
    }

    private static void AtomicWrite(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>One-time UAC prompt: give the Users group modify rights on the config folder.</summary>
    public bool FixPermissions()
    {
        if (ConfigPath is null) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = $"\"{ConfigPath}\" /grant *S-1-5-32-545:(OI)(CI)M",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit(30_000);
        }
        catch { return false; } // user cancelled UAC
        return Detect() != ApoStatus.NoWriteAccess;
    }

    /// <summary>Equalizer APO's device selector (choose which outputs it hooks).</summary>
    public void OpenConfigurator()
    {
        if (InstallPath is null) return;
        string exe = Path.Combine(InstallPath, "Configurator.exe");
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    /// <summary>Equalizer APO's COM class id, written into an output's audio effects
    /// (FxProperties) when it's enabled for that output in the Configurator.</summary>
    public const string ApoClsid = "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}";

    /// <summary>Check whether APO is enabled on the output with this endpoint id
    /// ("{0.0.0.00000000}.{guid}"). Status "Ready" alone isn't enough: APO is enabled
    /// per output, and Bluetooth headphones are often left out.</summary>
    public static ApoEndpoint CheckEndpoint(string endpointId)
    {
        int i = endpointId.LastIndexOf("}.", StringComparison.Ordinal);
        string guid = i >= 0 ? endpointId[(i + 2)..] : endpointId;
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\{guid}\FxProperties");
            if (k is null) return ApoEndpoint.NotEnabled; // no audio effects at all on this output
            foreach (var name in k.GetValueNames())
            {
                var v = k.GetValue(name);
                var text = v switch { string s => s, string[] a => string.Join(";", a), _ => null };
                if (text?.Contains(ApoClsid, StringComparison.OrdinalIgnoreCase) == true) return ApoEndpoint.Enabled;
            }
            return ApoEndpoint.NotEnabled;
        }
        catch { return ApoEndpoint.Unknown; }
    }

    /// <summary>Our generated filter file, to look at what's being sent to APO.</summary>
    public string? OurConfigFile => ConfigPath is null ? null : Path.Combine(ConfigPath, OurFile);

    public void OpenOurConfig()
    {
        if (OurConfigFile is { } f && File.Exists(f)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{f}\"") { UseShellExecute = true });
    }

    public static void OpenDownloadPage() =>
        Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
}
