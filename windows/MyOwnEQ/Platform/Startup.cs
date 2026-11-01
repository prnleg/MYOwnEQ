using System;
using Microsoft.Win32;

namespace MyOwnEQ.Platform;

/// <summary>"Start with Windows" via HKCU\...\Run (no admin needed).</summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "MyOwnEQ";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
            else k.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
