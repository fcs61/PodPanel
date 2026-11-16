using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PodPanel.Core.Interop;

namespace PodPanel.Core.Audio;

/// <summary>A2DP filtresinin kimliğinden okunan Bluetooth bilgisi.</summary>
public sealed record BtIdent(int VendorId, int ProductId, string Address);

public sealed record EndpointInfo(
    string Id,
    string Name,
    string DeviceName,
    DataFlow Flow,
    int State,
    string FilterId,
    Guid ContainerId,
    bool JackConnected,
    bool IsHandsFree,
    BtIdent? Bt)
{
    public bool IsActive => State == 1;
    public bool IsDisabled => State == 2;
    public bool IsBluetooth => Bt != null || IsHandsFree;
}

public sealed record SessionInfo(int ProcessId, string ProcessName);

/// <summary>
/// Windows Core Audio sarmalayıcısı. Tüm çağrılar tek bir iş parçacığından (PodMonitor) yapılır;
/// aygıt bildirimi geri çağrıları yalnızca <see cref="DevicesChanged"/> olayını tetikler.
/// </summary>
public sealed class AudioService : IDisposable
{
    private const int ClsCtxAll = 23;
    private const int StateMask = 0xB; // etkin | devre dışı | takılı değil

    private static readonly Guid IidDeviceTopology = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    private static readonly Guid IidKsJack = new("4509F757-2D46-4637-8E62-CE7DB944F57B");
    private static readonly Guid IidKsControl = new("28F54685-06FD-11D2-B27A-00A0C9223196");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid IidMeter = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    private static readonly Guid IidSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid KsPropSetBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");

    private static readonly PROPERTYKEY KeyFriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    private static readonly PROPERTYKEY KeyDeviceName = new("b3f8fa53-0004-438e-9003-51a46e139bfc", 6);
    private static readonly PROPERTYKEY KeyContainerId = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);

    // ...bthenum#{0000110b-...}_vid&0001004c_pid&2027#7&xxxxxxxx&0&aabbccddeeff_c00000000#...  (son 12 hane: Bluetooth adresi)
    private static readonly Regex A2dpRx = new(
        @"bthenum#\{0000110b-[0-9a-f\-]+\}_(?:vid&[0-9a-f]{4}(?<vid>[0-9a-f]{4})_pid&(?<pid>[0-9a-f]{4})|[^#]*)#.*?&(?<addr>[0-9a-f]{12})_c[0-9a-f]{8}#",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IMMDeviceEnumerator _enum;
    private readonly NotificationClient _client;
    private IPolicyConfig? _policy;

    public event Action? DevicesChanged;

    public AudioService()
    {
        _enum = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        _client = new NotificationClient(() => DevicesChanged?.Invoke());
        int hr = _enum.RegisterEndpointNotificationCallback(_client);
        if (hr != 0) Log.Write($"Ses aygıtı bildirimi kaydedilemedi: 0x{hr:X8}");
    }

    public void Dispose()
    {
        try { _enum.UnregisterEndpointNotificationCallback(_client); } catch { }
    }

    public List<EndpointInfo> Enumerate()
    {
        var result = new List<EndpointInfo>();
        foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            if (_enum.EnumAudioEndpoints(flow, StateMask, out var collection) != 0 || collection == null) continue;
            try
            {
                collection.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) != 0 || device == null) continue;
                    try
                    {
                        var info = Describe(device, flow);
                        if (info != null) result.Add(info);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Uç nokta okunamadı: " + ex.Message);
                    }
                    finally
                    {
                        Rel(device);
                    }
                }
            }
            finally
            {
                Rel(collection);
            }
        }
        return result;
    }

    private static EndpointInfo? Describe(IMMDevice device, DataFlow flow)
    {
        if (device.GetId(out string id) != 0 || id == null) return null;
        device.GetState(out int state);

        string name = "", deviceName = "";
        Guid container = Guid.Empty;
        if (device.OpenPropertyStore(0, out var store) == 0 && store != null)
        {
            try
            {
                name = GetString(store, KeyFriendlyName) ?? "";
                deviceName = GetString(store, KeyDeviceName) ?? "";
                container = GetGuid(store, KeyContainerId);
            }
            finally
            {
                Rel(store);
            }
        }

        string filter = "";
        bool jack = state == 1;
        Guid iid = IidDeviceTopology;
        if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object topologyObj) == 0 && topologyObj != null)
        {
            try
            {
                var topology = (IDeviceTopology)topologyObj;
                if (topology.GetConnector(0, out var connector) == 0 && connector != null)
                {
                    try
                    {
                        if (connector.GetDeviceIdConnectedTo(out string filterId) == 0 && filterId != null) filter = filterId;
                        if (state == 1 && connector.GetConnectedTo(out var other) == 0 && other != null)
                        {
                            try
                            {
                                bool? fromJack = ReadJack(other);
                                if (fromJack.HasValue) jack = fromJack.Value;
                            }
                            finally
                            {
                                Rel(other);
                            }
                        }
                    }
                    finally
                    {
                        Rel(connector);
                    }
                }
            }
            finally
            {
                Rel(topologyObj);
            }
        }

        BtIdent? bt = null;
        var match = A2dpRx.Match(filter);
        if (match.Success)
        {
            int vid = match.Groups["vid"].Success ? int.Parse(match.Groups["vid"].Value, NumberStyles.HexNumber) : 0;
            int pid = match.Groups["pid"].Success ? int.Parse(match.Groups["pid"].Value, NumberStyles.HexNumber) : 0;
            bt = new BtIdent(vid, pid, match.Groups["addr"].Value.ToUpperInvariant());
        }
        bool handsFree = filter.Contains("bthhfenum", StringComparison.OrdinalIgnoreCase);

        return new EndpointInfo(id, name, deviceName, flow, state, filter, container, jack, handsFree, bt);
    }

    /// <summary>Uç noktanın bağlı olduğu filtre tarafındaki jak durumunu okur; bilgi yoksa null.</summary>
    private static bool? ReadJack(IConnector filterSide)
    {
        try
        {
            var part = (IPart)filterSide;
            Guid iid = IidKsJack;
            if (part.Activate(ClsCtxAll, ref iid, out object jackObj) != 0 || jackObj == null) return null;
            try
            {
                var jack = (IKsJackDescription)jackObj;
                if (jack.GetJackCount(out uint count) != 0 || count == 0) return null;
                if (jack.GetJackDescription(0, out var description) != 0) return null;
                return description.IsConnected != 0;
            }
            finally
            {
                Rel(jackObj);
            }
        }
        catch
        {
            return null;
        }
    }

    public string? GetDefaultId(DataFlow flow, Role role)
    {
        if (_enum.GetDefaultAudioEndpoint(flow, role, out var device) != 0 || device == null) return null;
        try
        {
            return device.GetId(out string id) == 0 ? id : null;
        }
        finally
        {
            Rel(device);
        }
    }

    public bool SetDefault(string endpointId, Role role)
    {
        try
        {
            _policy ??= (IPolicyConfig)new PolicyConfigClientCom();
            int hr = _policy.SetDefaultEndpoint(endpointId, role);
            if (hr != 0) Log.Write($"Varsayılan aygıt ayarlanamadı ({role}): 0x{hr:X8}");
            return hr == 0;
        }
        catch (Exception ex)
        {
            Log.Write("Varsayılan aygıt ayarlanamadı: " + ex.Message);
            return false;
        }
    }

    /// <summary>Ses denetim masasındaki "Devre dışı bırak / Etkinleştir" ile aynı işlem.</summary>
    public bool SetVisibility(string endpointId, bool visible)
    {
        try
        {
            _policy ??= (IPolicyConfig)new PolicyConfigClientCom();
            int hr = _policy.SetEndpointVisibility(endpointId, visible ? 1 : 0);
            if (hr != 0) Log.Write($"Uç nokta görünürlüğü değiştirilemedi: 0x{hr:X8}");
            return hr == 0;
        }
        catch (Exception ex)
        {
            Log.Write("Uç nokta görünürlüğü değiştirilemedi: " + ex.Message);
            return false;
        }
    }

    /// <summary>Uç noktada etkin (ses akıtan) oturumu olan süreçler.</summary>
    public List<SessionInfo> GetActiveSessions(string endpointId)
    {
        var list = new List<SessionInfo>();
        if (_enum.GetDevice(endpointId, out var device) != 0 || device == null) return list;
        object? managerObj = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            Guid iid = IidSessionManager2;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out managerObj) != 0 || managerObj == null) return list;
            if (((IAudioSessionManager2)managerObj).GetSessionEnumerator(out sessions) != 0 || sessions == null) return list;
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out object sessionObj) != 0 || sessionObj == null) continue;
                try
                {
                    var control = (IAudioSessionControl2)sessionObj;
                    if (control.GetState(out int state) != 0 || state != 1) continue;
                    if (control.GetProcessId(out uint pid) != 0 || pid == 0) continue;
                    string processName = "pid " + pid;
                    try
                    {
                        using var process = Process.GetProcessById((int)pid);
                        processName = process.ProcessName;
                    }
                    catch
                    {
                        // Süreç kapanmış ya da erişilemiyor olabilir.
                    }
                    if (!list.Any(s => s.ProcessId == (int)pid)) list.Add(new SessionInfo((int)pid, processName));
                }
                finally
                {
                    Rel(sessionObj);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Oturumlar okunamadı: " + ex.Message);
        }
        finally
        {
            Rel(sessions);
            Rel(managerObj);
            Rel(device);
        }
        return list;
    }

    public float GetPeak(string endpointId)
    {
        if (_enum.GetDevice(endpointId, out var device) != 0 || device == null) return 0;
        object? meterObj = null;
        try
        {
            Guid iid = IidMeter;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out meterObj) != 0 || meterObj == null) return 0;
            return ((IAudioMeterInformation)meterObj).GetPeakValue(out float peak) == 0 ? peak : 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            Rel(meterObj);
            Rel(device);
        }
    }

    /// <summary>
    /// Kısa bir sessiz akış açar. Duyulabilir ses üretmez; akış kapanırken sürücü telemetrisi
    /// kullanılan codec'i bildirdiği için bağlantı bilgisini doldurmaya yarar.
    /// </summary>
    public bool ProbeSilence(string endpointId, int milliseconds)
    {
        if (_enum.GetDevice(endpointId, out var device) != 0 || device == null) return false;
        object? clientObj = null;
        IntPtr format = IntPtr.Zero;
        try
        {
            Guid iid = IidAudioClient;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out clientObj) != 0 || clientObj == null) return false;
            var client = (IAudioClient)clientObj;
            if (client.GetMixFormat(out format) != 0) return false;
            if (client.Initialize(0, 0, 2_000_000, 0, format, IntPtr.Zero) != 0) return false;
            client.GetBufferSize(out uint frames);
            Guid renderIid = IidRenderClient;
            if (client.GetService(ref renderIid, out object renderObj) != 0 || renderObj == null) return false;
            try
            {
                var render = (IAudioRenderClient)renderObj;
                if (render.GetBuffer(frames, out _) == 0) render.ReleaseBuffer(frames, 2 /* sessiz */);
            }
            finally
            {
                Rel(renderObj);
            }
            if (client.Start() != 0) return false;
            Thread.Sleep(milliseconds);
            client.Stop();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Sessiz akış açılamadı: " + ex.Message);
            return false;
        }
        finally
        {
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            Rel(clientObj);
            Rel(device);
        }
    }

    /// <summary>
    /// Gecikme ölçümü için: kesintisiz bir akış açar ve verilen anlarda (akışın başından itibaren ms) kısa gürültü
    /// patlamaları çalar. Her patlamanın ilk örneğinin ses motoruna ulaşacağı an (Stopwatch tik) <paramref name="queued"/>
    /// ile bildirilir: yazma anı artı o sırada kuyrukta bekleyen sesin süresi.
    /// </summary>
    /// <param name="channelMask">Patlamanın çalınacağı kanallar (bit 0 sol, bit 1 sağ).</param>
    public unsafe bool PlayBursts(string endpointId, IReadOnlyList<int> startsMs, int burstMs, float amplitude, int channelMask, Action<int, long> queued)
    {
        if (_enum.GetDevice(endpointId, out var device) != 0 || device == null) return false;
        object? clientObj = null;
        IntPtr format = IntPtr.Zero;
        try
        {
            Guid iid = IidAudioClient;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out clientObj) != 0 || clientObj == null) return false;
            var client = (IAudioClient)clientObj;
            if (client.GetMixFormat(out format) != 0) return false;
            int channels = Marshal.ReadInt16(format, 2);
            int rate = Marshal.ReadInt32(format, 4);
            int bits = Marshal.ReadInt16(format, 14);
            if (bits != 32 || channels < 1) return false; // paylaşımlı kipte karışım biçimi 32 bit kayan noktadır
            if (client.Initialize(0, 0, 1_000_000, 0, format, IntPtr.Zero) != 0) return false;
            client.GetBufferSize(out uint bufferFrames);
            Guid renderIid = IidRenderClient;
            if (client.GetService(ref renderIid, out object renderObj) != 0 || renderObj == null) return false;
            try
            {
                var render = (IAudioRenderClient)renderObj;
                var random = new Random(20261117);
                long burstFrames = (long)burstMs * rate / 1000;
                long[] starts = startsMs.Select(ms => (long)ms * rate / 1000).ToArray();
                long total = starts.Max() + burstFrames + (rate * 4 / 10);
                int targetQueue = rate * 40 / 1000; // kuyrukta 40 ms tutulur: zamanlayıcı gecikse de akış kesilmez
                long written = 0;
                if (client.Start() != 0) return false;
                while (written < total)
                {
                    client.GetCurrentPadding(out uint padding);
                    int room = (int)Math.Min(bufferFrames - padding, Math.Max(0, targetQueue - (int)padding));
                    room = (int)Math.Min(room, total - written);
                    if (room <= 0)
                    {
                        Thread.Sleep(2);
                        continue;
                    }
                    if (render.GetBuffer((uint)room, out IntPtr data) != 0) return false;
                    long now = Stopwatch.GetTimestamp();
                    float* samples = (float*)data;
                    for (int i = 0; i < room; i++)
                    {
                        long frame = written + i;
                        float value = 0;
                        for (int b = 0; b < starts.Length; b++)
                        {
                            if (frame < starts[b] || frame >= starts[b] + burstFrames) continue;
                            value = amplitude * ((random.NextSingle() * 2) - 1);
                            if (frame == starts[b]) queued(b, now + ((padding + i) * Stopwatch.Frequency / rate));
                        }
                        for (int c = 0; c < channels; c++) samples[(i * channels) + c] = (channelMask & (1 << c)) != 0 ? value : 0;
                    }
                    render.ReleaseBuffer((uint)room, 0);
                    written += room;
                    Thread.Sleep(2);
                }
                Thread.Sleep(200);
                client.Stop();
                return true;
            }
            finally
            {
                Rel(renderObj);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Ölçüm akışı açılamadı: " + ex.Message);
            return false;
        }
        finally
        {
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            Rel(clientObj);
            Rel(device);
        }
    }

    /// <summary>Bluetooth ses filtresine "yeniden bağlan" ya da "bağlantıyı kes" isteği gönderir.</summary>
    public bool BtOneShot(string filterId, bool reconnect)
    {
        if (string.IsNullOrEmpty(filterId)) return false;
        if (_enum.GetDevice(filterId, out var device) != 0 || device == null) return false;
        object? controlObj = null;
        try
        {
            Guid iid = IidKsControl;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out controlObj) != 0 || controlObj == null) return false;
            var property = new KSPROPERTY { Set = KsPropSetBtAudio, Id = reconnect ? 0 : 1, Flags = 1 };
            int hr = ((IKsControl)controlObj).KsProperty(ref property, Marshal.SizeOf<KSPROPERTY>(), IntPtr.Zero, 0, out _);
            if (hr != 0) Log.Write($"Bluetooth {(reconnect ? "bağlan" : "kes")} isteği başarısız: 0x{hr:X8}");
            return hr == 0;
        }
        catch (Exception ex)
        {
            Log.Write("Bluetooth isteği gönderilemedi: " + ex.Message);
            return false;
        }
        finally
        {
            Rel(controlObj);
            Rel(device);
        }
    }

    private static string? GetString(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var value) != 0) return null;
        try
        {
            return value.vt == 31 && value.p != IntPtr.Zero ? Marshal.PtrToStringUni(value.p) : null;
        }
        finally
        {
            Ole32.PropVariantClear(ref value);
        }
    }

    private static Guid GetGuid(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var value) != 0) return Guid.Empty;
        try
        {
            return value.vt == 72 && value.p != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(value.p) : Guid.Empty;
        }
        finally
        {
            Ole32.PropVariantClear(ref value);
        }
    }

    private static void Rel(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
    }

    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly Action _changed;

        public NotificationClient(Action changed) => _changed = changed;

        public int OnDeviceStateChanged(string deviceId, int newState) { _changed(); return 0; }
        public int OnDeviceAdded(string deviceId) { _changed(); return 0; }
        public int OnDeviceRemoved(string deviceId) { _changed(); return 0; }
        public int OnDefaultDeviceChanged(DataFlow flow, Role role, string? defaultDeviceId) { _changed(); return 0; }
        public int OnPropertyValueChanged(string deviceId, PROPERTYKEY key) => 0;
    }
}
