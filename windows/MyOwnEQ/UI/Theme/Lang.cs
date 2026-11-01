using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MyOwnEQ.UI;

/// <summary>A language pack from lang/*.json — same files as the Mac app (see lang/README.md).</summary>
public sealed class LangPack
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Author { get; set; }
    public Dictionary<string, string> Strings { get; set; } = new();
}

/// <summary>UI language. Keys are the English text, so a missing translation shows English.</summary>
// parity: languages
public sealed class Lang
{
    public List<LangPack> Packs { get; private set; } = new();
    /// <summary>Chosen code, or null to follow Windows.</summary>
    public string? Choice { get; private set; }
    public string Code { get; private set; } = "en-US";
    public List<string> LoadErrors { get; private set; } = new();
    private Dictionary<string, string> _table = new();

    public event Action? Changed;

    public Lang(string? choice, CoreBridge? core = null)
    {
        Choice = choice;
        _core = core;
        Reload();
    }

    public string Get(string key) => _table.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : key;

    public void Select(string? code)
    {
        Choice = code;
        Apply();
    }

    /// <summary>Parsing, merge rules and language choice live in the core (same for every app).</summary>
    private readonly CoreBridge? _core;
    private string _resolved = "en-US";

    public void Reload()
    {
        if (_core is null) { Apply(); return; }
        var r = _core.Command("load_langs", new
        {
            dirs = ThemeStore.SearchDirs("Lang").ToList(),
            choice = Choice,
            preferred = new[] { CultureInfo.CurrentUICulture.Name },
        });
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        Packs = r["packs"]?.Deserialize<List<LangPack>>(opts) ?? new() { new() { Code = "en-US", Name = "English (US)" } };
        LoadErrors = r["errors"]?.Deserialize<List<string>>() ?? new();
        _resolved = r["code"]?.GetValue<string>() ?? "en-US";
        Apply();
    }

    private void Apply()
    {
        var wanted = Choice ?? _resolved;
        var pack = Packs.FirstOrDefault(p => p.Code == wanted) ?? Packs.FirstOrDefault(p => p.Code == "en-US") ?? new LangPack { Code = "en-US" };
        _table = pack.Strings;
        Code = pack.Code;
        Changed?.Invoke();
    }

}

/// <summary><c>using static MyOwnEQ.UI.Loc;</c>, then call L with the English text as the key.</summary>
public static class Loc
{
    /// <summary>Translate. <c>%@</c> placeholders are filled from args in order.
    /// Never pass an interpolated string: the key must be a literal (scripts/lang-keys.py).</summary>
    public static string L(string key, params object?[] args)
    {
        var s = Ui.Lang?.Get(key) ?? key;
        foreach (var a in args)
        {
            int i = s.IndexOf("%@", StringComparison.Ordinal);
            if (i < 0) break;
            s = s[..i] + Convert.ToString(a, CultureInfo.CurrentCulture) + s[(i + 2)..];
        }
        return s;
    }
}
