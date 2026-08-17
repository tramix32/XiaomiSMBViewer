using System.Globalization;
using System.IO;
using System.Text.Json;

namespace XiaomiSMBViewer.Services;

public sealed record LanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Tlumaczenia z plikow Locales\{kod}.json obok exe. Angielski jest fallbackiem
/// dla brakujacych kluczy, wiec dorzucenie niepelnego jezyka niczego nie psuje.
/// </summary>
public static class Loc
{
    public const string AutoCode = "auto";

    private static readonly Dictionary<string, Dictionary<string, string>> Catalogs = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _current = new();
    private static Dictionary<string, string> _fallback = new();

    /// <summary>Kolejnosc na liscie wyboru; kody spoza tej listy trafiaja na koniec alfabetycznie.</summary>
    private static readonly string[] PreferredOrder =
        ["en", "zh", "hi", "es", "fr", "ar", "pt", "ru", "ja", "de", "it", "pl"];

    public static string CurrentCode { get; private set; } = "en";

    public static bool IsRightToLeft => CurrentCode is "ar" or "he" or "fa" or "ur";

    /// <summary>Kultura do formatowania dat i liczb w wybranym jezyku.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.InvariantCulture;

    public static event Action? LanguageChanged;

    public static void Initialize()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Locales");
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var code = Path.GetFileNameWithoutExtension(file);
                    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                    if (map is { Count: > 0 }) Catalogs[code] = map;
                }
                catch { /* uszkodzony plik jezyka nie moze blokowac startu */ }
            }
        }

        _fallback = Catalogs.TryGetValue("en", out var en) ? en : new Dictionary<string, string>();
        _current = _fallback;
    }

    public static IReadOnlyList<LanguageOption> AvailableLanguages()
    {
        var list = Catalogs
            .Select(kv => new LanguageOption(kv.Key, kv.Value.GetValueOrDefault("lang.name", kv.Key)))
            .OrderBy(o =>
            {
                int i = Array.IndexOf(PreferredOrder, o.Code);
                return i < 0 ? int.MaxValue : i;
            })
            .ThenBy(o => o.DisplayName, StringComparer.CurrentCulture)
            .ToList();

        list.Insert(0, new LanguageOption(AutoCode, T("lang.auto")));
        return list;
    }

    /// <summary>Kod jezyka systemu, jesli mamy dla niego katalog - inaczej angielski.</summary>
    public static string DetectSystemCode()
    {
        var ui = CultureInfo.CurrentUICulture;
        foreach (var candidate in new[] { ui.Name, ui.TwoLetterISOLanguageName })
        {
            if (Catalogs.ContainsKey(candidate)) return candidate;
        }

        // zh-Hans / zh-CN / zh-TW -> zh
        var prefix = ui.TwoLetterISOLanguageName;
        var match = Catalogs.Keys.FirstOrDefault(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return match ?? "en";
    }

    /// <summary>Ustawia jezyk. "auto" bierze jezyk systemu.</summary>
    public static void Use(string? code)
    {
        var resolved = string.IsNullOrWhiteSpace(code) || code == AutoCode
            ? DetectSystemCode()
            : code;

        if (!Catalogs.TryGetValue(resolved, out var catalog))
        {
            resolved = "en";
            catalog = _fallback;
        }

        CurrentCode = resolved;
        _current = catalog;

        // "zh" bez regionu nie jest kultura specyficzna - mapujemy na wariant uproszczony.
        var cultureName = resolved switch { "zh" => "zh-Hans", _ => resolved };
        try { Culture = CultureInfo.GetCultureInfo(cultureName); }
        catch (CultureNotFoundException) { Culture = CultureInfo.InvariantCulture; }

        LanguageChanged?.Invoke();
    }

    public static string T(string key)
    {
        if (_current.TryGetValue(key, out var value)) return value;
        if (_fallback.TryGetValue(key, out var fb)) return fb;
        return key;
    }

    public static string T(string key, params object?[] args)
    {
        var format = T(key);
        try { return string.Format(CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) { return format; }
    }
}
