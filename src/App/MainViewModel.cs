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

/// <summary>Dil seçicideki bir satır; boş kod "Windows'un dili" demektir.</summary>
public sealed class LanguageItem
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsSelected { get; init; }
}

/// <summary>Oyun modu listesindeki ya da eklenebilecek açık uygulamalardaki bir satır.</summary>
public sealed class GameItem
{
    /// <summary>Süreç adı, uzantısız (ör. cs2).</summary>
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string Exe => Name + ".exe";
}

/// <summary>Panelin bağlandığı görünüm modeli: PodState'i metne çevirir, tıklamaları izleyiciye iletir.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{

    private enum BannerKind { None, OtherOutput, Mono, Mic, BridgeGaveUp, BridgeUnsupported, BridgeStopped }

    private BannerKind _bannerKind;

    private readonly PodMonitor? _monitor;
    private PodState _state = new();
    private bool _applying;
    private DateTime _userActionAt = DateTime.MinValue;
    private DateTime _modeActionAt = DateTime.MinValue;
    private DateTime _jitterActionAt = DateTime.MinValue;
    private DateTime _volumeActionAt = DateTime.MinValue;
    private string _micSignature = "";
    private string _lastGames = "";
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
        ToggleDetailsCommand = new RelayCommand(_ =>
        {
            ShowDetails = !ShowDetails;
            if (!ShowDetails)
            {
                ClosePicker();
                ShowLanguages = false;
            }
        });
        ToggleLanguagesCommand = new RelayCommand(_ => ShowLanguages = !ShowLanguages);
        SelectLanguageCommand = new RelayCommand(parameter =>
        {
            if (parameter is not string code) return;
            ShowLanguages = false;
            Loc.Use(code);
            _monitor?.SetLanguage(Loc.Requested);
            RefreshLanguages();
        });
        ToggleMuteCommand = new RelayCommand(_ =>
        {
            _volumeActionAt = DateTime.Now;
            Muted = !Muted;
            _monitor?.SetMuted(Muted);
        });
        TogglePickerCommand = new RelayCommand(_ =>
        {
            if (ShowPicker) ClosePicker();
            else OpenPicker();
        });
        AddGameCommand = new RelayCommand(parameter =>
        {
            if (parameter is GameItem item) AddGame(item);
        });
        RemoveGameCommand = new RelayCommand(parameter =>
        {
            if (parameter is string name) RemoveGame(name);
        });
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
        // Dil değişince hesaplanan metinler (durum, ipuçları, uyarı satırı) son durumdan yeniden üretilir.
        Loc.Changed += () => dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshLanguages();
            NotifyVolume();
            Apply(_state);
        }));
        RefreshLanguages();
        if (monitor != null)
        {
            monitor.StateChanged += state => dispatcher.BeginInvoke(new Action(() => Apply(state)));
            monitor.VolumeChanged += (level, muted) => dispatcher.BeginInvoke(new Action(() => ShowSystemVolume(level, muted)));
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
    public ICommand TogglePickerCommand { get; }
    public ICommand AddGameCommand { get; }
    public ICommand RemoveGameCommand { get; }
    public ICommand ToggleLanguagesCommand { get; }
    public ICommand SelectLanguageCommand { get; }
    public ICommand ToggleMuteCommand { get; }

    private ICommand? _exitCommand;
    public ICommand? ExitCommand
    {
        get => _exitCommand;
        set => Set(ref _exitCommand, value);
    }

    public ObservableCollection<MicItem> Mics { get; } = new();

    // ---- Başlık ----
    private string _deviceTitle = Loc.T("searching");
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

    private string _gameTip = Loc.T("game_tip_default");
    public string GameTip { get => _gameTip; private set => Set(ref _gameTip, value); }

    private bool _spatialEnabled;
    public bool SpatialEnabled { get => _spatialEnabled; private set => Set(ref _spatialEnabled, value); }

    private string _spatialTip = "";
    /// <summary>Uzamsal kutusunun ipucu: hangi biçimin açık olduğu ya da açılacağı.</summary>
    public string SpatialTip { get => _spatialTip; private set => Set(ref _spatialTip, value); }

    private string _spatialText = "—";
    /// <summary>Ayarlar bölümündeki satır: açık olan uzamsal ses biçimi.</summary>
    public string SpatialText { get => _spatialText; private set => Set(ref _spatialText, value); }

    // ---- Gecikme tamponu ----
    private bool _isJitterDefault = true;
    public bool IsJitterDefault { get => _isJitterDefault; private set => Set(ref _isJitterDefault, value); }

    private bool _isJitter90;
    public bool IsJitter90 { get => _isJitter90; private set => Set(ref _isJitter90, value); }

    private bool _isJitter60;
    public bool IsJitter60 { get => _isJitter60; private set => Set(ref _isJitter60, value); }


    private void ShowJitter(int milliseconds)
    {
        IsJitterDefault = milliseconds == 0;
        IsJitter90 = milliseconds == 90;
        IsJitter60 = milliseconds == 60;
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

    private bool _autoPause = true;
    public bool AutoPause
    {
        get => _autoPause;
        set
        {
            if (!Set(ref _autoPause, value) || _applying) return;
            _userActionAt = DateTime.Now;
            _monitor?.SetAutoPause(value);
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

    // ---- Oyun modunu kendiliğinden açan oyunlar ----

    /// <summary>Çalışırken oyun modunu açan oyunlar.</summary>
    public ObservableCollection<GameItem> Games { get; } = new();

    /// <summary>"Oyun ekle" açıkken: penceresi açık olan ve listede olmayan uygulamalar.</summary>
    public ObservableCollection<GameItem> OpenApps { get; } = new();

    private bool _noGames;
    public bool NoGames { get => _noGames; private set => Set(ref _noGames, value); }

    private bool _showPicker;
    public bool ShowPicker { get => _showPicker; private set => Set(ref _showPicker, value); }

    private bool _pickerEmpty;
    public bool PickerEmpty { get => _pickerEmpty; private set => Set(ref _pickerEmpty, value); }

    private void OpenPicker()
    {
        IReadOnlyList<RunningApp> apps;
        try
        {
            apps = RunningApps.List();
        }
        catch (Exception ex)
        {
            Log.Write("Açık uygulamalar listelenemedi: " + ex.Message);
            apps = Array.Empty<RunningApp>();
        }
        ShowApps(apps);
    }

    /// <summary>Seçiciyi verilen uygulamalarla açar; listede zaten olanlar gösterilmez.</summary>
    public void ShowApps(IReadOnlyList<RunningApp> apps)
    {
        OpenApps.Clear();
        foreach (var app in apps)
        {
            if (Games.Any(g => string.Equals(g.Name, app.Process, StringComparison.OrdinalIgnoreCase))) continue;
            OpenApps.Add(new GameItem { Name = app.Process, Label = app.Label });
        }
        PickerEmpty = OpenApps.Count == 0;
        ShowPicker = true;
    }

    public void ClosePicker()
    {
        ShowPicker = false;
        OpenApps.Clear();
    }

    private void AddGame(GameItem item)
    {
        ClosePicker();
        if (Games.Any(g => string.Equals(g.Name, item.Name, StringComparison.OrdinalIgnoreCase))) return;
        Games.Add(item);
        NoGames = false;
        // Oyun ekleyen kişi bunun çalışmasını bekler: kutucuk kapalıysa açılır.
        _userActionAt = DateTime.Now;
        AutoGame = true;
        _monitor?.AddAutoGame(item.Name, item.Label);
    }

    private void RemoveGame(string name)
    {
        var item = Games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        Games.Remove(item);
        NoGames = Games.Count == 0;
        _monitor?.RemoveAutoGame(name);
    }

    private void UpdateGames(PodState s)
    {
        var items = s.AutoGameProcesses
            .Select((name, index) => new GameItem { Name = name, Label = index < s.AutoGameLabels.Count ? s.AutoGameLabels[index] : name })
            .ToList();
        // Yalnızca izleyicinin bildirdiği liste değişince yenile: az önce eklenen oyun, komut işlenene kadar silinmesin.
        string signature = string.Join("|", items.Select(i => i.Name + "=" + i.Label));
        if (signature == _lastGames) return;
        _lastGames = signature;
        Games.Clear();
        foreach (var item in items) Games.Add(item);
        NoGames = Games.Count == 0;
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

    // ---- Ses düzeyi (Windows'un ses çubuğuyla aynı değer) ----
    private bool _showVolume;
    public bool ShowVolume { get => _showVolume; private set => Set(ref _showVolume, value); }

    private double _volume;
    public double Volume
    {
        get => _volume;
        set
        {
            double level = Math.Round(Math.Clamp(value, 0, 100));
            if (!Set(ref _volume, level)) return;
            NotifyVolume();
            if (_applying) return;
            _volumeActionAt = DateTime.Now;
            _monitor?.SetVolume((int)level);
            // Windows'un çubuğundaki gibi: düzeyi değiştirmek sessizi kaldırır.
            if (Muted && level > 0)
            {
                Muted = false;
                _monitor?.SetMuted(false);
            }
        }
    }

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        private set
        {
            if (Set(ref _muted, value)) NotifyVolume();
        }
    }

    public string VolumeText => ((int)_volume).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Hoparlör simgesi: sessizde üstü çizili, yoksa düzeye göre dalga sayısı.</summary>
    public string VolumeGlyph => ((char)(_muted ? 0xE74F : _volume <= 0 ? 0xE992 : _volume < 34 ? 0xE993 : _volume < 67 ? 0xE994 : 0xE995)).ToString();

    public string MuteTip => Loc.T(_muted ? "unmute" : "mute");

    private void NotifyVolume()
    {
        Notify(nameof(VolumeText));
        Notify(nameof(VolumeGlyph));
        Notify(nameof(MuteTip));
    }

    /// <summary>Düzey başka bir yerden değişti: Windows'un çubuğu, klavye tuşları ya da kulaklığın sapı.</summary>
    private void ShowSystemVolume(int level, bool muted)
    {
        if ((DateTime.Now - _volumeActionAt).TotalMilliseconds < 400) return;
        bool applying = _applying;
        _applying = true;
        try
        {
            Volume = level;
            Muted = muted;
        }
        finally
        {
            _applying = applying;
        }
    }

    // ---- Dil ----
    public ObservableCollection<LanguageItem> Languages { get; } = new();

    private bool _showLanguages;
    public bool ShowLanguages
    {
        get => _showLanguages;
        set
        {
            if (Set(ref _showLanguages, value)) Notify(nameof(LanguagesGlyph));
        }
    }

    public string LanguagesGlyph => ((char)(_showLanguages ? 0xE70E : 0xE70D)).ToString();

    /// <summary>"Dil: Türkçe" ya da "Dil: Windows'un dili".</summary>
    public string LanguageLabel => Loc.T("language") + ": "
        + (Loc.Requested.Length == 0 ? Loc.T("language_auto") : Loc.Languages.FirstOrDefault(l => l.Code == Loc.Requested).Name ?? Loc.Requested);

    private void RefreshLanguages()
    {
        Languages.Clear();
        Languages.Add(new LanguageItem { Code = "", Name = Loc.T("language_auto"), IsSelected = Loc.Requested.Length == 0 });
        foreach (var (code, name) in Loc.Languages)
        {
            Languages.Add(new LanguageItem { Code = code, Name = name, IsSelected = Loc.Requested == code });
        }
        Notify(nameof(LanguageLabel));
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
            DeviceTitle = s.DeviceFound ? s.DeviceName : Loc.T("not_found");
            StatusText = !s.DeviceFound ? Loc.T("no_paired")
                : !s.Connected ? Loc.T("not_connected")
                : Loc.T("connected") + Place(s, s.ControlOpen || batteryFresh);

            BatteryLeftText = Percent(s.BatteryLeft);
            BatteryRightText = Percent(s.BatteryRight);
            BatteryCaseText = Percent(s.BatteryCase);
            IsChargingLeft = batteryFresh && s.ChargingLeft && s.BatteryLeft != null;
            IsChargingRight = batteryFresh && s.ChargingRight && s.BatteryRight != null;
            IsChargingCase = batteryFresh && s.ChargingCase && s.BatteryCase != null;
            BatteryTip = !s.DeviceFound ? null
                : !s.BatteryKnown ? Loc.T("battery_waiting")
                : s.BatteryAgeSeconds > 180 ? Loc.T("battery_age", s.BatteryAgeSeconds / 60)
                : null;

            ApplyBanner(s);

            AudioModeText = !s.Connected ? "—"
                : s.MonoMode ? Loc.T("mono_handsfree") + (s.HfpCodec != null ? " · " + s.HfpCodec : "")
                : Loc.T("stereo") + (s.Codec != null ? " · " + s.Codec : "") + (s.SampleRate != null ? $" {s.SampleRate / 1000.0:0.#} kHz" : "")
                  + (s.AudioKbps != null ? $" · {s.AudioKbps} kbps" : "");
            DelayText = s.DelayMs != null ? $"{s.DelayMs} ms" : "—";
            MicText = s.BridgeMicOn && s.BridgeMicReady ? Loc.T("mic_bridge")
                : !s.HasMic ? "—"
                : s.MonoMode ? Loc.T("mic_used_by", string.Join(", ", s.MicUsers))
                : s.MicDisabled ? Loc.T("mic_locked")
                : s.IsDefaultMic ? Loc.T("mic_handsfree")
                : s.DefaultMicName;
            LinkNote = !s.TelemetryAvailable ? Loc.T("link_no_telemetry")
                : s.LinkTroubleCount > 0 ? Loc.T("link_trouble", s.LinkTroubleCount)
                : "";

            ShowBridgeMic = s.DeviceFound && s.BridgeAvailable;
            GameModeAuto = s.GameModeActive && !s.GameModeManual;
            GameTip = GameModeAuto ? Loc.T("game_auto_on", s.GameModeReason) : Loc.T("game_tip_default");
            SpatialEnabled = s.Connected && s.SpatialSupported;
            SpatialText = !s.Connected ? "—" : s.SpatialOn ? s.SpatialFormat : Loc.T("off");
            SpatialTip = s.SpatialOn
                ? Loc.T("spatial_on_tip", s.SpatialFormat)
                  + (s.SpatialAtmosAvailable && s.SpatialFormat != "Dolby Atmos" ? ". " + Loc.T("spatial_switch_atmos") : "")
                : s.SpatialAtmosAvailable ? Loc.T("spatial_tip_atmos")
                : Loc.T("spatial_tip_sonic");

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
                AutoPause = s.AutoPause;
                AutoGame = s.AutoGame;
                SpatialOn = s.SpatialOn;
            }

            UpdateGames(s);

            ShowVolume = s.Connected && s.Volume != null;
            // Kullanıcı az önce çubuğu sürüklediyse, ondan önce okunmuş bir durum çubuğu geri çekmesin.
            if (s.Volume is { } level && (DateTime.Now - _volumeActionAt).TotalMilliseconds > 1000)
            {
                Volume = level;
                Muted = s.Muted;
            }

            UpdateMics(s.Mics);

            TrayTooltip = !s.DeviceFound ? Loc.T("tray_not_found")
                : !s.Connected ? Loc.T("tray_not_connected", s.DeviceName)
                : Loc.T("tray_battery", s.DeviceName, Percent(s.BatteryLeft), Percent(s.BatteryRight))
                  + (s.ListeningMode is { } shown ? " · " + Aap.ModeName(shown) : "") + (s.MonoMode ? " · " + Loc.T("mono") : "");
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
            text = Loc.T("banner_other_output");
            action = Loc.T("banner_take");
            tip = s.DefaultOutputName.Length > 0 ? s.DefaultOutputName : null;
        }
        else if (s.MonoMode)
        {
            kind = BannerKind.Mono;
            text = Loc.T("banner_mono", string.Join(", ", s.MicUsers));
            tip = Loc.T("banner_mono_tip");
        }
        else if (s.BridgeMicOn && s.BridgeMicNote.Length > 0)
        {
            kind = BannerKind.Mic;
            text = s.BridgeMicNote;
        }
        else if (s.BridgeMicOn && s.BridgeMicCapturing && !s.BridgeMicStreaming)
        {
            kind = BannerKind.Mic;
            text = Loc.T(s.Worn ? "banner_mic_waiting" : "banner_mic_wear");
        }
        else if (s.DeviceFound && s.BridgeState == BridgeServiceState.Native)
        {
            kind = BannerKind.BridgeGaveUp;
            text = Loc.T("banner_gave_up");
            action = Loc.T("retry");
            tip = Loc.T("banner_gave_up_tip");
        }
        else if (s.DeviceFound && s.BridgeState == BridgeServiceState.Unsupported)
        {
            kind = BannerKind.BridgeUnsupported;
            text = s.BridgeDetail switch
            {
                "not-usb" => Loc.T("banner_not_usb"),
                "usbip-missing" => Loc.T("banner_usbip_missing"),
                _ => Loc.T("banner_no_adapter"),
            };
        }
        else if (s.Connected && !s.BridgeAvailable && BridgeInstalled())
        {
            kind = BannerKind.BridgeStopped;
            text = Loc.T("banner_stopped");
            tip = Loc.T("banner_stopped_tip");
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

    private static string Percent(int? value) => value == null ? "—" : Loc.T("percent", value);

    /// <summary>Kulaklıkların yeri, durum satırına eklenecek biçimde; bilinmiyorsa boş.</summary>
    private static string Place(PodState s, bool known)
    {
        if (!known) return "";
        if (s.InEarLeft && s.InEarRight) return " · " + Loc.T("place_both");
        if (s.InEarLeft || s.InEarRight || s.Worn) return " · " + Loc.T("place_one");
        if (s.BothInCase) return " · " + Loc.T("place_case");
        return " · " + Loc.T("place_out");
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
