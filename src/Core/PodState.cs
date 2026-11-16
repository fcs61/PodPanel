using PodPanel.Core.Bridge;

namespace PodPanel.Core;

public sealed class MicOption
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Selected { get; init; }
}

/// <summary>Arayüzün gösterdiği her şeyin anlık görüntüsü.</summary>
public sealed class PodState
{
    // Aygıt
    public bool DeviceFound { get; init; }
    public string DeviceName { get; init; } = "";
    public string ModelName { get; init; } = "";
    public int ProductId { get; init; }
    public string Address { get; init; } = "";
    public bool Connected { get; init; }
    public string OutputEndpointId { get; init; } = "";

    // Windows ses yönlendirmesi
    public bool IsDefaultOutput { get; init; }
    public string DefaultOutputName { get; init; } = "";
    public bool HasMic { get; init; }
    public bool MicDisabled { get; init; }
    public bool IsDefaultMic { get; init; }
    public string DefaultMicName { get; init; } = "";
    public IReadOnlyList<string> MicUsers { get; init; } = Array.Empty<string>();
    public bool Playing { get; init; }

    /// <summary>Bir uygulama AirPods mikrofonunu açmış: kulaklık el-serbest (mono) modda.</summary>
    public bool MonoMode => Connected && MicUsers.Count > 0;

    // BLE yayınından
    public bool BatteryKnown { get; init; }
    public int? BatteryLeft { get; init; }
    public int? BatteryRight { get; init; }
    public int? BatteryCase { get; init; }
    public bool ChargingLeft { get; init; }
    public bool ChargingRight { get; init; }
    public bool ChargingCase { get; init; }
    public bool InEarLeft { get; init; }
    public bool InEarRight { get; init; }
    public bool BothInCase { get; init; }
    public int BatteryAgeSeconds { get; init; }
    public string Activity { get; init; } = "";

    // Kontrol kanalından (köprü servisi üzerinden)
    /// <summary>Köprü servisi kurulu ve çalışıyor.</summary>
    public bool BridgeAvailable { get; init; }
    /// <summary>Servisin bildirdiği durum; durum kanalı kapalıyken null.</summary>
    public BridgeServiceState? BridgeState { get; init; }
    /// <summary>Servis adaptörü devralamıyorsa nedeni: "not-usb", "none" ya da "usbip-missing".</summary>
    public string BridgeDetail { get; init; } = "";
    /// <summary>Kulaklığın kontrol kanalı açık.</summary>
    public bool ControlOpen { get; init; }
    /// <summary>Pil ve kulak durumu kontrol kanalından okundu (yüzde yüzde kesin; BLE yayını onar onar bildirir).</summary>
    public bool BatteryExact { get; init; }
    /// <summary>Kulaklığın bildirdiği dinleme modu.</summary>
    public ListeningMode? ListeningMode { get; init; }
    /// <summary>İstenen ama kulaklığın henüz onaylamadığı mod.</summary>
    public ListeningMode? ListeningModePending { get; init; }
    /// <summary>En az bir kulaklık kulakta; kulaklık modu yalnızca bu durumda değiştirir.</summary>
    public bool Worn { get; init; }

    /// <summary>
    /// Kulaklığa giden sesin ölçülen bit hızı (kbps), köprü servisinin saydığı paket hacminden; ses çalmıyorsa ya da
    /// servis bildirmiyorsa null.
    /// </summary>
    public int? AudioKbps { get; init; }

    /// <summary>Seçili gecikme tamponu hedefi (ms); 0 = kulaklığın varsayılanı.</summary>
    public int JitterMs { get; init; }

    // Mikrofon köprüsü
    /// <summary>Kullanıcı AirPods mikrofonunu köprü üzerinden kullanmayı açtı.</summary>
    public bool BridgeMicOn { get; init; }
    /// <summary>El-serbest kapalı, servis kanalı açık ve sanal mikrofon Windows'ta etkin.</summary>
    public bool BridgeMicReady { get; init; }
    /// <summary>Bir uygulama sanal mikrofondan kayıt alıyor.</summary>
    public bool BridgeMicCapturing { get; init; }
    /// <summary>Kulaklıktan ses akıyor.</summary>
    public bool BridgeMicStreaming { get; init; }
    /// <summary>Sanal mikrofon Windows'un varsayılan mikrofonu.</summary>
    public bool BridgeMicIsDefault { get; init; }
    /// <summary>Kalıcı bir sorun varsa açıklaması.</summary>
    public string BridgeMicNote { get; init; } = "";

    // Sürücü telemetrisinden
    public bool TelemetryAvailable { get; init; }
    public string? Codec { get; init; }
    public int? SampleRate { get; init; }
    public int? Channels { get; init; }
    public int? DelayMs { get; init; }
    public string? HfpCodec { get; init; }
    public bool LinkFromCache { get; init; }
    public IReadOnlyList<string> RemoteCodecs { get; init; } = Array.Empty<string>();
    public int LinkTroubleCount { get; init; }

    // Uzamsal ses
    public bool SpatialSupported { get; init; }
    public bool SpatialOn { get; init; }
    public string SpatialFormat { get; init; } = "";

    // Oyun modu
    public bool GameModeManual { get; init; }
    public bool GameModeActive { get; init; }
    public string GameModeReason { get; init; } = "";
    public bool LockMic { get; init; }
    public bool AutoGame { get; init; }
    public IReadOnlyList<string> AutoGameProcesses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MicOption> Mics { get; init; } = Array.Empty<MicOption>();
    public string? GameMicName { get; init; }
}
