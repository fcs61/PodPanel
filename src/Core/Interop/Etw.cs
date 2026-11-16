using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PodPanel.Core.Interop;

/// <summary>Çözülmüş bir TraceLogging olayı.</summary>
public sealed class TlEvent
{
    public Guid Provider { get; init; }
    public string Name { get; init; } = "";
    public DateTime Time { get; init; }
    public Dictionary<string, object> Fields { get; init; } = new(StringComparer.Ordinal);

    public bool TryNumber(string field, out long value)
    {
        value = 0;
        if (!Fields.TryGetValue(field, out var raw)) return false;
        switch (raw)
        {
            case bool b: value = b ? 1 : 0; return true;
            case byte v: value = v; return true;
            case sbyte v: value = v; return true;
            case short v: value = v; return true;
            case ushort v: value = v; return true;
            case int v: value = v; return true;
            case uint v: value = v; return true;
            case long v: value = v; return true;
            case ulong v: value = unchecked((long)v); return true;
            default: return false;
        }
    }

    public long Number(string field, long fallback = 0) => TryNumber(field, out long value) ? value : fallback;

    public string Text(string field) =>
        Fields.TryGetValue(field, out var raw) ? Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "" : "";

    public override string ToString()
    {
        var parts = Fields.Select(kv => kv.Key + "=" + (kv.Value is Array a
            ? "[" + string.Join(",", a.Cast<object>()) + "]"
            : Convert.ToString(kv.Value, CultureInfo.InvariantCulture)));
        return Name + " :: " + string.Join("; ", parts);
    }
}

/// <summary>
/// TraceLogging olaylarının kendini tanımlayan üstverisini ve verisini çözer.
/// Üstveri: [u16 boyut][uzantı baytları][olay adı\0] ardından her alan için [ad\0][giriş türü][çıkış türü?]...
/// </summary>
internal static unsafe class TraceLoggingDecoder
{
    public static bool TryDecode(byte* meta, int metaLen, byte* data, int dataLen, out string name, Dictionary<string, object> fields)
    {
        name = "";
        if (meta == null || metaLen < 4) return false;
        int mp = 2;
        while (mp < metaLen)
        {
            byte extension = meta[mp++];
            if ((extension & 0x80) == 0) break;
        }
        name = ReadUtf8Z(meta, metaLen, ref mp);
        int dp = 0;
        return DecodeFields(meta, metaLen, ref mp, data, dataLen, ref dp, fields, "", int.MaxValue, false);
    }

    private static string ReadUtf8Z(byte* p, int len, ref int pos)
    {
        int start = pos;
        while (pos < len && p[pos] != 0) pos++;
        string text = Encoding.UTF8.GetString(p + start, pos - start);
        if (pos < len) pos++;
        return text;
    }

    private static bool DecodeFields(byte* meta, int metaLen, ref int mp, byte* data, int dataLen, ref int dp,
        Dictionary<string, object> fields, string prefix, int maxFields, bool metadataOnly)
    {
        int count = 0;
        while (mp < metaLen && count < maxFields)
        {
            string fieldName = ReadUtf8Z(meta, metaLen, ref mp);
            if (mp >= metaLen) return false;
            byte inType = meta[mp++];
            byte outType = 0;
            if ((inType & 0x80) != 0)
            {
                if (mp >= metaLen) return false;
                outType = meta[mp++];
                if ((outType & 0x80) != 0)
                {
                    while (mp < metaLen)
                    {
                        byte tag = meta[mp++];
                        if ((tag & 0x80) == 0) break;
                    }
                }
            }

            int countMode = inType & 0x60;
            int baseType = inType & 0x1F;
            int elements = 1;
            bool isArray = false;
            if (countMode == 0x20)
            {
                if (mp + 2 > metaLen) return false;
                elements = *(ushort*)(meta + mp);
                mp += 2;
                isArray = true;
            }
            else if (countMode == 0x40)
            {
                isArray = true;
                if (metadataOnly)
                {
                    elements = 0;
                }
                else
                {
                    if (dp + 2 > dataLen) return false;
                    elements = *(ushort*)(data + dp);
                    dp += 2;
                }
            }
            else if (countMode == 0x60)
            {
                if (mp + 2 > metaLen) return false;
                int typeInfoSize = *(ushort*)(meta + mp);
                mp += 2 + typeInfoSize;
                if (!metadataOnly)
                {
                    if (dp + 2 > dataLen) return false;
                    int size = *(ushort*)(data + dp);
                    dp += 2 + size;
                    fields[prefix + fieldName] = "<özel>";
                }
                count++;
                continue;
            }

            string key = prefix + fieldName;
            if (baseType == 24)
            {
                // Yapı: çıkış türünün alt 7 biti alan sayısıdır, kendi verisi yoktur.
                int structFields = outType & 0x7F;
                if (metadataOnly || elements == 0)
                {
                    if (!DecodeFields(meta, metaLen, ref mp, data, dataLen, ref dp, fields, key + ".", structFields, true)) return false;
                }
                else
                {
                    int metaStart = mp;
                    for (int i = 0; i < elements; i++)
                    {
                        mp = metaStart;
                        string childPrefix = isArray ? $"{key}[{i}]." : key + ".";
                        if (!DecodeFields(meta, metaLen, ref mp, data, dataLen, ref dp, fields, childPrefix, structFields, false)) return false;
                    }
                }
                count++;
                continue;
            }

            if (!metadataOnly)
            {
                if (!isArray)
                {
                    if (!ReadValue(baseType, data, dataLen, ref dp, out object value)) return false;
                    fields[key] = value;
                }
                else
                {
                    var values = new object[elements];
                    for (int i = 0; i < elements; i++)
                    {
                        if (!ReadValue(baseType, data, dataLen, ref dp, out values[i])) return false;
                    }
                    fields[key] = values;
                }
            }
            count++;
        }
        return true;
    }

    private static bool ReadValue(int type, byte* d, int len, ref int p, out object value)
    {
        value = "";
        switch (type)
        {
            case 1: // sıfırla biten UTF-16
            {
                int start = p;
                while (p + 1 < len && *(ushort*)(d + p) != 0) p += 2;
                value = new string((char*)(d + start), 0, (p - start) / 2);
                p = Math.Min(p + 2, len);
                return true;
            }
            case 2: // sıfırla biten 8 bitlik dizgi
            {
                int start = p;
                while (p < len && d[p] != 0) p++;
                value = Encoding.UTF8.GetString(d + start, p - start);
                if (p < len) p++;
                return true;
            }
            case 3: if (p + 1 > len) return false; value = (sbyte)d[p]; p += 1; return true;
            case 4: if (p + 1 > len) return false; value = d[p]; p += 1; return true;
            case 5: if (p + 2 > len) return false; value = *(short*)(d + p); p += 2; return true;
            case 6: if (p + 2 > len) return false; value = *(ushort*)(d + p); p += 2; return true;
            case 7: if (p + 4 > len) return false; value = *(int*)(d + p); p += 4; return true;
            case 8:
            case 20: if (p + 4 > len) return false; value = *(uint*)(d + p); p += 4; return true;
            case 9:
            case 17: if (p + 8 > len) return false; value = *(long*)(d + p); p += 8; return true;
            case 10:
            case 16:
            case 21: if (p + 8 > len) return false; value = *(ulong*)(d + p); p += 8; return true;
            case 11: if (p + 4 > len) return false; value = *(float*)(d + p); p += 4; return true;
            case 12: if (p + 8 > len) return false; value = *(double*)(d + p); p += 8; return true;
            case 13: if (p + 4 > len) return false; value = *(int*)(d + p) != 0; p += 4; return true;
            case 14:
            case 25: // u16 uzunluk + bayt dizisi
            {
                if (p + 2 > len) return false;
                int size = *(ushort*)(d + p);
                p += 2;
                if (p + size > len) return false;
                value = new ReadOnlySpan<byte>(d + p, size).ToArray();
                p += size;
                return true;
            }
            case 15: if (p + 16 > len) return false; value = *(Guid*)(d + p); p += 16; return true;
            case 18: if (p + 16 > len) return false; value = "<systemtime>"; p += 16; return true;
            case 19:
            {
                if (p + 8 > len) return false;
                int size = 8 + 4 * d[p + 1];
                if (p + size > len) return false;
                value = "<sid>";
                p += size;
                return true;
            }
            case 22: // u16 bayt uzunluğu + UTF-16
            {
                if (p + 2 > len) return false;
                int size = *(ushort*)(d + p);
                p += 2;
                if (p + size > len) return false;
                value = new string((char*)(d + p), 0, size / 2);
                p += size;
                return true;
            }
            case 23: // u16 uzunluk + 8 bitlik dizgi
            {
                if (p + 2 > len) return false;
                int size = *(ushort*)(d + p);
                p += 2;
                if (p + size > len) return false;
                value = Encoding.UTF8.GetString(d + p, size);
                p += size;
                return true;
            }
            default:
                return false;
        }
    }
}

/// <summary>
/// Gerçek zamanlı ETW oturumu. Yönetici yetkisi gerekmez; kullanıcının "Performance Log Users"
/// grubunda (ya da yönetici) olması yeterlidir. Yapı düzenleri x64 içindir.
/// </summary>
public sealed unsafe class EtwRealtimeSession : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void EventRecordCallback(IntPtr record);

    private const int PropsSize = 120;          // sizeof(EVENT_TRACE_PROPERTIES)
    private const int NameSpace = 2048;         // ad alanları için ayrılan bayt
    private const int LogFileSize = 448;        // sizeof(EVENT_TRACE_LOGFILEW)
    private const uint RealTimeMode = 0x100;
    private const uint TracedGuidFlag = 0x00020000;
    private const uint ProcessRealTime = 0x100;
    private const uint ProcessEventRecord = 0x10000000;
    private const uint ControlStop = 1;
    private const int ErrorAlreadyExists = 183;
    private const ushort ExtTypeTraceLoggingSchema = 0x000B;

    private readonly string _name;
    private ulong _session;
    private ulong _trace = ulong.MaxValue;
    private Thread? _thread;
    private EventRecordCallback? _callback;
    private IntPtr _logFile;
    private IntPtr _namePtr;
    private bool _stopped;

    public event Action<TlEvent>? EventReceived;

    public bool Running { get; private set; }

    public EtwRealtimeSession(string name) => _name = name;

    /// <summary>Oturumu başlatır; 0 başarı, aksi halde Win32 hata kodu (5 = erişim reddedildi).</summary>
    public int Start(IReadOnlyList<Guid> providers)
    {
        int size = PropsSize + 2 * NameSpace;
        IntPtr props = Marshal.AllocHGlobal(size);
        try
        {
            int rc = 0;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                InitProps(props, size, true);
                rc = StartTraceW(out _session, _name, props);
                if (rc != ErrorAlreadyExists) break;
                // Önceki çalıştırmadan kalan oturumu kapatıp yeniden dene.
                InitProps(props, size, false);
                ControlTraceW(0, _name, props, ControlStop);
            }
            if (rc != 0) return rc;

            foreach (Guid provider in providers)
            {
                Guid copy = provider;
                int enable = EnableTraceEx2(_session, in copy, 1, 0xFF, ulong.MaxValue, 0, 0, IntPtr.Zero);
                if (enable != 0) Log.Write($"ETW sağlayıcısı açılamadı {provider}: {enable}");
            }

            _logFile = Marshal.AllocHGlobal(LogFileSize);
            new Span<byte>((void*)_logFile, LogFileSize).Clear();
            _namePtr = Marshal.StringToHGlobalUni(_name);
            Marshal.WriteIntPtr(_logFile, 8, _namePtr);                                       // LoggerName
            Marshal.WriteInt32(_logFile, 28, unchecked((int)(ProcessRealTime | ProcessEventRecord)));
            _callback = OnRecord;
            Marshal.WriteIntPtr(_logFile, 424, Marshal.GetFunctionPointerForDelegate(_callback)); // EventRecordCallback

            _trace = OpenTraceW(_logFile);
            if (_trace == ulong.MaxValue)
            {
                int error = Marshal.GetLastWin32Error();
                StopSession();
                return error == 0 ? -1 : error;
            }

            ulong trace = _trace;
            _thread = new Thread(() =>
            {
                int result = ProcessTrace(new[] { trace }, 1, IntPtr.Zero, IntPtr.Zero);
                if (result != 0) Log.Write($"ETW işleme döngüsü bitti: {result}");
            })
            {
                IsBackground = true,
                Name = "PodPanel.ETW",
            };
            _thread.Start();
            Running = true;
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }
    }

    private void InitProps(IntPtr props, int size, bool realTime)
    {
        new Span<byte>((void*)props, size).Clear();
        Marshal.WriteInt32(props, 0, size);                                  // Wnode.BufferSize
        Marshal.WriteInt32(props, 40, 1);                                    // Wnode.ClientContext = QPC
        Marshal.WriteInt32(props, 44, unchecked((int)TracedGuidFlag));       // Wnode.Flags
        Marshal.WriteInt32(props, 48, 64);                                   // BufferSize (KB)
        Marshal.WriteInt32(props, 52, 2);                                    // MinimumBuffers
        Marshal.WriteInt32(props, 56, 8);                                    // MaximumBuffers
        Marshal.WriteInt32(props, 64, realTime ? unchecked((int)RealTimeMode) : 0); // LogFileMode
        Marshal.WriteInt32(props, 68, 1);                                    // FlushTimer (sn)
        Marshal.WriteInt32(props, 112, realTime ? 0 : PropsSize + NameSpace); // LogFileNameOffset
        Marshal.WriteInt32(props, 116, PropsSize);                           // LoggerNameOffset
    }

    private void OnRecord(IntPtr record)
    {
        try
        {
            byte* rec = (byte*)record;
            Guid provider = *(Guid*)(rec + 24);
            ushort extendedCount = *(ushort*)(rec + 84);
            ushort userLength = *(ushort*)(rec + 86);
            byte* extended = *(byte**)(rec + 88);
            byte* user = *(byte**)(rec + 96);

            byte* meta = null;
            int metaLength = 0;
            for (int i = 0; i < extendedCount && extended != null; i++)
            {
                byte* item = extended + i * 16;
                if (*(ushort*)(item + 2) != ExtTypeTraceLoggingSchema) continue;
                metaLength = *(ushort*)(item + 6);
                meta = (byte*)*(ulong*)(item + 8);
                break;
            }
            if (meta == null) return;

            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            TraceLoggingDecoder.TryDecode(meta, metaLength, user, userLength, out string name, fields);
            if (name.Length == 0) return;
            EventReceived?.Invoke(new TlEvent { Provider = provider, Name = name, Time = DateTime.Now, Fields = fields });
        }
        catch (Exception ex)
        {
            Log.Write("ETW olayı işlenemedi: " + ex.Message);
        }
    }

    private void StopSession()
    {
        if (_session == 0) return;
        int size = PropsSize + 2 * NameSpace;
        IntPtr props = Marshal.AllocHGlobal(size);
        try
        {
            InitProps(props, size, false);
            ControlTraceW(_session, null, props, ControlStop);
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }
        _session = 0;
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        Running = false;
        StopSession();
        if (_trace != ulong.MaxValue)
        {
            CloseTrace(_trace);
            _trace = ulong.MaxValue;
        }
        bool ended = _thread == null || _thread.Join(3000);
        if (ended)
        {
            if (_logFile != IntPtr.Zero) Marshal.FreeHGlobal(_logFile);
            if (_namePtr != IntPtr.Zero) Marshal.FreeHGlobal(_namePtr);
        }
        _logFile = IntPtr.Zero;
        _namePtr = IntPtr.Zero;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StartTraceW(out ulong traceHandle, string instanceName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ControlTraceW(ulong traceHandle, string? instanceName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int EnableTraceEx2(ulong traceHandle, in Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ulong OpenTraceW(IntPtr logFile);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int CloseTrace(ulong traceHandle);
}
