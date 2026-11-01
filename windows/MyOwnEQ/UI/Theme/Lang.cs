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
public sealed class Lang
{
    public List<LangPack> Packs { get; private set; } = new();
    /// <summary>Chosen code, or null to follow Windows.</summary>
    public string? Choice { get; private set; }
    public string Code { get; private set; } = "en-US";
    public List<string> LoadErrors { get; private set; } = new();
    private Dictionary<string, string> _table = new();

    public event Action? Changed;

    public Lang(string? choice)
    {
        Choice = choice;
        Reload();
    }

    public string Get(string key) => _table.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : key;

    public void Select(string? code)
    {
        Choice = code;
        Apply();
    }

    public void Reload()
    {
        var byCode = new Dictionary<string, LangPack> { ["en-US"] = new() { Code = "en-US", Name = "English (US)", Author = "MyOwnEQ" } };
        var errors = new List<string>();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var dir in ThemeStore.SearchDirs("Lang"))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var p = JsonSerializer.Deserialize<LangPack>(File.ReadAllText(f), opts);
                    if (p is null || string.IsNullOrWhiteSpace(p.Code)) throw new InvalidDataException("missing code");
                    // User packs extend/override bundled ones key by key.
                    if (byCode.TryGetValue(p.Code, out var existing))
                    {
                        foreach (var kv in p.Strings) existing.Strings[kv.Key] = kv.Value;
                        existing.Name = p.Name;
                    }
                    else byCode[p.Code] = p;
                }
                catch (Exception e) { errors.Add($"{Path.GetFileName(f)}: {e.Message}"); }
            }
        }
        Packs = byCode.Values.OrderBy(p => p.Name, StringComparer.CurrentCulture).ToList();
        LoadErrors = errors;
        Apply();
    }

    private void Apply()
    {
        var wanted = Choice ?? SystemMatch(Packs.Select(p => p.Code).ToList()) ?? "en-US";
        var pack = Packs.FirstOrDefault(p => p.Code == wanted) ?? Packs.First(p => p.Code == "en-US");
        _table = pack.Strings;
        Code = pack.Code;
        Changed?.Invoke();
    }

    private static string? SystemMatch(List<string> codes)
    {
        var ui = CultureInfo.CurrentUICulture;
        return codes.FirstOrDefault(c => c.Equals(ui.Name, StringComparison.OrdinalIgnoreCase))
            ?? codes.FirstOrDefault(c => c.StartsWith(ui.TwoLetterISOLanguageName + "-", StringComparison.OrdinalIgnoreCase));
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
