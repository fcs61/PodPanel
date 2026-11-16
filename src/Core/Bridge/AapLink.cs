namespace PodPanel.Core.Bridge;

/// <summary>Kontrol kanalından okunan durumun anlık görüntüsü.</summary>
public sealed class AapSnapshot
{
    /// <summary>Köprü servisinin kanalı bulundu (servis kurulu ve çalışıyor).</summary>
    public bool ServiceAvailable { get; init; }

    /// <summary>Kulaklığın kontrol kanalı açık ve açılış tamamlandı.</summary>
    public bool Open { get; init; }

    /// <summary>Son açma denemesinin Win32 hata kodu; 0 başarılı.</summary>
    public int LastError { get; init; }

    /// <summary>Kulaklığın bildirdiği dinleme modu.</summary>
    public ListeningMode? Mode { get; init; }

    /// <summary>İstenen ama kulaklığın henüz onaylamadığı mod.</summary>
    public ListeningMode? Pending { get; init; }

    public int? Left { get; init; }
    public int? Right { get; init; }
    public int? Case { get; init; }
    public bool LeftCharging { get; init; }
    public bool RightCharging { get; init; }
    public bool CaseCharging { get; init; }

    /// <summary>Kulak durumu bildirildi.</summary>
    public bool EarKnown { get; init; }

    /// <summary>Sol ve sağ kulaklık kulakta mı; hangisinin birincil olduğu bilinmiyorsa null.</summary>
    public bool? LeftInEar { get; init; }
    public bool? RightInEar { get; init; }

    /// <summary>En az bir kulaklık kulakta. Kulaklık, dinleme modunu yalnızca bu durumda değiştirir.</summary>
    public bool Worn { get; init; }

    public bool BothInCase { get; init; }

    /// <summary>Birincil (telefonla ya da bilgisayarla konuşan) kulaklık sol mu; bilinmiyorsa null.</summary>
    public bool? PrimaryIsLeft { get; init; }
}

/// <summary>
/// Kulaklığın kontrol kanalını açık tutar: kulaklık bağlanınca açar, kapanırsa yeniden dener, gelen bildirimlerden
/// durumu derler. Kulaklığın ayarlarına yalnızca <see cref="SetListeningMode"/> dokunur.
/// </summary>
public sealed class AapLink : IDisposable
{
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly Dictionary<byte, BatteryEntry> _battery = new();
    private AapClient? _client;
    private ulong _address;
    private bool _opening;
    private bool _disposed;
    private DateTime _nextAttempt = DateTime.MinValue;

    private bool _serviceAvailable;
    private bool _open;
    private int _lastError;
    private ListeningMode? _mode;
    private ListeningMode? _pending;
    private DateTime _pendingUntil;
    private byte? _earPrimary;
    private byte? _earSecondary;
    private bool? _primaryIsLeft;
    private int _jitterWanted;       // 0 = kulaklığın varsayılanı
    private bool _jitterRestore;     // varsayılan isteniyorsa onu da açıkça gönder (daha önce değiştirildiyse)
    private int _jitterSent;         // bu kanalda son gönderilen değer; 0 = hiç gönderilmedi
    private DateTime _openedAt;

    /// <summary>Durum değişti (herhangi bir iş parçacığından).</summary>
    public event Action? Changed;

    /// <summary>Kulaklıktan gelen mikrofon akışı paketi (işlem 0x58); kontrol kanalının okuma iş parçacığında çağrılır.</summary>
    public event Action<byte[]>? MicPacket;

    /// <summary>Kanal açıksa kulaklığa ham bir paket gönderir (mikrofon akışını başlatma ve durdurma için).</summary>
    public bool SendRaw(ReadOnlySpan<byte> packet)
    {
        AapClient? client;
        lock (_gate)
        {
            if (_client == null || !_open) return false;
            client = _client;
        }
        return client.Send(packet);
    }

    public AapSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                bool earKnown = _earPrimary != null && _earSecondary != null;
                bool primaryIn = _earPrimary == 0x00, secondaryIn = _earSecondary == 0x00;
                _battery.TryGetValue(Aap.LeftComponent, out BatteryEntry left);
                _battery.TryGetValue(Aap.RightComponent, out BatteryEntry right);
                _battery.TryGetValue(Aap.CaseComponent, out BatteryEntry box);
                return new AapSnapshot
                {
                    ServiceAvailable = _serviceAvailable,
                    Open = _open,
                    LastError = _lastError,
                    Mode = _mode,
                    Pending = _pending != null && DateTime.UtcNow <= _pendingUntil ? _pending : null,
                    Left = _battery.ContainsKey(Aap.LeftComponent) && left.Present ? left.Level : null,
                    Right = _battery.ContainsKey(Aap.RightComponent) && right.Present ? right.Level : null,
                    Case = _battery.ContainsKey(Aap.CaseComponent) && box.Present ? box.Level : null,
                    LeftCharging = left.Present && left.Charging,
                    RightCharging = right.Present && right.Charging,
                    CaseCharging = box.Present && box.Charging,
                    EarKnown = earKnown,
                    LeftInEar = earKnown && _primaryIsLeft is { } leftPrimary ? (leftPrimary ? primaryIn : secondaryIn) : null,
                    RightInEar = earKnown && _primaryIsLeft is { } leftPrimary2 ? (leftPrimary2 ? secondaryIn : primaryIn) : null,
                    Worn = earKnown && (primaryIn || secondaryIn),
                    BothInCase = earKnown && _earPrimary == 0x02 && _earSecondary == 0x02,
                    PrimaryIsLeft = _primaryIsLeft,
                };
            }
        }
    }

    /// <summary>
    /// Kulaklığın gecikme tamponu hedefini belirler (ms; 0 = varsayılan). Kanal açıldıktan kısa süre sonra ve her
    /// değişiklikte gönderilir. <paramref name="restoreDefault"/>: varsayılan isteniyorsa 150 açıkça gönderilsin
    /// (daha önce bu uygulama değeri değiştirdiyse).
    /// </summary>
    public void SetJitter(int milliseconds, bool restoreDefault)
    {
        lock (_gate)
        {
            _jitterWanted = milliseconds;
            _jitterRestore = restoreDefault;
        }
        ApplyJitter();
    }

    private void ApplyJitter()
    {
        AapClient? client;
        int target;
        lock (_gate)
        {
            if (_client == null || !_open || (DateTime.UtcNow - _openedAt).TotalMilliseconds < 1500) return;
            if (_jitterWanted == 0 && !_jitterRestore && _jitterSent == 0) return;
            target = _jitterWanted == 0 ? Aap.DefaultJitterMs : _jitterWanted;
            if (target == _jitterSent) return;
            _jitterSent = target;
            client = _client;
        }
        client.Send(Aap.SetJitterBuffer(target));
    }

    /// <summary>
    /// İzleyicinin her yenilemesinde çağrılır: kulaklık bağlıysa kanalı açar (ya da yeniden dener), değilse kapatır.
    /// Engellemez; açma işlemi arka planda yürür.
    /// </summary>
    public void Update(ulong address, bool connected)
    {
        ApplyJitter();
        AapClient? close = null;
        bool start = false;
        lock (_gate)
        {
            if (_disposed) return;
            bool wanted = connected && address != 0;
            if (_client != null && (!wanted || address != _address))
            {
                close = _client;
                _client = null;
                ResetLocked();
            }
            _address = wanted ? address : 0;
            if (wanted && _client == null && !_opening && DateTime.UtcNow >= _nextAttempt)
            {
                _opening = true;
                start = true;
            }
        }
        close?.Dispose();
        if (start) _ = Task.Run(() => OpenAsync(address));
    }

    /// <summary>Dinleme modunu ister; kanal kapalıysa false. Kulaklık onayı ayrı bir bildirimle gelir.</summary>
    public bool SetListeningMode(ListeningMode mode)
    {
        AapClient? client;
        lock (_gate)
        {
            if (_client == null || !_open) return false;
            client = _client;
            _pending = mode;
            _pendingUntil = DateTime.UtcNow + PendingTimeout;
        }
        bool sent = client.Send(Aap.SetListeningMode(mode));
        Changed?.Invoke();
        // Onay gelmezse bekleyen istek zaman aşımına uğrar; arayüz kulaklığın bildirdiği moda dönsün.
        _ = Task.Delay(PendingTimeout + TimeSpan.FromMilliseconds(200)).ContinueWith(_ => Changed?.Invoke(), TaskScheduler.Default);
        return sent;
    }

    private async Task OpenAsync(ulong address)
    {
        AapClient? client = null;
        int error;
        try
        {
            client = AapClient.Open(address, out error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = e.HResult & 0xFFFF;
        }
        if (client == null)
        {
            lock (_gate)
            {
                _opening = false;
                _lastError = error;
                // 2: kanal yok (servis kurulu değil ya da durmuş), 5: kanal servise ait değil.
                _serviceAvailable = error is not (BridgePipes.ErrorNoPipe or BridgePipes.ErrorAccessDenied);
                _nextAttempt = DateTime.UtcNow + TimeSpan.FromSeconds(error switch
                {
                    BridgePipes.ErrorNoPipe => 30,
                    BridgePipes.ErrorBusy => 15,
                    BridgePipes.ErrorNotConnected => 5,
                    _ => 10,
                });
            }
            Changed?.Invoke();
            return;
        }

        // Kulaklık ilk durum dökümünü açılış sırasında gönderir: paketler kanal atanmadan önce kaçmasın.
        bool stale;
        lock (_gate)
        {
            stale = _disposed || _address != address;
            if (!stale)
            {
                _client = client;
                _serviceAvailable = true;
            }
        }
        if (stale)
        {
            client.Dispose();
            lock (_gate) _opening = false;
            return;
        }
        client.Packet += packet => OnPacket(client, packet);
        client.Ended += code => OnEnded(client, code);

        bool ready = await client.InitializeAsync().ConfigureAwait(false);
        bool drop = false;
        lock (_gate)
        {
            _opening = false;
            if (!ReferenceEquals(_client, client)) return; // bu arada kapandı
            if (ready)
            {
                _open = true;
                _openedAt = DateTime.UtcNow;
                _lastError = 0;
            }
            else
            {
                drop = true;
                _client = null;
                ResetLocked();
                _lastError = BridgePipes.ErrorTimeout;
                _nextAttempt = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            }
        }
        if (drop) client.Dispose();
        Changed?.Invoke();
    }

    private void OnPacket(AapClient client, byte[] packet)
    {
        if (Aap.Opcode(packet) == 0x58)
        {
            MicPacket?.Invoke(packet);
            return;
        }
        bool changed = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_client, client)) return;
            if (Aap.TryBattery(packet, out List<BatteryEntry> entries))
            {
                foreach (BatteryEntry entry in entries)
                {
                    if (!_battery.TryGetValue(entry.Component, out BatteryEntry old) || old != entry)
                    {
                        _battery[entry.Component] = entry;
                        changed = true;
                    }
                }
            }
            else if (Aap.TryEar(packet, out byte primary, out byte secondary))
            {
                changed = _earPrimary != primary || _earSecondary != secondary;
                _earPrimary = primary;
                _earSecondary = secondary;
            }
            else if (Aap.TryListeningMode(packet, out ListeningMode mode))
            {
                changed = _mode != mode || _pending != null;
                _mode = mode;
                if (_pending == mode) _pending = null;
            }
            else if (Aap.Opcode(packet) == 0x08 && packet.Length >= 7 && packet[6] is 0x01 or 0x02)
            {
                bool left = packet[6] == 0x01;
                changed = _primaryIsLeft != left;
                _primaryIsLeft = left;
            }
        }
        if (changed) Changed?.Invoke();
    }

    private void OnEnded(AapClient client, int code)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_client, client)) return;
            _client = null;
            ResetLocked();
            _lastError = code;
            _nextAttempt = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        }
        // Bu geri çağrı kanalın okuma döngüsünden gelir; kapatma döngünün bitmesini beklediği için ayrı yürütülür.
        _ = Task.Run(client.Dispose);
        Changed?.Invoke();
    }

    private void ResetLocked()
    {
        _open = false;
        _jitterSent = 0;
        _mode = null;
        _pending = null;
        _earPrimary = null;
        _earSecondary = null;
        _primaryIsLeft = null;
        _battery.Clear();
    }

    public void Dispose()
    {
        AapClient? client;
        bool restore;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            client = _client;
            restore = _open && _jitterSent != 0 && _jitterSent != Aap.DefaultJitterMs;
            _client = null;
            ResetLocked();
        }
        if (client != null && restore)
        {
            // Uygulama kapanırken kulaklık kendi varsayılan tamponuna dönsün.
            client.Send(Aap.SetJitterBuffer(Aap.DefaultJitterMs));
            Thread.Sleep(200);
        }
        client?.Dispose();
    }
}
