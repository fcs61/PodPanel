using System.Collections.Concurrent;

namespace PodPanel.Core.Bridge;

/// <summary>Bir AVDTP isteğinin yanıtı.</summary>
public sealed record AvdtpResponse(bool Accepted, byte[] Payload)
{
    /// <summary>Yanıt hiç gelmedi.</summary>
    public bool TimedOut { get; init; }

    public string Describe() => TimedOut ? "yanıt yok"
        : Accepted ? (Payload.Length == 0 ? "kabul" : "kabul " + Convert.ToHexString(Payload))
        : "ret " + Convert.ToHexString(Payload);
}

/// <summary>
/// AVDTP işaretleşmesinin istemci tarafı (ses akışı uçlarını keşfetme, yapılandırma, bırakma), köprü servisinin
/// açtığı L2CAP kanalı üzerinden. Yalnızca tek paketlik iletileri destekler; kulaklığın yetenek ve yapılandırma
/// iletileri buna sığar.
/// </summary>
public sealed class AvdtpSignaling : IDisposable
{
    public const byte Discover = 0x01;
    public const byte GetAllCapabilities = 0x0C;
    public const byte SetConfiguration = 0x03;
    public const byte Open = 0x06;
    public const byte Close = 0x08;
    public const byte Abort = 0x0A;
    public const byte DelayReport = 0x0D;

    private readonly AapClient _channel;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<AvdtpResponse>> _pending = new();
    private int _label;

    /// <summary>Kulaklığın bildirdiği gecikme: akış ucu numarası ve gecikme (0,1 ms biriminde).</summary>
    public event Action<int, int>? DelayReported;

    /// <summary>Gelen her ileti (tanı çıktısı için).</summary>
    public event Action<string>? Trace;

    public AvdtpSignaling(AapClient channel)
    {
        _channel = channel;
        _channel.Packet += OnPacket;
    }

    /// <summary>Bir komut gönderir ve aynı etiketli yanıtı bekler.</summary>
    public AvdtpResponse Request(byte signal, ReadOnlySpan<byte> payload, int timeoutMs = 3000)
    {
        int label = Interlocked.Increment(ref _label) & 0x0F;
        var waiter = new TaskCompletionSource<AvdtpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[(label << 8) | signal] = waiter;
        byte[] message = new byte[2 + payload.Length];
        message[0] = (byte)(label << 4); // tek paket, komut
        message[1] = signal;
        payload.CopyTo(message.AsSpan(2));
        Trace?.Invoke($"→ {Name(signal)} {Convert.ToHexString(payload)}");
        if (!_channel.Send(message) || !waiter.Task.Wait(timeoutMs))
        {
            _pending.TryRemove((label << 8) | signal, out _);
            return new AvdtpResponse(false, Array.Empty<byte>()) { TimedOut = true };
        }
        return waiter.Task.Result;
    }

    private void OnPacket(byte[] packet)
    {
        if (packet.Length < 2) return;
        int label = packet[0] >> 4;
        int packetType = (packet[0] >> 2) & 3;
        int messageType = packet[0] & 3;
        byte signal = (byte)(packet[1] & 0x3F);
        byte[] payload = packet.AsSpan(2).ToArray();
        if (packetType != 0)
        {
            Trace?.Invoke($"← parçalı ileti (desteklenmiyor) {Convert.ToHexString(packet)}");
            return;
        }
        if (messageType == 0)
        {
            // Kulaklıktan gelen komut. Gecikme bildirimi kabul edilir; diğerleri "desteklenmiyor" ile yanıtlanır.
            Trace?.Invoke($"← komut {Name(signal)} {Convert.ToHexString(payload)}");
            if (signal == DelayReport && payload.Length >= 3)
            {
                _channel.Send(new[] { (byte)((label << 4) | 0x02), signal });
                DelayReported?.Invoke(payload[0] >> 2, (payload[1] << 8) | payload[2]);
            }
            else
            {
                _channel.Send(new[] { (byte)((label << 4) | 0x01), signal }); // genel ret
            }
            return;
        }
        Trace?.Invoke($"← {Name(signal)} {(messageType == 2 ? "kabul" : "ret")} {Convert.ToHexString(payload)}");
        if (_pending.TryRemove((label << 8) | signal, out TaskCompletionSource<AvdtpResponse>? waiter))
        {
            waiter.TrySetResult(new AvdtpResponse(messageType == 2, payload));
        }
    }

    private static string Name(byte signal) => signal switch
    {
        Discover => "DISCOVER",
        GetAllCapabilities => "GET_ALL_CAPABILITIES",
        SetConfiguration => "SET_CONFIGURATION",
        Open => "OPEN",
        Close => "CLOSE",
        Abort => "ABORT",
        DelayReport => "DELAYREPORT",
        _ => $"0x{signal:X2}",
    };

    public void Dispose() => _channel.Packet -= OnPacket;
}
