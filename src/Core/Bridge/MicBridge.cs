using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace PodPanel.Core.Bridge;

/// <summary>Mikrofon köprüsünün anlık durumu.</summary>
public sealed class MicBridgeStatus
{
    /// <summary>Servisin mikrofon kanalı açık; sanal mikrofon Windows'ta görünür.</summary>
    public bool PipeConnected { get; init; }

    /// <summary>Bir uygulama sanal mikrofondan kayıt alıyor.</summary>
    public bool Capturing { get; init; }

    /// <summary>Kulaklıktan ses akıyor (son yarım saniyede paket geldi).</summary>
    public bool Streaming { get; init; }

    /// <summary>Kalıcı bir sorun varsa açıklaması (FFmpeg yok gibi); yoksa boş.</summary>
    public string Error { get; init; } = "";
}

/// <summary>
/// AirPods mikrofonunu el-serbest profili olmadan kullanır: bir uygulama servisin sanal mikrofonundan kayıt alırken
/// kulaklıktan kontrol kanalı üzerinden mikrofon akışı ister, AAC-ELD sesi çözer ve 48 kHz mono PCM olarak servise
/// yazar. Kulaklık stereo (A2DP) kalır.
/// Kulaklık, akış sürerken el-serbest bağlantısı başlarsa bağlantıyı düşürür; bu yüzden köprü yalnızca Windows'ta
/// el-serbest servisi kapalıyken etkinleştirilmelidir (çağıran sağlar).
/// </summary>
public sealed class MicBridge : IDisposable
{
    private const byte MicDevice = 0x10;
    private const byte MicPcm = 0x11;
    private const byte MicCapture = 0x12;
    private const string MicPipe = "WPods.Mic";

    private const int HeaderLength = 22;
    private const int RecordHeaderLength = 5; // u32 zaman damgası + u8 uzunluk
    // Kulaklığın konuşma düzeyi düşüktür: AirPods Pro 3'te sakin konuşma ham hâlde yaklaşık -46 dBFS RMS ölçüldü.
    // +18 dB ile gerçek kullanımda tepeler tam ölçeğe dayandı; +15 dB konuşmayı -30 dBFS dolayına getirir ve
    // yumuşak sınırlayıcıya pay bırakır. Windows'un mikrofon düzeyi kaydırıcısı bunun üzerine ayrıca uygulanır.
    private const float MakeUpGain = 5.6f;
    // Kayıt bitince akışın durdurulması bu kadar bekletilir (bkz. SetCapturing).
    private const int StopDelayMs = 800;
    private const int MaxConsecutiveErrors = 8;

    private static readonly byte[] StartMicrophone = { 0x04, 0x00, 0x04, 0x00, 0x58, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x01, 0x82, 0x00, 0x00, 0x00, 0x04, 0x96, 0x00 };
    private static readonly byte[] StopMicrophone = { 0x04, 0x00, 0x04, 0x00, 0x58, 0x00, 0x00, 0x00, 0x02, 0x00, 0x03, 0x01 };

    private readonly AapLink _aap;
    private readonly object _gate = new();
    private readonly Timer _watchdog;
    private readonly float[] _decoded = new float[AacEldDecoder.MaxSamplesPerUnit];
    private readonly byte[] _message = new byte[1 + (AacEldDecoder.MaxSamplesPerUnit * 2)];

    private NamedPipeClientStream? _pipe;
    private byte[]? _device;
    private bool _enabled;
    private bool _connecting;
    private bool _disposed;
    private DateTime _nextConnect = DateTime.MinValue;
    private bool _capturing;
    private bool _streamOn; // kulaklıktan akış istendi ve henüz durdurulmadı
    private int _stopToken;
    private bool _flush;
    private long _startSentAt;
    private long _lastAudioAt;
    private int _errors;
    private string _error = "";
    private AacEldDecoder? _decoder; // yalnızca kontrol kanalının okuma iş parçacığında kullanılır

    public event Action? Changed;

    public MicBridge(AapLink aap)
    {
        _aap = aap;
        _aap.MicPacket += OnMicPacket;
        _message[0] = MicPcm;
        _watchdog = new Timer(_ => Watch(), null, 500, 500);
    }

    public MicBridgeStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new MicBridgeStatus
                {
                    PipeConnected = _pipe != null,
                    Capturing = _capturing,
                    Streaming = _capturing && Environment.TickCount64 - _lastAudioAt < 500,
                    Error = _error,
                };
            }
        }
    }

    /// <summary>
    /// İzleyicinin her yenilemesinde çağrılır. Etkinse servisin mikrofon kanalını açık tutar ve sanal mikrofonun hangi
    /// kulaklık için olduğunu bildirir; değilse akışı durdurup kanalı kapatır. Engellemez.
    /// </summary>
    public void Configure(bool enabled, ulong address, string name)
    {
        NamedPipeClientStream? close = null;
        bool stop = false, connect = false;
        lock (_gate)
        {
            if (_disposed) return;
            _enabled = enabled && address != 0;
            if (!_enabled)
            {
                stop = _streamOn;
                _streamOn = false;
                _capturing = false;
                _stopToken++;
                close = _pipe;
                _pipe = null;
                _device = null;
            }
            else
            {
                byte[] device = new byte[10 + Encoding.UTF8.GetByteCount(name)];
                device[0] = MicDevice;
                BinaryPrimitives.WriteUInt64LittleEndian(device.AsSpan(1), address);
                device[9] = 1; // ad çağıranın verdiği addır; servis kendi bildiği adla değiştirmesin
                Encoding.UTF8.GetBytes(name, device.AsSpan(10));
                if (_device == null || !device.AsSpan().SequenceEqual(_device))
                {
                    _device = device;
                    if (_pipe != null) WriteLocked(device);
                }
                if (_pipe == null && !_connecting && DateTime.UtcNow >= _nextConnect)
                {
                    _connecting = true;
                    connect = true;
                }
            }
        }
        if (stop) _aap.SendRaw(StopMicrophone);
        close?.Dispose();
        if (connect) _ = Task.Run(ConnectAsync);
    }

    private async Task ConnectAsync()
    {
        NamedPipeClientStream? pipe = BridgePipes.Connect(MicPipe, out int error);
        lock (_gate)
        {
            _connecting = false;
            if (pipe == null || !_enabled || _disposed)
            {
                pipe?.Dispose();
                _nextConnect = DateTime.UtcNow + TimeSpan.FromSeconds(error == BridgePipes.ErrorPipeBusy ? 3 : 10);
                return;
            }
            _pipe = pipe;
            if (_device != null) WriteLocked(_device);
        }
        Changed?.Invoke();

        // Servis, bir uygulamanın sanal mikrofondan kayıt alıp almadığını bildirir.
        byte[] buffer = new byte[16];
        try
        {
            while (true)
            {
                int count = await pipe.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) break;
                if (buffer[0] != MicCapture || count < 2) continue;
                SetCapturing(pipe, buffer[1] != 0);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // Kanal kapandı.
        }
        bool stop;
        lock (_gate)
        {
            if (!ReferenceEquals(_pipe, pipe)) return;
            _pipe = null;
            stop = _streamOn;
            _streamOn = false;
            _capturing = false;
            _stopToken++;
            _nextConnect = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        }
        pipe.Dispose();
        if (stop) _aap.SendRaw(StopMicrophone);
        Changed?.Invoke();
    }

    private void SetCapturing(NamedPipeClientStream pipe, bool capturing)
    {
        bool start = false;
        int stopToken = 0;
        lock (_gate)
        {
            if (!ReferenceEquals(_pipe, pipe) || _capturing == capturing) return;
            _capturing = capturing;
            _stopToken++; // bekleyen bir durdurma varsa geçersiz kalır
            if (capturing)
            {
                if (!_streamOn)
                {
                    // Kulakta değilken kulaklık akışı 0,2 saniye sonra keser; o durumda istek gözcüye bırakılır.
                    _flush = true;
                    _lastAudioAt = 0;
                    _startSentAt = Environment.TickCount64;
                    start = MayStream();
                    _streamOn = start;
                }
            }
            else if (_streamOn)
            {
                stopToken = _stopToken;
            }
        }
        if (start) _aap.SendRaw(StartMicrophone);
        // Uygulamalar kaydı çoğu kez durdurup hemen yeniden başlatır (Ses Kaydedici 2 ms sonra). Akış hemen durdurulursa
        // kulaklık ardından gelen başlatma isteğini kaçırır ve kayıt iki saniye sessiz başlar; bu yüzden durdurma kısa
        // bir süre bekletilir ve bu sürede kayıt yeniden başlarsa akışa hiç dokunulmaz.
        if (stopToken != 0) _ = Task.Delay(StopDelayMs).ContinueWith(_ => StopIfIdle(stopToken), TaskScheduler.Default);
        Changed?.Invoke();
    }

    private void StopIfIdle(int token)
    {
        lock (_gate)
        {
            if (_disposed || _stopToken != token || _capturing || !_streamOn) return;
            _streamOn = false;
        }
        _aap.SendRaw(StopMicrophone);
    }

    // Kayıt sürerken ses gelmiyorsa akışı yeniden ister: ilk istek kaybolmuş, kontrol kanalı yeniden açılmış ya da
    // kulaklık kulaktan çıkarılıp yeniden takılmış olabilir (kulakta değilken kulaklık akışı keser).
    private void Watch()
    {
        bool restart = false, changed = false;
        lock (_gate)
        {
            if (_disposed || !_capturing) return;
            long now = Environment.TickCount64;
            bool silent = now - _lastAudioAt > 1500;
            if (silent && now - _startSentAt > 2000 && MayStream())
            {
                _startSentAt = now;
                _flush = true;
                _streamOn = true;
                restart = true;
            }
            changed = silent != _wasSilent;
            _wasSilent = silent;
        }
        if (restart) _aap.SendRaw(StartMicrophone);
        if (changed) Changed?.Invoke();
    }

    private bool _wasSilent;

    // Kulak durumu bilinmiyorsa denenir; biliniyorsa en az bir kulaklık kulakta olmalı.
    private bool MayStream()
    {
        AapSnapshot snapshot = _aap.Snapshot;
        return snapshot.Open && (!snapshot.EarKnown || snapshot.Worn);
    }

    // Kontrol kanalının okuma iş parçacığında çalışır: 0x58 ses paketi -> erişim birimleri -> PCM -> servis.
    private void OnMicPacket(byte[] packet)
    {
        if (packet.Length < HeaderLength || packet[6] != 0x01 || packet[7] != 0x00) return;
        bool flush;
        lock (_gate)
        {
            if (!_capturing) return;
            flush = _flush;
            _flush = false;
        }
        if (_decoder == null)
        {
            try
            {
                _decoder = new AacEldDecoder();
                Log.Write($"Mikrofon çözücüsü açıldı ({_decoder.LibraryVersion})");
            }
            catch (InvalidOperationException e)
            {
                lock (_gate)
                {
                    if (_error.Length > 0) return;
                    _error = e.Message;
                }
                Log.Write("Mikrofon çözücüsü açılamadı: " + e.Message);
                Changed?.Invoke();
                return;
            }
        }
        if (flush)
        {
            _decoder.Flush();
            _errors = 0;
        }

        bool audio = false;
        // Kayıtlar: u32 zaman damgası, u8 uzunluk, erişim birimi. Bozuk bir kayıt paketi bitirir.
        for (int offset = HeaderLength; packet.Length - offset >= RecordHeaderLength;)
        {
            int length = packet[offset + 4];
            int start = offset + RecordHeaderLength;
            if (length == 0 || start + length > packet.Length) break;
            int samples = _decoder.Decode(packet.AsSpan(start, length), _decoded);
            offset = start + length;
            if (samples < 0)
            {
                if (++_errors >= MaxConsecutiveErrors)
                {
                    _decoder.Flush();
                    _errors = 0;
                }
                continue;
            }
            _errors = 0;
            if (samples == 0) continue;
            audio = true;
            Span<byte> pcm = _message.AsSpan(1, samples * 2);
            for (int i = 0; i < samples; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(i * 2, 2), ToPcm16(_decoded[i] * MakeUpGain));
            }
            lock (_gate)
            {
                if (_pipe == null || !_capturing) return;
                WriteLocked(_message.AsSpan(0, 1 + (samples * 2)));
            }
        }
        if (audio)
        {
            lock (_gate) _lastAudioAt = Environment.TickCount64;
        }
    }

    // Yumuşak sınırlayıcı: 0,8'e kadar doğrusal, üstü tanh ile 1'e yaklaşır; sert kırpma cızırtısı olmaz.
    private static short ToPcm16(float sample)
    {
        float magnitude = Math.Abs(sample);
        if (magnitude > 0.8f) sample = Math.Sign(sample) * (0.8f + (0.2f * MathF.Tanh((magnitude - 0.8f) / 0.2f)));
        return (short)Math.Clamp((int)MathF.Round(sample * 32767f), short.MinValue, short.MaxValue);
    }

    private void WriteLocked(ReadOnlySpan<byte> message)
    {
        try
        {
            _pipe!.Write(message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Okuma döngüsü kapanan kanalı görür ve yeniden bağlanmayı zamanlar.
            _pipe?.Dispose();
        }
    }

    public void Dispose()
    {
        NamedPipeClientStream? pipe;
        bool stop;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            stop = _streamOn;
            _streamOn = false;
            _capturing = false;
            pipe = _pipe;
            _pipe = null;
        }
        _watchdog.Dispose();
        _aap.MicPacket -= OnMicPacket;
        if (stop) _aap.SendRaw(StopMicrophone);
        pipe?.Dispose();
        // Çözücü, kontrol kanalının okuma iş parçacığına aittir; o iş parçacığı kanalla birlikte kapanır.
    }
}
