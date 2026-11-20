using System.Globalization;
using System.Reflection;
using System.Text;

namespace PodPanel.Core;

/// <summary>
/// Arayüz metinleri. Her dil, Diller klasöründeki "kod.txt" dosyasıdır ("anahtar = metin" satırları) ve derlemeye
/// gömülür. Bir anahtar seçili dilde yoksa İngilizce, o da yoksa Türkçe metin kullanılır.
/// </summary>
public static class Loc
{
    private const string ResourcePrefix = "dil.";
    private const string Fallback = "en";
    private const string Original = "tr";

    private static readonly Dictionary<string, Dictionary<string, string>> Tables = Load();
    private static volatile Dictionary<string, string> _current;
    private static volatile string _language;
    private static volatile string _requested = "";

    static Loc()
    {
        _language = FromWindows();
        _current = Tables[_language];
    }

    /// <summary>Dil değişti (Use çağrısını yapan iş parçacığında bildirilir).</summary>
    public static event Action? Changed;

    /// <summary>Etkin dilin kodu (ör. "tr").</summary>
    public static string Language => _language;

    /// <summary>Kullanıcının seçtiği dil; boşsa Windows'un dili izlenir.</summary>
    public static string Requested => _requested;

    /// <summary>Kullanılabilir diller: kod ve dilin kendi adı; ada göre sıralı.</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = Tables
        .Select(t => (Code: t.Key, Name: t.Value.TryGetValue("_name", out string? name) ? name : t.Key))
        .OrderBy(t => t.Name, StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true))
        .ToList();

    /// <summary>Dili seçer. Boş ya da bilinmeyen kod, Windows'un görüntüleme dilini (yoksa İngilizceyi) seçer.</summary>
    public static void Use(string? code)
    {
        string wanted = (code ?? "").Trim().ToLowerInvariant();
        bool known = Tables.ContainsKey(wanted);
        string resolved = known ? wanted : FromWindows();
        _requested = known ? wanted : "";
        if (resolved == _language) return;
        _current = Tables[resolved];
        _language = resolved;
        Changed?.Invoke();
    }

    public static string T(string key)
    {
        if (_current.TryGetValue(key, out string? text)) return text;
        if (Tables[Fallback].TryGetValue(key, out text)) return text;
        return Tables[Original].TryGetValue(key, out text) ? text : key;
    }

    public static string T(string key, params object?[] args)
    {
        string format = T(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    private static string FromWindows()
    {
        string code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return Tables.ContainsKey(code) ? code : Fallback;
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Assembly assembly = typeof(Loc).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".txt", StringComparison.Ordinal)) continue;
            string code = resource[ResourcePrefix.Length..^4].ToLowerInvariant();
            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream == null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                int split = line.IndexOf('=');
                if (split <= 0) continue;
                string key = line[..split].Trim();
                string value = line[(split + 1)..].Trim();
                if (key.Length > 0 && value.Length > 0) table[key] = value;
            }
            tables[code] = table;
        }
        // Gömülü dosya yoksa (olmamalı) boş tablolarla çalış: anahtarlar görünür, uygulama çökmez.
        foreach (string code in new[] { Fallback, Original })
        {
            if (!tables.ContainsKey(code)) tables[code] = new Dictionary<string, string>();
        }
        return tables;
    }
}
