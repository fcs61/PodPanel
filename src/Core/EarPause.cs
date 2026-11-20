using Windows.Media.Control;

namespace PodPanel.Core;

/// <summary>
/// Kulaklık kulaktan çıkınca çalan medyayı durdurur, geri takılınca devam ettirir (Apple'ın "Otomatik Kulak
/// Algılama"sı gibi). Windows'un medya oturumlarını kullanır: Spotify, tarayıcılar ve medya tuşlarına yanıt veren
/// diğer oynatıcılar. Oyunların böyle bir oturumu yoktur; onlara dokunulmaz.
/// Yalnızca kendi durdurduğu ve hâlâ duraklatılmış olan oturumları devam ettirir.
/// </summary>
public sealed class EarPause
{
    /// <summary>Bundan uzun süre önce durdurulmuş medya, kulaklık takılınca kendiliğinden başlamaz.</summary>
    private static readonly TimeSpan ResumeWindow = TimeSpan.FromMinutes(10);

    /// <summary>Yanıt vermeyen bir oynatıcı izleyiciyi bekletmesin.</summary>
    private const int CallTimeoutMs = 2000;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly List<string> _paused = new();
    private DateTime _pausedAt = DateTime.MinValue;
    private int? _lastCount;
    private bool _errorLogged;

    /// <param name="enabled">Özellik açık mı.</param>
    /// <param name="inEar">Kulaktaki kulaklık sayısı (0-2); bilinmiyorsa null.</param>
    /// <param name="audible">Ses bu kulaklıktan çıkıyor (Windows'un varsayılan çıkışı).</param>
    public void Update(bool enabled, int? inEar, bool audible)
    {
        int? before = _lastCount;
        _lastCount = inEar;
        if (!enabled)
        {
            _paused.Clear();
            return;
        }
        // Durum bilinmiyorken (kontrol kanalı yeni açıldı ya da kapandı) geçiş sayılmaz.
        if (inEar is not { } now || before is not { } was || now == was) return;

        if (now < was)
        {
            if (!audible) return;
            var stopped = PausePlaying();
            if (stopped.Count > 0) Log.Write("Kulaklık çıkarıldı; durdurulan: " + string.Join(", ", stopped));
        }
        else
        {
            var resumed = ResumePaused();
            if (resumed.Count > 0) Log.Write("Kulaklık takıldı; devam ettirilen: " + string.Join(", ", resumed));
        }
    }

    /// <summary>Bağlantı kesildi: bekleyen "devam ettir" unutulur.</summary>
    public void Reset()
    {
        _lastCount = null;
        _paused.Clear();
    }

    /// <summary>Çalan bütün oturumları durdurur; durdurulanların uygulama kimliklerini döndürür.</summary>
    public List<string> PausePlaying()
    {
        var stopped = new List<string>();
        try
        {
            foreach (var session in Sessions())
            {
                if (session.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
                if (!Wait(session.TryPauseAsync().AsTask())) continue;
                string id = session.SourceAppUserModelId ?? "";
                stopped.Add(id);
                if (!_paused.Contains(id)) _paused.Add(id);
            }
        }
        catch (Exception ex)
        {
            LogOnce("Medya durdurulamadı: " + ex.Message);
        }
        if (stopped.Count > 0) _pausedAt = DateTime.Now;
        return stopped;
    }

    /// <summary>Bu sınıfın durdurduğu ve hâlâ duraklatılmış olan oturumları devam ettirir.</summary>
    public List<string> ResumePaused()
    {
        var resumed = new List<string>();
        if (_paused.Count == 0) return resumed;
        var wanted = _paused.ToList();
        _paused.Clear();
        if (DateTime.Now - _pausedAt > ResumeWindow) return resumed;
        try
        {
            foreach (var session in Sessions())
            {
                string id = session.SourceAppUserModelId ?? "";
                if (!wanted.Contains(id)) continue;
                // Kullanıcı bu arada kendisi başlattıysa ya da oynatıcıyı kapattıysa dokunma.
                if (session.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused) continue;
                if (Wait(session.TryPlayAsync().AsTask())) resumed.Add(id);
            }
        }
        catch (Exception ex)
        {
            LogOnce("Medya devam ettirilemedi: " + ex.Message);
        }
        return resumed;
    }

    /// <summary>Tanı için: medya oturumları ve durumları.</summary>
    public List<string> Describe()
    {
        var lines = new List<string>();
        foreach (var session in Sessions())
        {
            lines.Add($"{session.SourceAppUserModelId}: {session.GetPlaybackInfo()?.PlaybackStatus}");
        }
        return lines;
    }

    private IReadOnlyList<GlobalSystemMediaTransportControlsSession> Sessions()
    {
        _manager ??= Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask());
        return _manager?.GetSessions() ?? (IReadOnlyList<GlobalSystemMediaTransportControlsSession>)Array.Empty<GlobalSystemMediaTransportControlsSession>();
    }

    private static T? Wait<T>(Task<T> task) => task.Wait(CallTimeoutMs) ? task.Result : default;

    private void LogOnce(string text)
    {
        if (_errorLogged) return;
        _errorLogged = true;
        Log.Write(text);
    }
}
