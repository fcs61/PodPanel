using System.Buffers.Binary;
using System.IO.Pipes;

namespace PodPanel.Core.Bridge;

/// <summary>Bir bağlantıda Windows'un gönderdiği en yoğun kanalın son ölçümü.</summary>
public readonly record struct LinkVolume(int Cid, double BytesPerSecond, double PdusPerSecond, DateTime At)
{
    /// <summary>L2CAP yükünün bit hızı (kbps).</summary>
    public double RawKbps => BytesPerSecond * 8 / 1000;

    /// <summary>
    /// Sesin tahmini bit hızı (kbps): A2DP'de her L2CAP paketi 12 baytlık RTP başlığı taşıyan tek bir ortam paketidir;
    /// başlıklar çıkarılınca kodlanmış ses kalır.
    /// </summary>
    public double AudioKbps => Math.Max(0, BytesPerSecond - (12 * PdusPerSecond)) * 8 / 1000;
}

/// <summary>Köprü servisinin bildirdiği durum (ileti 0x20).</summary>
public enum BridgeServiceState
{
    /// <summary>Adaptör devralınıyor.</summary>
    Starting = 0,
    /// <summary>Adaptör servis üzerinden çalışıyor.</summary>
    Running = 1,
    /// <summary>Art arda başarısız denemelerden sonra servis adaptörü Windows'un kendi sürücüsüne geri verdi.</summary>
    Native = 2,
    /// <summary>Devralınacak adaptör yok. Ayrıntı: "not-usb", "none" ya da "usbip-missing".</summary>
    Unsupported = 3,
    Updating = 4,
}

/// <summary>
/// Köprü servisinin durum kanalını dinler: servisin durumunu (ileti 0x20) ve bağlantı başına gönderim hacmini
/// (ileti 0x28; servisin 0.3.2+…noupdate.3 ve sonrası sürümleri gönderir) tutar. Servis yalnızca paket uzunluklarını sayar.
/// </summary>
public sealed class BridgeStatusClient : IDisposable
{
    private const byte LinkVolumeMessage = 0x28;
    private const byte StateMessage = 0x20;
    private const byte RetryMessage = 0x21;

    private readonly object _gate = new();
    private readonly Dictionary<ulong, LinkVolume> _volumes = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private NamedPipeClientStream? _pipe;
    private BridgeServiceState? _state;
    private string _detail = "";

    /// <summary>Servisin son bildirdiği durum; kanal kapalıyken null.</summary>
    public BridgeServiceState? State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Durumun ayrıntısı (ör. "not-usb"); yoksa boş.</summary>
    public string Detail
    {
        get { lock (_gate) return _detail; }
    }

    /// <summary>Servisten adaptörü yeniden devralmayı denemesini ister (koruma devreye girdikten sonra).</summary>
    public bool Retry()
    {
        NamedPipeClientStream? pipe;
        lock (_gate) pipe = _pipe;
        if (pipe == null) return false;
        try
        {
            pipe.Write(new[] { RetryMessage }, 0, 1);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Yeni bir ölçüm ya da durum geldi (herhangi bir iş parçacığından).</summary>
    public event Action? Changed;

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>Adresin son ölçümü; <paramref name="maxAge"/> süresinden eskiyse ya da hiç yoksa null.</summary>
    public LinkVolume? Get(ulong address, TimeSpan maxAge)
    {
        lock (_gate)
        {
            return _volumes.TryGetValue(address, out LinkVolume volume) && DateTime.UtcNow - volume.At <= maxAge ? volume : null;
        }
    }

    private async Task RunAsync()
    {
        var buffer = new byte[4096];
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeClientStream? pipe = BridgePipes.Connect(BridgePipes.ServicePipe, out _);
            if (pipe != null)
            {
                lock (_gate) _pipe = pipe;
                try
                {
                    while (true)
                    {
                        int total = 0;
                        do
                        {
                            int n = await pipe.ReadAsync(buffer.AsMemory(total), _stop.Token).ConfigureAwait(false);
                            if (n == 0) throw new IOException("kanal kapandı");
                            total += n;
                        }
                        while (!pipe.IsMessageComplete && total < buffer.Length);
                        if (buffer[0] == LinkVolumeMessage) OnVolume(buffer.AsSpan(1, total - 1));
                        else if (buffer[0] == StateMessage && total >= 2) OnState(buffer[1], buffer.AsSpan(2, total - 2));
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
                {
                    // Servis durdu ya da uygulama kapanıyor.
                }
                finally
                {
                    bool had;
                    lock (_gate)
                    {
                        had = _state != null;
                        _pipe = null;
                        _state = null;
                        _detail = "";
                    }
                    pipe.Dispose();
                    if (had) Changed?.Invoke();
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnState(byte state, ReadOnlySpan<byte> detail)
    {
        string text = System.Text.Encoding.UTF8.GetString(detail);
        lock (_gate)
        {
            if (_state == (BridgeServiceState)state && _detail == text) return;
            _state = (BridgeServiceState)state;
            _detail = text;
        }
        Changed?.Invoke();
    }

    // u32 süre (ms), ardından bağlantı başına: u64 adres, u16 kanal, u32 bayt, u32 paket.
    private void OnVolume(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4) return;
        double seconds = BinaryPrimitives.ReadUInt32LittleEndian(body) / 1000.0;
        if (seconds <= 0) return;
        DateTime now = DateTime.UtcNow;
        lock (_gate)
        {
            for (int offset = 4; offset + 18 <= body.Length; offset += 18)
            {
                ReadOnlySpan<byte> entry = body.Slice(offset, 18);
                ulong address = BinaryPrimitives.ReadUInt64LittleEndian(entry);
                _volumes[address] = new LinkVolume(
                    BinaryPrimitives.ReadUInt16LittleEndian(entry[8..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(entry[10..]) / seconds,
                    BinaryPrimitives.ReadUInt32LittleEndian(entry[14..]) / seconds,
                    now);
            }
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(1000);
        }
        catch (AggregateException)
        {
            // Döngü kapanırken hata verdi; önemli değil.
        }
        _stop.Dispose();
    }
}
