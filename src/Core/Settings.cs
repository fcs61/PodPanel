using System.Text.Encodings.Web;
using System.Text.Json;

namespace PodPanel.Core;

/// <summary>Oyun modu açılırken değiştirilenler; kapanırken (ya da çökme sonrası) geri almak için saklanır.</summary>
public sealed class SavedGameState
{
    public string? AirPodsCaptureId { get; set; }
    public List<int> RolesToRestore { get; set; } = new();
    public string? DisabledEndpointId { get; set; }
}

/// <summary>Son bağlantıda sürücü telemetrisinden okunan bağlantı bilgisi.</summary>
public sealed class CachedLink
{
    public string? Codec { get; set; }
    public int? SampleRate { get; set; }
    public int? Channels { get; set; }
    public int? DelayMs { get; set; }
    public string? HfpCodec { get; set; }
    public DateTime Updated { get; set; }
}

public sealed class Settings
{
    public bool GameMode { get; set; }
    public bool LockMic { get; set; }
    public string? PreferredMicId { get; set; }
    public bool AutoGame { get; set; }
    public List<string> AutoGameProcesses { get; set; } = new() { "cs2", "TslGame" };

    /// <summary>Listedeki süreç adlarının panelde gösterilen adları (süreç adı -> ad). Adı olmayan süreç adıyla görünür.</summary>
    public Dictionary<string, string> AutoGameLabels { get; set; } = new()
    {
        ["cs2"] = "Counter-Strike 2",
        ["TslGame"] = "PUBG: BATTLEGROUNDS",
    };

    /// <summary>Bir süreç adının gösterilecek adı; kayıtlı değilse süreç adının kendisi.</summary>
    public string LabelFor(string process)
    {
        foreach (var pair in AutoGameLabels)
        {
            if (string.Equals(pair.Key, process, StringComparison.OrdinalIgnoreCase) && pair.Value.Length > 0) return pair.Value;
        }
        return process;
    }

    public void SetLabel(string process, string? label)
    {
        foreach (string key in AutoGameLabels.Keys.Where(k => string.Equals(k, process, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            AutoGameLabels.Remove(key);
        }
        if (!string.IsNullOrWhiteSpace(label)) AutoGameLabels[process] = label.Trim();
    }
    public string? LastAddress { get; set; }

    /// <summary>
    /// Kulaklık kulaktan çıkınca çalan medya durur, geri takılınca devam eder. Varsayılan açık; eski ayar
    /// dosyalarında bu alan olmadığı için onlarda da açık gelir.
    /// </summary>
    public bool AutoPause { get; set; } = true;

    /// <summary>Arayüz dili (ör. "tr", "en"); boşsa Windows'un görüntüleme dili izlenir.</summary>
    public string Language { get; set; } = "";

    /// <summary>
    /// Kulaklığın gecikme tamponu hedefi (ms): 0 = kulaklığın varsayılanı (150), yoksa 90 ya da 60.
    /// Düşük değer oyunlarda sesi öne çeker; bağlantı zayıfsa ses takılabilir.
    /// </summary>
    public int JitterMs { get; set; }

    /// <summary>Bu uygulama tamponu en az bir kez değiştirdi: varsayılan seçiliyken de 150 açıkça gönderilir.</summary>
    public bool JitterTouched { get; set; }

    /// <summary>AirPods mikrofonu köprü servisi üzerinden kullanılsın (el-serbest profili kapatılır, ses stereo kalır).</summary>
    public bool BridgeMic { get; set; }

    /// <summary>
    /// Köprüyü bu ayar dosyasını kullanan uygulama kurdu. Geri alma yalnızca bu işaret varken yapılır; böylece aynı
    /// anda çalışan başka bir kopya (tanı aracı gibi) panelin kurduğu köprüyü bozmaz.
    /// </summary>
    public bool BridgeMicApplied { get; set; }

    /// <summary>El-serbest servisini bu uygulama kapattı: köprü kapanınca ya da uygulamadan çıkınca geri açılır.</summary>
    public bool BridgeMicHandsFreeOff { get; set; }

    /// <summary>Köprü mikrofonu varsayılan yapılmadan önceki varsayılan mikrofonlar (rol numarası -> uç nokta).</summary>
    public Dictionary<int, string>? BridgeMicPrevDefaults { get; set; }

    /// <summary>
    /// Kurulu kopya ilk çalıştığında "Windows ile başlat" bir kez açılır; sonrasında kullanıcının seçimi geçerlidir.
    /// </summary>
    public bool AutostartOffered { get; set; }

    /// <summary>
    /// Kurulum programındaki "Windows ile başlat" seçiminin en son uygulanan damgası: her kurulumun seçimi bir kez
    /// uygulanır, sonrasında kullanıcının paneldeki seçimi geçerlidir.
    /// </summary>
    public string AutostartStamp { get; set; } = "";
    public SavedGameState? Saved { get; set; }
    public Dictionary<string, CachedLink> Links { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(Log.Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Ayarlar okunamadı, varsayılanlar kullanılıyor: " + ex.Message);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            Log.Write("Ayarlar yazılamadı: " + ex.Message);
        }
    }
}
