using System.Buffers.Binary;
using System.IO.Pipes;

namespace PodPanel.Core.Bridge;

/// <summary>
/// Köprü servisi üzerinden bir kulaklığın kontrol kanalı. Servis her kulaklık için tek kanal açar; ikinci istemci
/// 170 hatası alır.
/// </summary>
public sealed class AapClient : IDisposable
{
    private readonly PipeStream _pipe;
    private readonly CancellationTokenSource _life = new();
    private readonly object _writeGate = new();
    private readonly TaskCompletionSource _firstPacket = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _reader;

    /// <summary>Açılmış bir servis kanalını sarar (sınamalarda sahte bir sunucunun kanalı da olabilir).</summary>
    public AapClient(PipeStream connectedPipe) => _pipe = connectedPipe;

    /// <summary>Kulaklıktan gelen bir AAP paketi.</summary>
    public event Action<byte[]>? Packet;

    /// <summary>Kanal kapandı; değer servisin bildirdiği Win32 kodu, kanal koptuysa 109.</summary>
    public event Action<int>? Ended;

    /// <summary>Arama sesi (el-serbest bağlantısı) başladı ya da bitti.</summary>
    public event Action<bool>? CallAudio;

    public bool IsOpen { get; private set; } = true;

    /// <summary>Kulaklığa kanal açar ve okumayı başlatır; açılamazsa null ve Win32 hata kodu.</summary>
    public static AapClient? Open(ulong address, out int error, ushort psm = BridgePipes.AapPsm)
    {
        if (BridgePipes.OpenAap(address, out error, psm) is not { } pipe) return null;
        var client = new AapClient(pipe);
        client.Start();
        return client;
    }

    public void Start() => _reader ??= Task.Run(ReadLoopAsync);

    /// <summary>
    /// Açılış sırası: el sıkışma, ilk yanıt (en çok 10 sn), 100 ms, özellik bayrakları, bildirim isteği.
    /// Kulaklığın ayarlarını değiştiren hiçbir paket gönderilmez.
    /// </summary>
    public async Task<bool> InitializeAsync(bool earDetection = true)
    {
        if (!Send(Aap.Handshake)) return false;
        Task first = await Task.WhenAny(_firstPacket.Task, Task.Delay(TimeSpan.FromSeconds(10), _life.Token)).ConfigureAwait(false);
        if (first != _firstPacket.Task) return false;
        await Task.Delay(100).ConfigureAwait(false);
        return Send(Aap.FeatureFlags) && Send(Aap.RequestNotifications(earDetection));
    }

    /// <summary>Kulaklığa bir AAP paketi gönderir; kanal kapalıysa false.</summary>
    public bool Send(ReadOnlySpan<byte> packet)
    {
        if (!IsOpen) return false;
        try
        {
            lock (_writeGate)
            {
                _pipe.Write(packet);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[4096];
        int ended = BridgePipes.ErrorBrokenPipe;
        try
        {
            while (!_life.IsCancellationRequested)
            {
                int total = 0;
                do
                {
                    int n = await _pipe.ReadAsync(buffer.AsMemory(total), _life.Token).ConfigureAwait(false);
                    if (n == 0) return;
                    total += n;
                }
                while (!_pipe.IsMessageComplete && total < buffer.Length);
                switch (buffer[0])
                {
                    case BridgePipes.AapPacket when total > 1:
                        _firstPacket.TrySetResult();
                        Packet?.Invoke(buffer.AsSpan(1, total - 1).ToArray());
                        break;
                    case BridgePipes.AapEnded when total >= 5:
                        ended = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(1));
                        return;
                    case BridgePipes.AapCallAudio when total >= 2:
                        CallAudio?.Invoke(buffer[1] != 0);
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // Kanal iki taraftan biri tarafından kapatıldı.
        }
        finally
        {
            bool wasOpen = IsOpen;
            IsOpen = false;
            if (wasOpen && !_life.IsCancellationRequested) Ended?.Invoke(ended);
        }
    }

    public void Dispose()
    {
        IsOpen = false;
        _life.Cancel();
        _pipe.Dispose();
        try
        {
            _reader?.Wait(1000);
        }
        catch (AggregateException)
        {
            // Okuma döngüsü kapanırken hata verdi; kanal zaten kapalı.
        }
        _life.Dispose();
    }
}
