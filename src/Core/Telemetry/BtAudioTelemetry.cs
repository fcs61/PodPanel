using PodPanel.Core.Interop;

namespace PodPanel.Core.Telemetry;

/// <summary>Sürücü telemetrisinden derlenen bağlantı bilgisi.</summary>
public sealed class LinkInfo
{
    public int? CodecId { get; set; }
    public int? VendorId { get; set; }
    public int? VendorCodecId { get; set; }
    public int? SampleRate { get; set; }
    public int? Channels { get; set; }
    public int? DelayMs { get; set; }
    public List<string> RemoteCodecs { get; set; } = new();
    public int? HfpCodec { get; set; }
    public string? HfpOffered { get; set; }
    public bool Streaming { get; set; }
    public int Overflows { get; set; }
    public int LatePackets { get; set; }
    public int Backoffs { get; set; }

    public string? CodecName => CodecId == null ? null : BtAudioTelemetry.CodecName(CodecId.Value, VendorId ?? 0, VendorCodecId ?? 0);

    public string? HfpCodecName => HfpCodec switch
    {
        null => null,
        1 => "CVSD (8 kHz)",
        2 => "mSBC (16 kHz)",
        3 => "LC3-SWB (32 kHz)",
        _ => $"codec {HfpCodec}",
    };

    public LinkInfo Clone()
    {
        var copy = (LinkInfo)MemberwiseClone();
        copy.RemoteCodecs = new List<string>(RemoteCodecs);
        return copy;
    }
}

/// <summary>
/// Microsoft'un A2DP ve el-serbest sürücülerinin TraceLogging sağlayıcılarını dinler:
/// kullanılan codec, kulaklığın bildirdiği gecikme, el-serbest codec'i ve tıkanma olayları.
/// </summary>
public sealed class BtAudioTelemetry : IDisposable
{
    private static readonly Guid A2dpProvider = new("8776ad1e-5022-4451-a566-f47e708b9075");
    private static readonly Guid HfpProvider = new("1d571ab7-c34d-41c5-b141-42d76317c7c5");
    private static readonly Guid HfAudProvider = new("8d87aa29-0ebe-41af-b3c4-f56ce668bd22");
    private static readonly Guid HfEnumProvider = new("9680893f-0696-4ec0-9af5-65974897c9d4");

    private const string SessionName = "PodPanel-BtAudio";
    private const int MaxRecent = 200;

    private readonly object _gate = new();
    private readonly Dictionary<ulong, LinkInfo> _links = new();
    private readonly Queue<string> _recent = new();
    private EtwRealtimeSession? _session;

    public bool Available { get; private set; }
    public int StartError { get; private set; }

    public event Action? Changed;
    public event Action<TlEvent>? RawEvent;

    /// <param name="instanceTag">
    /// Oturum adına eklenir. Tepsi uygulaması, tanı aracı ve ekran görüntüsü modu aynı anda çalışabildiği için
    /// her biri kendi oturumunu kullanır; aksi halde biri diğerinin oturumunu kapatırdı.
    /// </param>
    public void Start(string instanceTag = "")
    {
        try
        {
            _session = new EtwRealtimeSession(SessionName + instanceTag);
            _session.EventReceived += OnEvent;
            StartError = _session.Start(new[] { A2dpProvider, HfpProvider, HfAudProvider, HfEnumProvider });
            Available = StartError == 0;
            Log.Write(Available
                ? "Sürücü telemetrisi dinleniyor"
                : $"Sürücü telemetrisi açılamadı (hata {StartError}); codec ve gecikme bilgisi gösterilemeyecek");
        }
        catch (Exception ex)
        {
            Available = false;
            Log.Write("Sürücü telemetrisi başlatılamadı: " + ex.Message);
        }
    }

    public LinkInfo? Get(ulong address)
    {
        lock (_gate)
        {
            _links.TryGetValue(address, out var specific);
            _links.TryGetValue(0, out var shared);
            if (specific == null && shared == null) return null;
            var result = (specific ?? shared)!.Clone();
            if (specific != null && shared != null)
            {
                // Adres taşımayan olaylardan gelen bilgiyle boşlukları doldur.
                result.CodecId ??= shared.CodecId;
                result.VendorId ??= shared.VendorId;
                result.VendorCodecId ??= shared.VendorCodecId;
                result.SampleRate ??= shared.SampleRate;
                result.Channels ??= shared.Channels;
                result.DelayMs ??= shared.DelayMs;
                result.HfpCodec ??= shared.HfpCodec;
                result.HfpOffered ??= shared.HfpOffered;
            }
            return result;
        }
    }

    public List<string> RecentEvents()
    {
        lock (_gate) return _recent.ToList();
    }

    private void OnEvent(TlEvent e)
    {
        bool changed = false;
        // Olayların çoğu adresi BTDeviceAddress, yapılandırma olayı ise BTDeviceAddressRemote adıyla taşır.
        ulong address = e.TryNumber("BTDeviceAddress", out long raw) || e.TryNumber("BTDeviceAddressRemote", out raw)
            ? unchecked((ulong)raw)
            : 0;
        lock (_gate)
        {
            if (!_links.TryGetValue(address, out var info))
            {
                info = new LinkInfo();
                _links[address] = info;
            }

            switch (e.Name)
            {
                case "A2dpAvailableRemoteCodec":
                {
                    string codec = CodecName((int)e.Number("A2dpStandardCodecId"), (int)e.Number("A2dpVendorId"), (int)e.Number("A2dpVendorCodecId"));
                    if (!info.RemoteCodecs.Contains(codec))
                    {
                        info.RemoteCodecs.Add(codec);
                        changed = true;
                    }
                    break;
                }
                case "AvdtpSetConfigInfo":
                    if (e.Number("ResultCode", -1) == 0)
                    {
                        info.CodecId = (int)e.Number("A2dpStandardCodecId");
                        info.VendorId = (int)e.Number("A2dpVendorId");
                        info.VendorCodecId = (int)e.Number("A2dpVendorCodecId");
                        if (e.TryNumber("SampleRate", out long rate)) info.SampleRate = (int)rate;
                        if (e.TryNumber("ChannelCount", out long channels)) info.Channels = (int)channels;
                        changed = true;
                    }
                    break;
                case "A2dpPinSetDataFormat":
                    // Bağlantı uygulamadan önce kurulduysa örnekleme hızını akış biçiminden al.
                    if (e.Number("StatusErrorCode", -1) == 0)
                    {
                        if (e.TryNumber("SampleRate.Value", out long pinRate) && pinRate > 0 && info.SampleRate != (int)pinRate)
                        {
                            info.SampleRate = (int)pinRate;
                            changed = true;
                        }
                        if (e.TryNumber("ChannelCount.Value", out long pinChannels) && pinChannels > 0 && info.Channels != (int)pinChannels)
                        {
                            info.Channels = (int)pinChannels;
                            changed = true;
                        }
                    }
                    break;
                case "A2dpRemoteDelayReportDuration":
                    if (e.TryNumber("A2dpRemoteDelayReportDurationInMs", out long delay))
                    {
                        info.DelayMs = (int)delay;
                        changed = true;
                    }
                    break;
                case "A2dpStreamingStart":
                    info.Streaming = true;
                    changed = true;
                    break;
                case "A2dpStreaming":
                    info.Streaming = false;
                    if (e.TryNumber("A2dpStandardCodecId", out long codecId))
                    {
                        info.CodecId = (int)codecId;
                        info.VendorId = (int)e.Number("A2dpVendorId");
                        info.VendorCodecId = (int)e.Number("A2dpVendorCodecId");
                    }
                    changed = true;
                    break;
                case "A2dpUpdateConnectionStatus":
                    if (e.TryNumber("A2dpIsConnected", out long isConnected) && isConnected == 0 && e.Number("A2dpIsSepOpen") == 0)
                    {
                        // Bağlantı kapandı: sonraki bağlantıda uç nokta listesi yeniden dolar.
                        info.RemoteCodecs.Clear();
                        info.Streaming = false;
                        changed = true;
                    }
                    break;
                case "HfpAvailableCodecs":
                    info.HfpOffered = e.Text("AvailableCodecs");
                    changed = true;
                    break;
                case "HfpCodecSelectionCompleted":
                    if (e.TryNumber("CodecSelection", out long hfp))
                    {
                        info.HfpCodec = (int)hfp;
                        changed = true;
                    }
                    break;
                default:
                    if (e.Name.Contains("Overflow", StringComparison.OrdinalIgnoreCase)) { info.Overflows++; changed = true; }
                    else if (e.Name.Contains("Late", StringComparison.OrdinalIgnoreCase)) { info.LatePackets++; changed = true; }
                    else if (e.Name.Contains("Backoff", StringComparison.OrdinalIgnoreCase)) { info.Backoffs++; changed = true; }
                    break;
            }

            _recent.Enqueue($"{e.Time:HH:mm:ss.fff} {e}");
            while (_recent.Count > MaxRecent) _recent.Dequeue();
        }

        RawEvent?.Invoke(e);
        if (changed) Changed?.Invoke();
    }

    public static string CodecName(int standardId, int vendorId, int vendorCodecId) => standardId switch
    {
        0 => "SBC",
        1 => "MPEG-1/2",
        2 => "AAC",
        4 => "ATRAC",
        255 => (vendorId, vendorCodecId) switch
        {
            (0x004F, 0x0001) or (0x000A, 0x0002) => "aptX",
            (0x00D7, 0x0024) => "aptX HD",
            (0x00D7, 0x00AD) => "aptX Adaptive",
            (0x012D, 0x00AA) => "LDAC",
            (0x004C, 0x8001) => "Apple'a özel (0x004C:0x8001)",
            _ => $"üreticiye özel (0x{vendorId:X4}:0x{vendorCodecId:X4})",
        },
        _ => $"codec {standardId}",
    };

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        Available = false;
    }
}
