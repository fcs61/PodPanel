using System.Runtime.InteropServices;
using PodPanel.Core.Interop;

namespace PodPanel.Core.Audio;

/// <summary>
/// Bir ses çıkışının Windows'taki ses düzeyi: okur, değiştirir ve başka bir yerden (Windows'un ses çubuğu, klavye
/// tuşları, kulaklığın sapı) değiştiğinde bildirir. Windows'un kendi çubuğuyla aynı değeri kullanır (0-1 arası oran).
/// Her iş parçacığından çağrılabilir.
/// </summary>
public sealed class EndpointVolume : IDisposable
{
    private const int ClsCtxAll = 23;
    private static readonly Guid IidEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private readonly object _gate = new();
    // Kendi yaptığımız değişiklikler bu kimlikle işaretlenir; bildirimde geri dönenler yok sayılır.
    private readonly Guid _context = Guid.NewGuid();
    private readonly Callback _callback;
    private IMMDeviceEnumerator? _enum;
    private IAudioEndpointVolume? _volume;
    private string? _id;
    private float _pending = -1;
    private bool _worker;

    public EndpointVolume()
    {
        _callback = new Callback(this);
    }

    /// <summary>Düzey (0-1) ya da sessize alma başka bir yerden değişti. Bir COM iş parçacığında çağrılır.</summary>
    public event Action<float, bool>? Changed;

    /// <summary>İzlenecek ses çıkışını seçer; null izlemeyi bırakır.</summary>
    public void Attach(string? endpointId)
    {
        lock (_gate)
        {
            if (string.Equals(endpointId, _id, StringComparison.OrdinalIgnoreCase) && (endpointId == null || _volume != null)) return;
            Detach();
            if (endpointId == null) return;
            IMMDevice? device = null;
            try
            {
                _enum ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (_enum.GetDevice(endpointId, out device) != 0 || device == null) return;
                Guid iid = IidEndpointVolume;
                if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object? instance) != 0 || instance == null) return;
                _volume = (IAudioEndpointVolume)instance;
                _volume.RegisterControlChangeNotify(_callback);
                _id = endpointId;
            }
            catch (Exception e) when (e is COMException or InvalidCastException)
            {
                Log.Write("Ses düzeyi izlenemiyor: " + e.Message);
                Detach();
            }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
            }
        }
    }

    /// <summary>Şu anki düzey ve sessize alma; çıkış izlenmiyorsa null.</summary>
    public (float Level, bool Muted)? Get()
    {
        lock (_gate)
        {
            if (_volume == null) return null;
            try
            {
                if (_volume.GetMasterVolumeLevelScalar(out float level) != 0) return null;
                _volume.GetMute(out bool muted);
                return (level, muted);
            }
            catch (Exception e) when (e is COMException or InvalidComObjectException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Düzeyi ayarlar (0-1). Sürüklerken art arda gelen değerlerden yalnızca sonuncusu uygulanır; çağıran beklemez.
    /// </summary>
    public void Set(float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        lock (_gate)
        {
            _pending = level;
            if (_worker) return;
            _worker = true;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                float value;
                IAudioEndpointVolume? volume;
                lock (_gate)
                {
                    value = _pending;
                    _pending = -1;
                    volume = _volume;
                    if (value < 0 || volume == null)
                    {
                        _worker = false;
                        return;
                    }
                }
                try
                {
                    Guid context = _context;
                    volume.SetMasterVolumeLevelScalar(value, ref context);
                }
                catch (Exception e) when (e is COMException or InvalidComObjectException)
                {
                    // Çıkış bu arada gitti; bir sonraki Attach yeniden bağlar.
                }
            }
        });
    }

    public void SetMute(bool mute)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            IAudioEndpointVolume? volume;
            lock (_gate) volume = _volume;
            if (volume == null) return;
            try
            {
                Guid context = _context;
                volume.SetMute(mute, ref context);
            }
            catch (Exception e) when (e is COMException or InvalidComObjectException)
            {
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Detach();
            if (_enum != null) Marshal.ReleaseComObject(_enum);
            _enum = null;
        }
    }

    private void Detach()
    {
        if (_volume != null)
        {
            try { _volume.UnregisterControlChangeNotify(_callback); }
            catch (Exception e) when (e is COMException or InvalidComObjectException) { }
            try { Marshal.ReleaseComObject(_volume); }
            catch (Exception e) when (e is ArgumentException or InvalidComObjectException) { }
        }
        _volume = null;
        _id = null;
    }

    private sealed class Callback : IAudioEndpointVolumeCallback
    {
        private readonly EndpointVolume _owner;

        public Callback(EndpointVolume owner) => _owner = owner;

        public int OnNotify(IntPtr data)
        {
            if (data == IntPtr.Zero) return 0;
            var notification = Marshal.PtrToStructure<AUDIO_VOLUME_NOTIFICATION_DATA>(data);
            if (notification.EventContext == _owner._context) return 0;
            try
            {
                _owner.Changed?.Invoke(notification.MasterVolume, notification.Muted != 0);
            }
            catch (Exception e)
            {
                Log.Write("Ses düzeyi bildirimi işlenemedi: " + e.Message);
            }
            return 0;
        }
    }
}
