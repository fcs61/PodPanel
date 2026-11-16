using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PodPanel.Core;
using PodPanel.Core.Bridge;

namespace PodPanel;

public sealed class MicItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsSelected { get; init; }
}

/// <summary>Panelin bağlandığı görünüm modeli: PodState'i metne çevirir, tıklamaları izleyiciye iletir.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private const string GameTipDefault = "Varsayılan mikrofonu AirPods'tan uzak tutar; böylece kulaklık mono moda düşmez.";

    private enum BannerKind { None, OtherOutput, Mono, Mic, BridgeGaveUp, BridgeUnsupported, BridgeStopped }

    private BannerKind _bannerKind;

    private readonly PodMonitor? _monitor;
    private PodState _state = new();
    private bool _applying;
    private DateTime _userActionAt = DateTime.MinValue;
    private DateTime _modeActionAt = DateTime.MinValue;
    private DateTime _jitterActionAt = DateTime.MinValue;
    private string _micSignature = "";
    private string _lastStateList = "";
    private bool _bridgeInstalled;
    private DateTime _bridgeCheckedAt = DateTime.MinValue;

    public MainViewModel(PodMonitor? monitor, Dispatcher dispatcher)
    {
        _monitor = monitor;
        ToggleConnectionCommand = new RelayCommand(_ =>
        {
            if (_state.Connected) _monitor?.Disconnect();
            else _monitor?.Connect();
        });
        MakeDefaultCommand = new RelayCommand(_ => _monitor?.MakeDefaultOutput());
        BannerCommand = new RelayCommand(_ =>
        {
            if (_bannerKind == BannerKind.OtherOutput) _monitor?.MakeDefaultOutput();
            else if (_bannerKind == BannerKind.BridgeGaveUp) _monitor?.RetryBridge();
        });
        ToggleDetailsCommand = new RelayCommand(_ => ShowDetails = !ShowDetails);
        SelectMicCommand = new RelayCommand(parameter =>
        {
            if (parameter is string id) _monitor?.SetPreferredMic(id);
        });
        SetJitterCommand = new RelayCommand(parameter =>
        {
            if (!int.TryParse(parameter as string, out int milliseconds) || !ShowModes) return;
            _jitterActionAt = DateTime.Now;
            ShowJitter(milliseconds);
            _monitor?.SetJitter(milliseconds);
        });
        SetModeCommand = new RelayCommand(parameter =>
        {
            ListeningMode? mode = (parameter as string) switch
            {
                "transparency" => ListeningMode.Transparency,
                "adaptive" => ListeningMode.Adaptive,
                "nc" => ListeningMode.NoiseCancellation,
                _ => null,
            };
            if (mode is not { } wanted || !ModesEnabled) return;
            // Onay bir saniye kadar sürer; seçim hemen görünsün.
            _modeActionAt = DateTime.Now;
            ShowMode(wanted);
            _monitor?.SetListeningMode(wanted);
        });
        _startWithWindows = StartupRegistration.IsEnabled();
        if (monitor != null)
        {
            monitor.StateChanged += state => dispatcher.BeginInvoke(new Action(() => Apply(state)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand ToggleConnectionCommand { get; }
    public ICommand MakeDefaultCommand { get; }
    public ICommand BannerCommand { get; }
    public ICommand ToggleDetailsCommand { get; }
    public ICommand SelectMicCommand { get; }
    public ICommand SetModeCommand { get; }
    public ICommand SetJitterCommand { get; }

    private ICommand? _exitCommand;
    public ICommand? ExitCommand
    {
        get => _exitCommand;
        set => Set(ref _exitCommand, value);
    }

    public ObservableCollection<MicItem> Mics { get; } = new();

    // ---- Başlık ----
    private string _deviceTitle = "AirPods aranıyor…";
    public string DeviceTitle { get => _deviceTitle; private set => Set(ref _deviceTitle, value); }

    private string _statusText = "";
    /// <summary>Başlığın altındaki tek satır: bağlı mı, kulakta mı.</summary>
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private bool _showConnect;
    public bool ShowConnect { get => _showConnect; private set => Set(ref _showConnect, value); }

    // ---- Pil ----
    private string _batteryLeftText = "—";
    public string BatteryLeftText { get => _batteryLeftText; private set => Set(ref _batteryLeftText, value); }

    private string _batteryRightText = "—";
    public string BatteryRightText { get => _batteryRightText; private set => Set(ref _batteryRightText, value); }

    private string _batteryCaseText = "—";
    public string BatteryCaseText { get => _batteryCaseText; private set => Set(ref _batteryCaseText, value); }

    private bool _isChargingLeft;
    public bool IsChargingLeft { get => _isChargingLeft; private set => Set(ref _isChargingLeft, value); }

    private bool _isChargingRight;
    public bool IsChargingRight { get => _isChargingRight; private set => Set(ref _isChargingRight, value); }

    private bool _isChargingCase;
    public bool IsChargingCase { get => _isChargingCase; private set => Set(ref _isChargingCase, value); }

    private string? _batteryTip;
    /// <summary>Pil bilgisi eskiyse ya da yoksa açıklaması; yoksa null (ipucu çıkmaz).</summary>
    public string? BatteryTip { get => _batteryTip; private set => Set(ref _batteryTip, value); }

    // ---- Uyarı satırı ----
    private string _bannerText = "";
    /// <summary>Yalnızca kullanıcının bilmesi gereken bir durum varken dolu.</summary>
    public string BannerText { get => _bannerText; private set => Set(ref _bannerText, value); }

    private string _bannerAction = "";
    public string BannerAction { get => _bannerAction; private set => Set(ref _bannerAction, value); }

    private string? _bannerTip;
    public string? BannerTip { get => _bannerTip; private set => Set(ref _bannerTip, value); }

    // ---- Ayarlar bölümü ----
    private bool _showDetails;
    public bool ShowDetails
    {
        get => _showDetails;
        set
        {
            if (Set(ref _showDetails, value)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsGlyph)));
        }
    }

    /// <summary>Ayarlar bağlantısının oku: aşağı (kapalı) ya da yukarı (açık).</summary>
    public string DetailsGlyph => _showDetails ? "\uE70E" : "\uE70D";

    private string _audioModeText = "—";
    public string AudioModeText { get => _audioModeText; private set => Set(ref _audioModeText, value); }

    private string _delayText = "—";
    public string DelayText { get => _delayText; private set => Set(ref _delayText, value); }

    private string _micText = "—";
    public string MicText { get => _micText; private set => Set(ref _micText, value); }

    private string _linkNote = "";
    public string LinkNote { get => _linkNote; private set => Set(ref _linkNote, value); }

    private string _trayTooltip = "PodPanel";
    public string TrayTooltip { get => _trayTooltip; private set => Set(ref _trayTooltip, value); }

    // ---- Durum bayrakları ----
    private bool _isFound;
    public bool IsFound { get => _isFound; private set => Set(ref _isFound, value); }

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }

    private bool _isMono;
    public bool IsMono { get => _isMono; private set => Set(ref _isMono, value); }

    private bool _hasMics;
    public bool HasMics { get => _hasMics; private set => Set(ref _hasMics, value); }

    private bool _gameModeAuto;
    /// <summary>Oyun modu elle değil, listedeki bir uygulama çalıştığı için açık.</summary>
    public bool GameModeAuto { get => _gameModeAuto; private set => Set(ref _gameModeAuto, value); }

    private string _gameTip = GameTipDefault;
    public string GameTip { get => _gameTip; private set => Set(ref _gameTip, value); }

    private bool _spatialEnabled;
    public bool SpatialEnabled { get => _spatialEnabled; private set => Set(ref _spatialEnabled, value); }

    // ---- Gecikme tamponu ----
    private bool _isJitterDefault = true;
    public bool IsJitterDefault { get => _isJitterDefault; private set => Set(ref _isJitterDefault, value); }

    private bool _isJitter90;
    public bool IsJitter90 { get => _isJitter90; private set => Set(ref _isJitter90, value); }

    private bool _isJitter60;
    public bool IsJitter60 { get => _isJitter60; private set => Set(ref _isJitter60, value); }

    private bool _isJitter30;
    public bool IsJitter30 { get => _isJitter30; private set => Set(ref _isJitter30, value); }

    private void ShowJitter(int milliseconds)
    {
        IsJitterDefault = milliseconds == 0;
        IsJitter90 = milliseconds == 90;
        IsJitter60 = milliseconds == 60;
        IsJitter30 = milliseconds == 30;
    }

    // ---- Dinleme modu ----
    private bool _showModes;
    /// <summary>Kontrol kanalı açık: mod ve gecikme düğmeleri gösterilir.</summary>
    public bool ShowModes { get => _showModes; private set => Set(ref _showModes, value); }

    private bool _modesEnabled;
    /// <summary>Kulaklık kulakta: mod değiştirilebilir.</summary>
    public bool ModesEnabled { get => _modesEnabled; private set => Set(ref _modesEnabled, value); }

    private bool _isModeTransparency;
    public bool IsModeTransparency { get => _isModeTransparency; private set => Set(ref _isModeTransparency, value); }

    private bool _isModeAdaptive;
    public bool IsModeAdaptive { get => _isModeAdaptive; private set => Set(ref _isModeAdaptive, value); }

    private bool _isModeNoiseCancellation;
    public bool IsModeNoiseCancellation { get => _isModeNoiseCancellation; private set => Set(ref _isModeNoiseCancellation, value); }

    private void ShowMode(ListeningMode? mode)
    {
        IsModeTransparency = mode == ListeningMode.Transparency;
        IsModeAdaptive = mode == ListeningMode.Adaptive;
        IsModeNoiseCancellation = mode == ListeningMode.NoiseCancellation;
    }

    // ---- Kullanıcının değiştirdiği ayarlar ----
    private bool _gameMode;
    public bool GameMode
    {
        get => _gameMode;
        set
        {
            if (!Set(ref _gameMode, value) || _applying) return;
            _userActionAt = DateTime.Now;
            _monitor?.SetGameMode(value);
        }
    }

    private bool _lockMic;
    public bool LockMic
    {
        get => _lockMic;
        set
        {
            if (!Set(ref _lockMic, value) || _applying) return;
            _userActionAt = DateTime.Now;
            _monitor?.SetLockMic(value);
        }
    }

    private bool _autoGame;
    public bool AutoGame
    {
        get => _autoGame;
        set
        {
            if (!Set(ref _autoGame, value) || _applying) return;
            _userActionAt = DateTime.Now;
            _monitor?.SetAutoGame(value);
        }
    }

    private string _autoGameList = "";
    public string AutoGameList
    {
        get => _autoGameList;
        set
        {
            if (!Set(ref _autoGameList, value) || _applying) return;
            _monitor?.SetAutoGame(_autoGame, value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }

    private bool _bridgeMic;
    public bool BridgeMic
    {
        get => _bridgeMic;
        set
        {
            if (!Set(ref _bridgeMic, value) || _applying) return;
            _userActionAt = DateTime.Now;
            _monitor?.SetBridgeMic(value);
        }
    }

    private bool _showBridgeMic;
    /// <summary>Köprü servisi var: mikrofon kutusu gösterilir.</summary>
    public bool ShowBridgeMic { get => _showBridgeMic; private set => Set(ref _showBridgeMic, value); }

    private bool _spatialOn;
    public bool SpatialOn
    {
        get => _spatialOn;
        set
        {
            if (!Set(ref _spatialOn, value) || _applying) return;
            _userActionAt = DateTime.Now;
            _monitor?.SetSpatial(value);
        }
    }

    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!Set(ref _startWithWindows, value)) return;
            try { StartupRegistration.Set(value); }
            catch (Exception ex) { Log.Write("Başlangıç ayarı yazılamadı: " + ex.Message); }
        }
    }

    /// <summary>İzleyiciden gelen durumu görünüme uygular (arayüz iş parçacığında).</summary>
    public void Apply(PodState s)
    {
        _state = s;
        _applying = true;
        try
        {
            IsFound = s.DeviceFound;
            IsConnected = s.Connected;
            IsMono = s.MonoMode;
            ShowConnect = s.DeviceFound && !s.Connected;

            bool batteryFresh = s.BatteryKnown && s.BatteryAgeSeconds <= 180;
            DeviceTitle = s.DeviceFound ? s.DeviceName : "AirPods bulunamadı";
            StatusText = !s.DeviceFound ? "Eşleştirilmiş AirPods yok"
                : !s.Connected ? "Bağlı değil"
                : "Bağlı" + Place(s, s.ControlOpen || batteryFresh);

            BatteryLeftText = Percent(s.BatteryLeft);
            BatteryRightText = Percent(s.BatteryRight);
            BatteryCaseText = Percent(s.BatteryCase);
            IsChargingLeft = batteryFresh && s.ChargingLeft && s.BatteryLeft != null;
            IsChargingRight = batteryFresh && s.ChargingRight && s.BatteryRight != null;
            IsChargingCase = batteryFresh && s.ChargingCase && s.BatteryCase != null;
            BatteryTip = !s.DeviceFound ? null
                : !s.BatteryKnown ? "Pil bilgisi bekleniyor"
                : s.BatteryAgeSeconds > 180 ? $"{s.BatteryAgeSeconds / 60} dk önce okundu"
                : null;

            ApplyBanner(s);

            AudioModeText = !s.Connected ? "—"
                : s.MonoMode ? "Mono · el-serbest" + (s.HfpCodec != null ? " · " + s.HfpCodec : "")
                : "Stereo" + (s.Codec != null ? " · " + s.Codec : "") + (s.SampleRate != null ? $" {s.SampleRate / 1000.0:0.#} kHz" : "")
                  + (s.AudioKbps != null ? $" · {s.AudioKbps} kbps" : "");
            DelayText = s.DelayMs != null ? $"{s.DelayMs} ms" : "—";
            MicText = s.BridgeMicOn && s.BridgeMicReady ? "AirPods · köprü"
                : !s.HasMic ? "—"
                : s.MonoMode ? "Kullanan: " + string.Join(", ", s.MicUsers)
                : s.MicDisabled ? "AirPods kilitli"
                : s.IsDefaultMic ? "AirPods · el-serbest"
                : s.DefaultMicName;
            LinkNote = !s.TelemetryAvailable ? "Codec ve gecikme okunamıyor (sürücü telemetrisi kapalı)"
                : s.LinkTroubleCount > 0 ? $"Bağlantıda {s.LinkTroubleCount} tıkanma görüldü"
                : "";

            ShowBridgeMic = s.DeviceFound && (s.BridgeAvailable || s.BridgeMicOn);
            GameModeAuto = s.GameModeActive && !s.GameModeManual;
            GameTip = GameModeAuto ? "Kendiliğinden açık: " + s.GameModeReason : GameTipDefault;
            SpatialEnabled = s.Connected && s.SpatialSupported;

            ShowModes = s.Connected && s.ControlOpen;
            ModesEnabled = ShowModes && s.Worn;
            // Az önce bir mod seçildiyse, istek izleyiciye ulaşana kadar eski modu geri yazma.
            if (s.ListeningModePending != null || (DateTime.Now - _modeActionAt).TotalMilliseconds > 600)
            {
                ShowMode(s.ListeningModePending ?? s.ListeningMode);
            }
            if ((DateTime.Now - _jitterActionAt).TotalMilliseconds > 1200) ShowJitter(s.JitterMs);

            // Kullanıcı az önce bir anahtara dokunduysa, komut işlenene kadar eski durumu geri yazma.
            if ((DateTime.Now - _userActionAt).TotalMilliseconds > 1200)
            {
                GameMode = s.GameModeManual;
                BridgeMic = s.BridgeMicOn;
                LockMic = s.LockMic;
                AutoGame = s.AutoGame;
                SpatialOn = s.SpatialOn;
            }

            string list = string.Join(", ", s.AutoGameProcesses);
            if (list != _lastStateList)
            {
                _lastStateList = list;
                AutoGameList = list;
            }

            UpdateMics(s.Mics);

            TrayTooltip = !s.DeviceFound ? "PodPanel — AirPods bulunamadı"
                : !s.Connected ? $"{s.DeviceName} — bağlı değil"
                : $"{s.DeviceName} — Sol {Percent(s.BatteryLeft)}, Sağ {Percent(s.BatteryRight)}"
                  + (s.ListeningMode is { } shown ? " · " + Aap.ModeName(shown) : "") + (s.MonoMode ? " · mono" : "");
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>Tek satırlık uyarı: en önemli tek durumu gösterir, sorun yoksa boş kalır.</summary>
    private void ApplyBanner(PodState s)
    {
        string text = "", action = "";
        string? tip = null;
        BannerKind kind = BannerKind.None;
        if (s.Connected && !s.IsDefaultOutput)
        {
            kind = BannerKind.OtherOutput;
            text = "Ses başka aygıttan çıkıyor";
            action = "AirPods'a al";
            tip = s.DefaultOutputName.Length > 0 ? s.DefaultOutputName : null;
        }
        else if (s.MonoMode)
        {
            kind = BannerKind.Mono;
            text = "Ses mono · mikrofonu kullanan: " + string.Join(", ", s.MicUsers);
            tip = "Bir uygulama AirPods'un el-serbest mikrofonunu açtı. Mikrofon ya da Oyun modu kutusu bunu önler.";
        }
        else if (s.BridgeMicOn && s.BridgeMicNote.Length > 0)
        {
            kind = BannerKind.Mic;
            text = s.BridgeMicNote;
        }
        else if (s.BridgeMicOn && s.BridgeMicCapturing && !s.BridgeMicStreaming)
        {
            kind = BannerKind.Mic;
            text = s.Worn ? "Mikrofon: kulaklıktan ses bekleniyor" : "Mikrofon için kulaklığı tak";
        }
        else if (s.DeviceFound && s.BridgeState == BridgeServiceState.Native)
        {
            kind = BannerKind.BridgeGaveUp;
            text = "Köprü Bluetooth'u devralamadı";
            action = "Yeniden dene";
            tip = "Servis üç kez denedi ve adaptörü Windows'a geri verdi. Kulaklık çalışır; dinleme modu, gecikme ve mikrofon çalışmaz.";
        }
        else if (s.DeviceFound && s.BridgeState == BridgeServiceState.Unsupported)
        {
            kind = BannerKind.BridgeUnsupported;
            text = s.BridgeDetail switch
            {
                "not-usb" => "Bluetooth adaptörü USB değil; köprü çalışamaz",
                "usbip-missing" => "usbip-win2 kurulu değil; köprü çalışamaz",
                _ => "Köprü bir Bluetooth adaptörü bulamadı",
            };
        }
        else if (s.Connected && !s.BridgeAvailable && BridgeInstalled())
        {
            kind = BannerKind.BridgeStopped;
            text = "Köprü servisi çalışmıyor";
            tip = "Dinleme modu, gecikme ve mikrofon köprü servisi üzerinden çalışır. Kurulumdan sonra bilgisayarı yeniden başlat.";
        }
        _bannerKind = kind;
        BannerText = text;
        BannerAction = action;
        BannerTip = tip;
    }

    /// <summary>Köprü servisi bu bilgisayarda kurulu mu (yarım dakikada bir yeniden bakılır).</summary>
    private bool BridgeInstalled()
    {
        if ((DateTime.Now - _bridgeCheckedAt).TotalSeconds < 30) return _bridgeInstalled;
        _bridgeCheckedAt = DateTime.Now;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WPodsService");
            _bridgeInstalled = key != null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            _bridgeInstalled = false;
        }
        return _bridgeInstalled;
    }

    private void UpdateMics(IReadOnlyList<MicOption> options)
    {
        string signature = string.Join("|", options.Select(o => o.Id + (o.Selected ? "*" : "")));
        if (signature == _micSignature) return;
        _micSignature = signature;
        Mics.Clear();
        foreach (var option in options) Mics.Add(new MicItem { Id = option.Id, Name = option.Name, IsSelected = option.Selected });
        HasMics = Mics.Count > 0;
    }

    private static string Percent(int? value) => value == null ? "—" : "%" + value;

    /// <summary>Kulaklıkların yeri, durum satırına eklenecek biçimde; bilinmiyorsa boş.</summary>
    private static string Place(PodState s, bool known)
    {
        if (!known) return "";
        if (s.InEarLeft && s.InEarRight) return " · kulakta";
        if (s.InEarLeft || s.InEarRight || s.Worn) return " · tek kulakta";
        if (s.BothInCase) return " · kutuda";
        return " · kulakta değil";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
