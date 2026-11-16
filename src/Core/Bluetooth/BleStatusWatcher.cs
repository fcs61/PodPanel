using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace PodPanel.Core.Bluetooth;

/// <summary>
/// AirPods'un BLE yayınlarını dinler. Ses akışını bozmamak için pasif tarar ve
/// panel açık değilken yalnızca kısa aralıklarla (patlama) çalışır.
/// </summary>
public sealed class BleStatusWatcher : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly object _gate = new();
    private readonly Dictionary<int, PodAdvert> _latest = new();
    private Timer? _stopTimer;
    private bool _running;
    private bool _continuous;
    private bool _failed;

    public event Action? Updated;

    public BleStatusWatcher()
    {
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        _watcher.Received += OnReceived;
        _watcher.Stopped += (_, e) =>
        {
            lock (_gate) _running = false;
            if (e.Error != BluetoothError.Success && !_failed)
            {
                _failed = true;
                Log.Write("BLE taraması durdu: " + e.Error);
            }
        };
    }

    /// <summary>Panel açıkken sürekli tarama.</summary>
    public bool Continuous
    {
        get => _continuous;
        set
        {
            lock (_gate)
            {
                if (_continuous == value) return;
                _continuous = value;
                if (value) StartLocked();
                else ScheduleStopLocked(3);
            }
        }
    }

    /// <summary>Belirtilen süre boyunca tarar.</summary>
    public void Burst(int seconds)
    {
        lock (_gate)
        {
            StartLocked();
            if (!_continuous) ScheduleStopLocked(seconds);
        }
    }

    public PodAdvert? Latest(int model)
    {
        lock (_gate) return _latest.GetValueOrDefault(model);
    }

    private void StartLocked()
    {
        if (_running) return;
        try
        {
            _watcher.Start();
            _running = true;
        }
        catch (Exception ex)
        {
            if (!_failed)
            {
                _failed = true;
                Log.Write("BLE taraması başlatılamadı: " + ex.Message);
            }
        }
    }

    private void ScheduleStopLocked(int seconds)
    {
        _stopTimer?.Dispose();
        _stopTimer = new Timer(_ =>
        {
            lock (_gate)
            {
                if (_continuous || !_running) return;
                try { _watcher.Stop(); } catch { }
                _running = false;
            }
        }, null, seconds * 1000, Timeout.Infinite);
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        try
        {
            foreach (var data in e.Advertisement.ManufacturerData)
            {
                if (data.CompanyId != ProximityPairing.AppleCompanyId) continue;
                var advert = ProximityPairing.TryParse(data.Data.ToArray(), e.RawSignalStrengthInDBm, DateTime.Now);
                if (advert == null) continue;

                bool changed;
                lock (_gate)
                {
                    _latest.TryGetValue(advert.Model, out var previous);
                    // Aynı modelden iki kulaklık yakındaysa belirgin biçimde güçlü olanı tut.
                    if (previous != null && (advert.Time - previous.Time).TotalSeconds < 5 && advert.Rssi < previous.Rssi - 12) continue;
                    changed = previous == null || !previous.SameContent(advert);
                    _latest[advert.Model] = advert;
                }
                if (changed) Updated?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Write("BLE yayını işlenemedi: " + ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopTimer?.Dispose();
            try { if (_running) _watcher.Stop(); } catch { }
            _running = false;
        }
    }
}
