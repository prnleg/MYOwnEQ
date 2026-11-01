using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace MyOwnEQ.Platform;

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

    public static void OpenDownloadPage() =>
        Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
}
