using System.Collections.Concurrent;
using System.Diagnostics;
using PodPanel.Core.Audio;
using PodPanel.Core.Bluetooth;
using PodPanel.Core.Bridge;
using PodPanel.Core.Interop;
using PodPanel.Core.Spatial;
using PodPanel.Core.Telemetry;

namespace PodPanel.Core;

/// <summary>
/// Tüm servisleri tek bir arka plan iş parçacığında çalıştırır, durumu derler ve oyun modu kurallarını uygular.
/// Arayüz yalnızca <see cref="StateChanged"/> olayını dinler ve komut yöntemlerini çağırır.
/// </summary>
public sealed class PodMonitor : IDisposable
{
    private const int AppleVendorId = 0x004C;
    private static readonly Role[] Roles = { Role.Console, Role.Multimedia, Role.Communications };

    private readonly Settings _settings;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile bool _uiOpen;

    private AudioService? _audio;
    private BleStatusWatcher? _ble;
    private BtAudioTelemetry? _telemetry;
    private readonly AapLink _aap = new();
    private readonly MicBridge _mic;
    private readonly BridgeStatusClient _bridgeStatus = new();
    private bool? _handsFreeOn;
    private DateTime _handsFreeCheckedAt = DateTime.MinValue;
    private string _bridgeMicNote = "";
    private DateTime _micShownAt = DateTime.MinValue;

    private List<EndpointInfo> _endpoints = new();
    private EndpointInfo? _render;
    private EndpointInfo? _capture;
    private bool _wasConnected;
    private bool _probed;
    private bool _forceMic;
    private bool _spatialDirty = true;
    private bool _spatialErrorLogged;
    private bool _aapWasOpen;
    // Ses kesintilerinin nedenini sonradan ayırabilmek için günlüğe yazılan son durumlar.
    private string _loggedBuds = "";
    private bool _loggedCapturing;
    private int _loggedTrouble;
    private DateTime _connectedAt = DateTime.MinValue;
    private DateTime _lastBurst = DateTime.MinValue;
    private DateTime _lastSpatial = DateTime.MinValue;
    private (bool Supported, string Active) _spatial;

    public event Action<PodState>? StateChanged;

    public PodState Current { get; private set; } = new();

    public Settings Settings => _settings;

    public BtAudioTelemetry? Telemetry => _telemetry;

    /// <summary>Panel açıkken daha sık yenilenir ve BLE sürekli taranır.</summary>
    public bool UiOpen
    {
        get => _uiOpen;
        set
        {
            _uiOpen = value;
            _wake.Set();
        }
    }

    private readonly string _instanceTag;

    /// <param name="instanceTag">Telemetri oturum adına eklenen etiket (tanı aracı ve ekran görüntüsü modu için).</param>
    public PodMonitor(Settings settings, string instanceTag = "")
    {
        _settings = settings;
        // Eski sürümlerde seçilmiş 30 ms, en yakın sunulan değere çekilir.
        if (_settings.JitterMs is > 0 and < 60)
        {
            _settings.JitterMs = 60;
            _settings.Save();
        }
        _instanceTag = instanceTag;
        _thread = new Thread(Run) { IsBackground = true, Name = "PodPanel.Monitor" };
        _aap.Changed += () => _wake.Set();
        _mic = new MicBridge(_aap);
        _mic.Changed += () => _wake.Set();
        _bridgeStatus.Changed += () => _wake.Set();
    }

    public void Start() => _thread.Start();

    public void RefreshNow() => _wake.Set();

    // ---- Komutlar (arayüzden çağrılır, izleyici iş parçacığında çalışır) ----

    public void SetGameMode(bool on) => Post(() =>
    {
        _settings.GameMode = on;
        _settings.Save();
    });

    public void SetLockMic(bool on) => Post(() =>
    {
        _settings.LockMic = on;
        _settings.Save();
    });

    public void SetPreferredMic(string? endpointId) => Post(() =>
    {
        _settings.PreferredMicId = endpointId;
        _settings.Save();
        _forceMic = true;
    });

    public void SetAutoGame(bool on, IEnumerable<string>? processes = null) => Post(() =>
    {
        _settings.AutoGame = on;
        if (processes != null)
        {
            _settings.AutoGameProcesses = processes.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        _settings.Save();
    });

    public void SetSpatial(bool on) => Post(() =>
    {
        if (_render == null) return;
        try
        {
            string result = SpatialAudio.Set(_render.Id, on);
            Log.Write($"Uzamsal ses {(on ? "açma" : "kapatma")} isteği: {result}");
        }
        catch (Exception ex)
        {
            Log.Write("Uzamsal ses değiştirilemedi: " + ex.Message);
        }
        _spatialDirty = true;
    });

    /// <summary>Dinleme modunu ister. Kulaklık yalnızca kulaktayken uygular; onayı durumla birlikte gelir.</summary>
    public void SetListeningMode(ListeningMode mode) => Post(() =>
    {
        bool sent = _aap.SetListeningMode(mode);
        Log.Write(sent ? $"Dinleme modu istendi: {Aap.ModeName(mode)}" : "Dinleme modu gönderilemedi: kontrol kanalı kapalı");
    });

    /// <summary>
    /// AirPods mikrofonunu köprü üzerinden kullanmayı açar ya da kapatır. Açıkken Windows'ta el-serbest servisi
    /// kapatılır ve sanal mikrofon varsayılan yapılır; kapatılınca ikisi de eski hâline döner.
    /// </summary>
    public void SetBridgeMic(bool on) => Post(() =>
    {
        _settings.BridgeMic = on;
        _settings.Save();
        _handsFreeCheckedAt = DateTime.MinValue;
        _bridgeMicNote = "";
        Log.Write(on ? "Mikrofon köprüsü açıldı" : "Mikrofon köprüsü kapatıldı");
    });

    /// <summary>Kulaklığın gecikme tamponu hedefini seçer (ms; 0 = varsayılan). Kontrol kanalı açıkken uygulanır.</summary>
    public void SetJitter(int milliseconds) => Post(() =>
    {
        // 30 ms sunulmaz: Windows'un gönderdiği AAC paketleri yaklaşık 21 ms'lik ses taşır; 30 ms'lik tampon tek bir
        // gecikmiş pakette boşalır ve ses kesik kesik gelir (2026-11-20'de bu kulaklıkta böyle oldu).
        _settings.JitterMs = milliseconds is 60 or 90 ? milliseconds : 0;
        if (_settings.JitterMs != 0) _settings.JitterTouched = true;
        _settings.Save();
        Log.Write(_settings.JitterMs == 0 ? "Gecikme tamponu: varsayılan" : $"Gecikme tamponu: {_settings.JitterMs} ms");
    });

    public void Connect() => Post(() => OneShot(true));

    public void Disconnect() => Post(() => OneShot(false));

    /// <summary>Koruması devreye girmiş servisten Bluetooth adaptörünü yeniden devralmasını ister.</summary>
    public void RetryBridge() => Post(() =>
    {
        Log.Write(_bridgeStatus.Retry() ? "Servisten adaptörü yeniden devralması istendi" : "Servise yeniden deneme isteği gönderilemedi");
    });

    public void MakeDefaultOutput() => Post(() =>
    {
        if (_render == null || _audio == null) return;
        foreach (Role role in Roles) _audio.SetDefault(_render.Id, role);
        Log.Write("Varsayılan çıkış AirPods yapıldı");
    });

    private void Post(Action action)
    {
        _queue.Enqueue(action);
        _wake.Set();
    }

    private void OneShot(bool reconnect)
    {
        if (_render == null || _audio == null) return;
        var filters = _endpoints
            .Where(e => e.IsBluetooth && e.FilterId.Length > 0 &&
                        (e.Id == _render.Id || (_render.ContainerId != Guid.Empty && e.ContainerId == _render.ContainerId)))
            .Select(e => e.FilterId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string filter in filters) _audio.BtOneShot(filter, reconnect);
        Log.Write(reconnect ? "Bağlan isteği gönderildi" : "Bağlantıyı kes isteği gönderildi");
    }

    // ---- Ana döngü ----

    private void Run()
    {
        try
        {
            _audio = new AudioService();
            _audio.DevicesChanged += () => _wake.Set();
            _ble = new BleStatusWatcher();
            _ble.Updated += () => _wake.Set();
            _telemetry = new BtAudioTelemetry();
            _telemetry.Changed += () => _wake.Set();
            _telemetry.Start(_instanceTag);
            _bridgeStatus.Start();
        }
        catch (Exception ex)
        {
            Log.Write("Servisler başlatılamadı: " + ex);
        }

        while (!_stop)
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Log.Write("Komut hatası: " + ex.Message); }
            }

            try { Refresh(); }
            catch (Exception ex) { Log.Write("Yenileme hatası: " + ex); }

            Thread.Sleep(150); // art arda gelen bildirimleri tek yenilemede topla
            _wake.WaitOne(_uiOpen ? 1000 : 2500);
        }

        try { RevertGameMode(); }
        catch (Exception ex) { Log.Write("Oyun modu geri alınamadı: " + ex.Message); }
        // Çıkarken kulaklığın el-serbest mikrofonu ve önceki varsayılan mikrofon geri gelsin; ayar açık kalır ve
        // uygulama yeniden başlayınca köprü yeniden kurulur.
        try { RevertBridgeMic(_endpoints, _settings.LastAddress is { Length: > 0 } last ? Convert.ToUInt64(last, 16) : 0); }
        catch (Exception ex) { Log.Write("Mikrofon köprüsü geri alınamadı: " + ex.Message); }
        _mic.Dispose();
        _bridgeStatus.Dispose();
        _aap.Dispose();
        _telemetry?.Dispose();
        _ble?.Dispose();
        _audio?.Dispose();
    }

    private void Refresh()
    {
        var audio = _audio;
        if (audio == null) return;

        var endpoints = audio.Enumerate();
        _endpoints = endpoints;

        var candidates = endpoints.Where(e => e.Flow == DataFlow.Render && e.Bt?.VendorId == AppleVendorId).ToList();
        var render = candidates.FirstOrDefault(e => e.IsActive && e.JackConnected)
                     ?? candidates.FirstOrDefault(e => string.Equals(e.Bt!.Address, _settings.LastAddress, StringComparison.OrdinalIgnoreCase))
                     ?? candidates.FirstOrDefault();

        bool telemetryAvailable = _telemetry?.Available ?? false;
        if (render == null)
        {
            _render = null;
            _capture = null;
            _wasConnected = false;
            _aap.Update(0, false);
            _mic.Configure(false, 0, "");
            var idle = MaintainGameMode(endpoints, null);
            Publish(new PodState
            {
                TelemetryAvailable = telemetryAvailable,
                GameModeManual = _settings.GameMode,
                GameModeActive = idle.Active,
                GameModeReason = idle.Reason,
                LockMic = _settings.LockMic,
                AutoGame = _settings.AutoGame,
                AutoGameProcesses = _settings.AutoGameProcesses.ToList(),
            });
            return;
        }

        var capture = endpoints.FirstOrDefault(e => e.Flow == DataFlow.Capture && render.ContainerId != Guid.Empty && e.ContainerId == render.ContainerId)
                      ?? endpoints.FirstOrDefault(e => e.Flow == DataFlow.Capture && e.IsHandsFree && render.DeviceName.Length > 0
                                                       && e.DeviceName.StartsWith(render.DeviceName, StringComparison.OrdinalIgnoreCase));
        _render = render;
        _capture = capture;
        var bt = render.Bt!;

        bool connected = render.IsActive && render.JackConnected;
        if (connected && !_wasConnected)
        {
            _connectedAt = DateTime.Now;
            _probed = false;
            _spatialDirty = true;
            _lastBurst = DateTime.MinValue;
            if (!string.Equals(_settings.LastAddress, bt.Address, StringComparison.OrdinalIgnoreCase))
            {
                _settings.LastAddress = bt.Address;
                _settings.Save();
            }
            Log.Write($"{render.DeviceName} bağlı");
        }
        else if (!connected && _wasConnected)
        {
            Log.Write($"{render.DeviceName} bağlantısı kesildi");
        }
        _wasConnected = connected;

        // Windows ses yönlendirmesi
        string? defaultRender = audio.GetDefaultId(DataFlow.Render, Role.Console);
        string? defaultCapture = audio.GetDefaultId(DataFlow.Capture, Role.Console);
        string defaultRenderName = endpoints.FirstOrDefault(e => e.Id == defaultRender)?.Name ?? "";
        string defaultCaptureName = endpoints.FirstOrDefault(e => e.Id == defaultCapture)?.Name ?? "";

        var micUsers = connected && capture is { IsActive: true }
            ? audio.GetActiveSessions(capture.Id).Select(s => s.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        bool playing = connected && audio.GetPeak(render.Id) > 0.0001f;

        // BLE: panel açıkken sürekli, değilken 45 saniyede bir 8 saniyelik tarama
        PodAdvert? advert = null;
        if (_ble != null)
        {
            _ble.Continuous = _uiOpen;
            if ((DateTime.Now - _lastBurst).TotalSeconds > 45)
            {
                _ble.Burst(8);
                _lastBurst = DateTime.Now;
            }
            advert = _ble.Latest(bt.ProductId);
        }

        // Sürücü telemetrisi
        ulong address = Convert.ToUInt64(bt.Address, 16);
        var link = _telemetry?.Get(address);

        // Kontrol kanalı: açıksa pil ve kulak durumu buradan gelir (BLE yayınından daha kesin ve anlık).
        _aap.SetJitter(_settings.JitterMs, _settings.JitterTouched);
        _aap.Update(address, connected);
        AapSnapshot aap = _aap.Snapshot;
        bool exact = aap.Open && (aap.Left != null || aap.Right != null);
        bool aapEar = aap.Open && aap.EarKnown && aap.LeftInEar != null && aap.RightInEar != null;
        if (aap.Open != _aapWasOpen)
        {
            _aapWasOpen = aap.Open;
            Log.Write(aap.Open ? "Kontrol kanalı açıldı" : $"Kontrol kanalı kapalı: {BridgePipes.DescribeError(aap.LastError)}");
        }
        if (connected && telemetryAvailable && link?.CodecId == null && !_probed && !playing
            && (DateTime.Now - _connectedAt).TotalSeconds > 3)
        {
            // Codec yalnızca bağlantı kurulurken ya da akış kapanırken bildirilir: kısa bir sessiz akışla tetikle.
            // Akışın gerçekten başlaması yarım saniye kadar sürdüğü için süre bundan uzun olmalı.
            _probed = true;
            audio.ProbeSilence(render.Id, 2000);
        }

        _settings.Links.TryGetValue(bt.Address, out var cache);
        if (link?.CodecId != null)
        {
            var fresh = new CachedLink
            {
                Codec = link.CodecName,
                SampleRate = link.SampleRate ?? cache?.SampleRate,
                Channels = link.Channels ?? cache?.Channels,
                DelayMs = link.DelayMs ?? cache?.DelayMs,
                HfpCodec = link.HfpCodecName ?? cache?.HfpCodec,
                Updated = DateTime.Now,
            };
            if (cache == null || cache.Codec != fresh.Codec || cache.SampleRate != fresh.SampleRate || cache.Channels != fresh.Channels
                || cache.DelayMs != fresh.DelayMs || cache.HfpCodec != fresh.HfpCodec)
            {
                _settings.Links[bt.Address] = fresh;
                _settings.Save();
            }
            cache = fresh;
        }
        else if (link != null && cache != null && (link.DelayMs != null || link.HfpCodec != null))
        {
            cache.DelayMs = link.DelayMs ?? cache.DelayMs;
            cache.HfpCodec = link.HfpCodecName ?? cache.HfpCodec;
        }

        // Uzamsal ses
        if (connected && (_spatialDirty || (DateTime.Now - _lastSpatial).TotalSeconds > (_uiOpen ? 3 : 20)))
        {
            try
            {
                _spatial = SpatialAudio.Get(render.Id);
            }
            catch (Exception ex)
            {
                if (!_spatialErrorLogged)
                {
                    _spatialErrorLogged = true;
                    Log.Write("Uzamsal ses durumu okunamadı: " + ex.Message);
                }
            }
            _spatialDirty = false;
            _lastSpatial = DateTime.Now;
        }

        var bridgeMic = MaintainBridgeMic(endpoints, address, connected, aap, CleanName(render.DeviceName, render.Name));

        // Kesinti tanısı için olay kayıtları: kulaklık rolü ve kulak durumu, mikrofon kullanımı, bağlantı tıkanmaları.
        if (aap.Open && aapEar)
        {
            string buds = $"birincil {(aap.PrimaryIsLeft == true ? "sol" : aap.PrimaryIsLeft == false ? "sağ" : "?")}, " +
                          $"sol {(aap.LeftInEar == true ? "kulakta" : "dışarıda")}, sağ {(aap.RightInEar == true ? "kulakta" : "dışarıda")}";
            if (buds != _loggedBuds)
            {
                _loggedBuds = buds;
                Log.Write("Kulaklık: " + buds);
            }
        }
        if (bridgeMic.Status.Capturing != _loggedCapturing)
        {
            _loggedCapturing = bridgeMic.Status.Capturing;
            Log.Write(_loggedCapturing ? "Mikrofon kullanımda (kulaklıktan akış isteniyor)" : "Mikrofon bırakıldı");
        }
        int trouble = link == null ? 0 : link.Overflows + link.LatePackets + link.Backoffs;
        if (trouble != _loggedTrouble)
        {
            if (trouble > _loggedTrouble) Log.Write($"Ses bağlantısında tıkanma: +{trouble - _loggedTrouble} (taşma {link!.Overflows}, geç paket {link.LatePackets}, geri çekilme {link.Backoffs})");
            _loggedTrouble = trouble;
        }
        var game = MaintainGameMode(endpoints, capture);

        var micOptions = endpoints
            .Where(e => e.Flow == DataFlow.Capture && e.IsActive && (capture == null || e.Id != capture.Id))
            .Select(e => new MicOption { Id = e.Id, Name = e.Name, Selected = game.Alt != null && e.Id == game.Alt.Id })
            .ToList();

        int age = advert == null ? int.MaxValue : (int)(DateTime.Now - advert.Time).TotalSeconds;
        bool advertFresh = advert != null && age < 180;

        Publish(new PodState
        {
            DeviceFound = true,
            DeviceName = CleanName(render.DeviceName, render.Name),
            ModelName = ProximityPairing.ModelName(bt.ProductId),
            ProductId = bt.ProductId,
            Address = bt.Address,
            Connected = connected,
            OutputEndpointId = render.Id,

            IsDefaultOutput = defaultRender != null && defaultRender == render.Id,
            DefaultOutputName = defaultRenderName,
            HasMic = capture != null,
            MicDisabled = capture is { IsDisabled: true },
            IsDefaultMic = capture != null && defaultCapture != null && defaultCapture == capture.Id,
            DefaultMicName = defaultCaptureName,
            MicUsers = micUsers,
            Playing = playing,

            BatteryKnown = exact || advert != null,
            BatteryLeft = exact ? aap.Left : advert?.Left,
            BatteryRight = exact ? aap.Right : advert?.Right,
            BatteryCase = (exact ? aap.Case : null) ?? advert?.Case,
            ChargingLeft = exact ? aap.LeftCharging : advert?.LeftCharging ?? false,
            ChargingRight = exact ? aap.RightCharging : advert?.RightCharging ?? false,
            ChargingCase = exact && aap.Case != null ? aap.CaseCharging : advert?.CaseCharging ?? false,
            InEarLeft = aapEar ? aap.LeftInEar!.Value : advertFresh && advert!.LeftInEar,
            InEarRight = aapEar ? aap.RightInEar!.Value : advertFresh && advert!.RightInEar,
            BothInCase = aapEar ? aap.BothInCase : advertFresh && advert!.BothInCase,
            BatteryAgeSeconds = exact ? 0 : age,

            BridgeAvailable = aap.ServiceAvailable,
            BridgeState = _bridgeStatus.State,
            BridgeDetail = _bridgeStatus.Detail,
            ControlOpen = aap.Open,
            BatteryExact = exact,
            ListeningMode = aap.Mode,
            ListeningModePending = aap.Pending,
            Worn = aap.Open && aap.Worn,

            // Ses çalarken en yoğun kanal A2DP ortam kanalıdır; çalmıyorken ölçüm gösterilmez.
            AudioKbps = connected && playing && _bridgeStatus.Get(address, TimeSpan.FromSeconds(5)) is { } volume && volume.AudioKbps >= 32
                ? (int)Math.Round(volume.AudioKbps)
                : null,

            JitterMs = _settings.JitterMs,

            BridgeMicOn = _settings.BridgeMic,
            BridgeMicReady = bridgeMic.Ready,
            BridgeMicCapturing = bridgeMic.Status.Capturing,
            BridgeMicStreaming = bridgeMic.Status.Streaming,
            BridgeMicIsDefault = bridgeMic.Endpoint != null && defaultCapture == bridgeMic.Endpoint.Id,
            BridgeMicNote = bridgeMic.Status.Error.Length > 0 ? bridgeMic.Status.Error : _bridgeMicNote,
            Activity = advertFresh && connected ? ProximityPairing.ActivityName(advert!.ConnectionState) : "",

            TelemetryAvailable = telemetryAvailable,
            Codec = link?.CodecName ?? cache?.Codec,
            SampleRate = link?.SampleRate ?? cache?.SampleRate,
            Channels = link?.Channels ?? cache?.Channels,
            DelayMs = link?.DelayMs ?? cache?.DelayMs,
            HfpCodec = link?.HfpCodecName ?? cache?.HfpCodec,
            LinkFromCache = link?.CodecId == null && cache?.Codec != null,
            RemoteCodecs = link?.RemoteCodecs ?? new List<string>(),
            LinkTroubleCount = link == null ? 0 : link.Overflows + link.LatePackets + link.Backoffs,

            SpatialSupported = _spatial.Supported,
            SpatialOn = connected && SpatialAudio.IsOn(_spatial.Active),
            SpatialFormat = SpatialAudio.FormatName(_spatial.Active),

            GameModeManual = _settings.GameMode,
            GameModeActive = game.Active,
            GameModeReason = game.Reason,
            LockMic = _settings.LockMic,
            AutoGame = _settings.AutoGame,
            AutoGameProcesses = _settings.AutoGameProcesses.ToList(),
            Mics = micOptions,
            GameMicName = game.Alt?.Name,
        });
    }

    /// <summary>"Kulaklıklar (Fatih (AirPods Pro) - Find My)" gibi adlardan kısa bir başlık üretir.</summary>
    private static string CleanName(string deviceName, string endpointName)
    {
        string name = deviceName.Length > 0 ? deviceName : endpointName;
        const string suffix = " - Find My";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];
        return name;
    }

    private void Publish(PodState state)
    {
        Current = state;
        StateChanged?.Invoke(state);
    }

    // ---- Mikrofon köprüsü ----

    /// <summary>Servisin sanal mikrofonu (USB 1209:0001; adı "Headset (A2DP, ...)").</summary>
    private static bool IsBridgeMic(EndpointInfo e) =>
        e.Flow == DataFlow.Capture
        && (e.FilterId.Contains("vid_1209&pid_0001", StringComparison.OrdinalIgnoreCase)
            || e.DeviceName.Contains("Headset (A2DP", StringComparison.OrdinalIgnoreCase)
            || e.Name.Contains("Headset (A2DP", StringComparison.OrdinalIgnoreCase));

    private (bool Ready, MicBridgeStatus Status, EndpointInfo? Endpoint) MaintainBridgeMic(
        List<EndpointInfo> endpoints, ulong address, bool connected, AapSnapshot aap, string name)
    {
        EndpointInfo? virtualMic = endpoints.FirstOrDefault(IsBridgeMic);
        // Köprü servisi bu bilgisayarda kurulu değilse (köprüsüz sürüm ya da kaldırılmış köprü) mikrofon köprüsü de
        // olamaz: önceden kapatılmış el-serbest servisi ve varsayılan mikrofon geri alınır.
        if (!_settings.BridgeMic || !BridgeServiceInstalled())
        {
            _mic.Configure(false, 0, "");
            RevertBridgeMic(endpoints, address);
            return (false, _mic.Status, virtualMic);
        }
        if (!connected || !aap.Open || _audio == null)
        {
            // Kulaklık ya da kontrol kanalı yok: akış istenemez. Windows ayarlarına dokunulmaz.
            _mic.Configure(false, 0, "");
            return (false, _mic.Status, virtualMic);
        }

        // 1. El-serbest kapalı olmalı: akış sürerken el-serbest bağlantısı başlarsa kulaklık bağlantıyı düşürür.
        if (_handsFreeOn != false || (DateTime.Now - _handsFreeCheckedAt).TotalSeconds > 20)
        {
            if ((DateTime.Now - _handsFreeCheckedAt).TotalSeconds > 5)
            {
                _handsFreeCheckedAt = DateTime.Now;
                _handsFreeOn = HandsFree.IsEnabled(address);
                if (_handsFreeOn == true)
                {
                    if (HandsFree.SetEnabled(address, false, out string error))
                    {
                        _handsFreeOn = false;
                        _settings.BridgeMicHandsFreeOff = true;
                        _settings.Save();
                        _bridgeMicNote = "";
                        Log.Write("El-serbest servisi kapatıldı (mikrofon köprüsü için)");
                    }
                    else
                    {
                        _bridgeMicNote = error;
                        Log.Write("El-serbest servisi kapatılamadı: " + error);
                    }
                }
                else if (_handsFreeOn == null)
                {
                    _bridgeMicNote = "El-serbest servisinin durumu okunamadı";
                }
            }
        }
        bool handsFreeOff = _handsFreeOn == false;
        if (handsFreeOff && !_settings.BridgeMicApplied)
        {
            _settings.BridgeMicApplied = true;
            _settings.Save();
        }
        _mic.Configure(handsFreeOff, address, name);

        // 2. Sanal mikrofon: gizlenmişse göster, ilk kez etkinleştiğinde varsayılan yap (önceki varsayılanı sakla).
        if (handsFreeOff && virtualMic != null)
        {
            if (virtualMic.IsDisabled)
            {
                // Başka bir program uç noktayı yeniden gizlerse sürekli açıp kapatma döngüsüne girilmesin.
                if ((DateTime.Now - _micShownAt).TotalSeconds > 5)
                {
                    _micShownAt = DateTime.Now;
                    if (_audio.SetVisibility(virtualMic.Id, true)) Log.Write("Köprü mikrofonu etkinleştirildi");
                }
            }
            else if (virtualMic.IsActive && _settings.BridgeMicPrevDefaults == null)
            {
                var previous = new Dictionary<int, string>();
                foreach (Role role in Roles)
                {
                    string? current = _audio.GetDefaultId(DataFlow.Capture, role);
                    if (current != null && current != virtualMic.Id) previous[(int)role] = current;
                    _audio.SetDefault(virtualMic.Id, role);
                }
                _settings.BridgeMicPrevDefaults = previous;
                _settings.Save();
                Log.Write($"Varsayılan mikrofon → {virtualMic.Name}");
            }
        }
        MicBridgeStatus status = _mic.Status;
        return (handsFreeOff && status.PipeConnected && virtualMic is { IsActive: true }, status, virtualMic);
    }

    /// <summary>Köprünün Windows'ta değiştirdiklerini geri alır: varsayılan mikrofon, sanal mikrofonun görünürlüğü, el-serbest.</summary>
    private DateTime _bridgeCheckedAt = DateTime.MinValue;
    private bool _bridgeInstalled;

    /// <summary>Köprü servisi kayıtlı mı (yarım dakikada bir yeniden bakılır).</summary>
    private bool BridgeServiceInstalled()
    {
        if ((DateTime.Now - _bridgeCheckedAt).TotalSeconds < 30) return _bridgeInstalled;
        _bridgeCheckedAt = DateTime.Now;
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WPodsService");
            _bridgeInstalled = key != null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _bridgeInstalled = true;
        }
        return _bridgeInstalled;
    }

    private void RevertBridgeMic(List<EndpointInfo> endpoints, ulong address)
    {
        // Yalnızca köprüyü kuran kopya geri alır.
        if (!_settings.BridgeMicApplied && !_settings.BridgeMicHandsFreeOff && _settings.BridgeMicPrevDefaults == null) return;
        EndpointInfo? virtualMic = endpoints.FirstOrDefault(IsBridgeMic);
        if (_settings.BridgeMicPrevDefaults is { } previous && _audio != null)
        {
            foreach ((int role, string id) in previous)
            {
                string? current = _audio.GetDefaultId(DataFlow.Capture, (Role)role);
                bool stillOurs = virtualMic != null && current == virtualMic.Id;
                if (stillOurs && endpoints.Any(e => e.Id == id && e.IsActive)) _audio.SetDefault(id, (Role)role);
            }
            _settings.BridgeMicPrevDefaults = null;
            _settings.Save();
            Log.Write("Varsayılan mikrofon eski hâline döndürüldü");
        }
        // Köprü kapalıyken sanal mikrofon yalnızca sessizlik verir: uygulamalar seçmesin diye gizlenir.
        if (virtualMic is { IsActive: true } && _audio != null && _audio.SetVisibility(virtualMic.Id, false))
        {
            Log.Write("Köprü mikrofonu gizlendi");
        }
        if (_settings.BridgeMicHandsFreeOff && address != 0)
        {
            if (HandsFree.SetEnabled(address, true, out string error))
            {
                Log.Write("El-serbest servisi geri açıldı");
                _settings.BridgeMicHandsFreeOff = false;
                _settings.Save();
            }
            else
            {
                Log.Write("El-serbest servisi geri açılamadı: " + error);
            }
            _handsFreeOn = null;
            _handsFreeCheckedAt = DateTime.MinValue;
        }
        if (_settings.BridgeMicApplied && !_settings.BridgeMicHandsFreeOff)
        {
            _settings.BridgeMicApplied = false;
            _settings.Save();
        }
    }

    // ---- Oyun modu ----

    private (bool Active, string Reason, EndpointInfo? Alt) MaintainGameMode(List<EndpointInfo> endpoints, EndpointInfo? capture)
    {
        string? running = _settings.AutoGame ? FindRunning(_settings.AutoGameProcesses) : null;
        bool want = _settings.GameMode || running != null;
        string reason = _settings.GameMode ? "elle açıldı" : running != null ? running + " çalışıyor" : "";
        EndpointInfo? alt = capture != null ? PickAltMic(endpoints, capture) : null;

        if (want) ApplyGameMode(capture, alt);
        else if (_settings.Saved != null) RevertGameMode();
        return (want, reason, alt);
    }

    private static string? FindRunning(IEnumerable<string> names)
    {
        foreach (string raw in names)
        {
            string name = raw.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Length == 0) continue;
            try
            {
                var processes = Process.GetProcessesByName(name);
                bool any = processes.Length > 0;
                foreach (var process in processes) process.Dispose();
                if (any) return name;
            }
            catch
            {
                // Süreç listesi okunamazsa bu adı atla.
            }
        }
        return null;
    }

    private EndpointInfo? PickAltMic(List<EndpointInfo> endpoints, EndpointInfo capture)
    {
        var others = endpoints
            .Where(e => e.Flow == DataFlow.Capture && e.IsActive && e.Id != capture.Id
                        && (capture.ContainerId == Guid.Empty || e.ContainerId != capture.ContainerId))
            .ToList();
        return others.FirstOrDefault(e => e.Id == _settings.PreferredMicId)
               ?? others.FirstOrDefault(e => !e.IsBluetooth)
               ?? others.FirstOrDefault();
    }

    private void ApplyGameMode(EndpointInfo? capture, EndpointInfo? alt)
    {
        if (capture == null || _audio == null) return;
        bool dirty = false;
        var saved = _settings.Saved;
        if (saved == null)
        {
            saved = new SavedGameState { AirPodsCaptureId = capture.Id };
            _settings.Saved = saved;
            dirty = true;
            Log.Write("Oyun modu açıldı");
        }

        // Varsayılan mikrofonu AirPods'tan uzak tut: mikrofon açılmazsa kulaklık stereo kalır.
        foreach (Role role in Roles)
        {
            string? current = _audio.GetDefaultId(DataFlow.Capture, role);
            bool isAirPods = string.Equals(current, capture.Id, StringComparison.OrdinalIgnoreCase);
            if (isAirPods && !saved.RolesToRestore.Contains((int)role))
            {
                saved.RolesToRestore.Add((int)role);
                dirty = true;
            }
            bool switchToAlt = alt != null && (isAirPods || (_forceMic && !string.Equals(current, alt.Id, StringComparison.OrdinalIgnoreCase)));
            if (switchToAlt && _audio.SetDefault(alt!.Id, role)) Log.Write($"Varsayılan mikrofon ({role}) → {alt.Name}");
        }
        _forceMic = false;

        if (_settings.LockMic)
        {
            if (capture.IsActive && _audio.SetVisibility(capture.Id, false))
            {
                saved.DisabledEndpointId = capture.Id;
                dirty = true;
                Log.Write("AirPods mikrofonu kilitlendi (devre dışı)");
            }
        }
        else if (saved.DisabledEndpointId != null)
        {
            _audio.SetVisibility(saved.DisabledEndpointId, true);
            saved.DisabledEndpointId = null;
            dirty = true;
            Log.Write("AirPods mikrofonunun kilidi kaldırıldı");
        }

        if (dirty) _settings.Save();
    }

    private void RevertGameMode()
    {
        var saved = _settings.Saved;
        if (saved == null || _audio == null) return;

        if (saved.DisabledEndpointId != null)
        {
            _audio.SetVisibility(saved.DisabledEndpointId, true);
            Thread.Sleep(600); // uç noktanın yeniden etkinleşmesini bekle
        }

        string? captureId = saved.AirPodsCaptureId;
        if (captureId != null && saved.RolesToRestore.Count > 0)
        {
            var endpoint = _audio.Enumerate().FirstOrDefault(e => string.Equals(e.Id, captureId, StringComparison.OrdinalIgnoreCase));
            if (endpoint is { IsActive: true })
            {
                foreach (int role in saved.RolesToRestore) _audio.SetDefault(captureId, (Role)role);
            }
        }

        _settings.Saved = null;
        _settings.Save();
        Log.Write("Oyun modu kapandı; mikrofon ayarları geri alındı");
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _wake.Set();
        // Çıkışta el-serbest servisinin geri açılması birkaç saniye sürebilir.
        if (_thread.IsAlive) _thread.Join(12000);
    }
}
