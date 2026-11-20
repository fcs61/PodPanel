using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace PodPanel.Setup;

/// <summary>
/// Kurulum penceresinin dili. Betikler (kur.ps1, kaldir.ps1) hep Türkçe yazar ve günlük dosyası Türkçe kalır;
/// Windows'un görüntüleme dili Türkçe değilse pencere, gösterdiği her metni ceviri-en.txt tablosuyla İngilizceye
/// çevirir. Tabloda karşılığı olmayan metin olduğu gibi (Türkçe) görünür.
/// </summary>
internal static class Tr
{
    private sealed class Rule
    {
        public Regex Pattern = null!;
        public string Replacement = "";
    }

    private static readonly Dictionary<string, string> Exact = new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly List<Rule> Rules = new List<Rule>();

    static Tr()
    {
        string? forced = Environment.GetEnvironmentVariable("PODPANEL_DIL");
        English = forced != null
            ? !forced.Trim().Equals("tr", StringComparison.OrdinalIgnoreCase)
            : !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("tr", StringComparison.OrdinalIgnoreCase);
        Load();
    }

    /// <summary>Pencere İngilizce gösteriliyor (Windows'un dili Türkçe değil ya da PODPANEL_DIL=en).</summary>
    public static bool English { get; set; }

    /// <summary>Pencerenin kendi metni ya da betiğin tek sözcüklük değeri: önce tam eşleşme, yoksa parça kuralları.</summary>
    public static string T(string text)
    {
        if (!English || text.Length == 0) return text;
        return Exact.TryGetValue(text.Trim(), out string? translated) ? translated : Apply(text);
    }

    /// <summary>İçinde {0}, {1} olan bir şablonu çevirip doldurur.</summary>
    public static string T(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(format), args);

    /// <summary>Betikten gelen bir satır ya da ileti: baştaki boşluk korunur.</summary>
    public static string Line(string text)
    {
        if (!English || text.Length == 0) return text;
        string trimmed = text.TrimStart();
        if (Exact.TryGetValue(trimmed.TrimEnd(), out string? translated)) return text.Substring(0, text.Length - trimmed.Length) + translated;
        return Apply(text);
    }

    /// <summary>İlk harfi büyütür (Türkçede i -> İ, İngilizcede i -> I).</summary>
    public static string Capitalize(string text) =>
        text.Length == 0 ? text
            : char.ToUpper(text[0], English ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("tr-TR")) + text.Substring(1);

    private static string Apply(string text)
    {
        foreach (Rule rule in Rules)
        {
            text = rule.Pattern.Replace(text, match =>
                Regex.Replace(rule.Replacement, @"\{(\d)\}", slot => match.Groups["g" + slot.Groups[1].Value].Value));
        }
        return text;
    }

    private static void Load()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ceviri-en.txt");
        if (stream == null) return;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        bool exact = true;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line == "[tam]") { exact = true; continue; }
            if (line == "[parca]") { exact = false; continue; }
            int split = line.IndexOf(" => ", StringComparison.Ordinal);
            if (split <= 0) continue;
            string source = line.Substring(0, split).Trim(), target = line.Substring(split + 4).Trim();
            if (exact)
            {
                Exact[source] = target;
                continue;
            }
            Rules.Add(new Rule { Pattern = new Regex(ToPattern(source), RegexOptions.CultureInvariant), Replacement = target });
        }
    }

    /// <summary>
    /// "Servis güncel ({0}), durumu:" gibi bir şablonu düzenli ifadeye çevirir: metin harfi harfine aranır, {0} bir
    /// yakalama grubu olur (şablonun sonundaysa satırın sonuna kadar, değilse sonraki metne kadar).
    /// </summary>
    internal static string ToPattern(string template)
    {
        string pattern = Regex.Escape(template);
        pattern = Regex.Replace(pattern, @"\\\{(\d)}$", "(?<g$1>.+)");
        return Regex.Replace(pattern, @"\\\{(\d)}", "(?<g$1>.+?)");
    }
}
