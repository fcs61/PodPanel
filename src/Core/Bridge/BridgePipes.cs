using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PodPanel.Core.Bridge;

/// <summary>
/// Köprü servisinin adlandırılmış kanallarına istemci tarafı: yerel, ileti kipinde, tamsayılar küçük-sonlu.
/// Protokol bridge/src/WPods.Service/Protocol.cs dosyasında tanımlıdır.
/// </summary>
public static class BridgePipes
{
    public const string AapPipe = "WPods.Aap";
    public const string ServicePipe = "WPods.Service";
    public const string ServiceName = "WPodsService";

    /// <summary>Kulaklığın kontrol protokolü (AAP).</summary>
    public const ushort AapPsm = 0x1001;
    /// <summary>Ses akışı işaretleşmesi (AVDTP).</summary>
    public const ushort AvdtpPsm = 0x0019;

    // Servisten gelen iletilerin ilk baytı.
    public const byte AapPacket = 0x00;
    public const byte AapEnded = 0x01;
    public const byte AapOpenReply = 0x02;
    public const byte AapCallAudio = 0x03;
    public const byte ServiceStateMessage = 0x20;
    public const byte ServiceRetryMessage = 0x21;

    public const int ErrorNoPipe = 2;
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidData = 13;
    public const int ErrorBrokenPipe = 109;
    public const int ErrorTimeout = 121;
    public const int ErrorBusy = 170;
    public const int ErrorPipeBusy = 231;
    public const int ErrorNotConnected = 1167;
    public const int ErrorRefused = 1225;

    // Veri ve öznitelik okuma/yazma ile SYNCHRONIZE: kanalın etkileşimli kullanıcılara verdiği hakların tamamı.
    // GENERIC_WRITE, kanal örneği oluşturma hakkını da isteyeceği için reddedilir.
    private const uint PipeAccess = 0x00100183;
    private const uint OpenExisting = 3;
    // Sunucu bu kullanıcıyı yalnızca tanıyabilir, onun adına işlem yapamaz (ad başka bir sürece aitse diye).
    private const uint Flags = 0x40000000 /* FILE_FLAG_OVERLAPPED */ | 0x00100000 /* SECURITY_SQOS_PRESENT */ | 0x00010000 /* SECURITY_IDENTIFICATION */;

    /// <summary>Kanalı ileti kipinde açar; açılamazsa null döner ve <paramref name="error"/> Win32 hata kodunu taşır.</summary>
    public static NamedPipeClientStream? Connect(string name, out int error)
    {
        string path = @"\\.\pipe\" + name;
        SafePipeHandle handle = CreateFile(path, PipeAccess, 0, 0, OpenExisting, Flags, 0);
        error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (error == ErrorPipeBusy && WaitNamedPipe(path, 2000))
        {
            handle.Dispose();
            handle = CreateFile(path, PipeAccess, 0, 0, OpenExisting, Flags, 0);
            error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        }
        if (error == 0 && !IsFromService(handle))
        {
            // Aynı adı başka bir süreç açmış: kulaklık komutları ona gönderilmez.
            error = ErrorAccessDenied;
        }
        if (error != 0)
        {
            handle.Dispose();
            return null;
        }
        var pipe = new NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, handle);
        try
        {
            pipe.ReadMode = PipeTransmissionMode.Message;
            return pipe;
        }
        catch (IOException e)
        {
            pipe.Dispose();
            error = e.HResult & 0xFFFF;
            return null;
        }
    }

    /// <summary>
    /// <paramref name="address"/> adresli kulaklığa kontrol kanalı açar. Hata kodları: 1167 kulaklık bu bilgisayara
    /// bağlı değil, 170 kanal başka bir istemcide açık, 1225 kulaklık reddetti, 121 yanıt yok, 2 servis çalışmıyor.
    /// </summary>
    public static NamedPipeClientStream? OpenAap(ulong address, out int error, ushort psm = AapPsm)
    {
        if (Connect(AapPipe, out error) is not { } pipe) return null;
        try
        {
            // 10 bayt: kontrol kanalı. 12 bayt: sonunda bağlanılacak PSM (servisin .noupdate.5 ve sonrası sürümleri).
            byte[] hello = new byte[psm == AapPsm ? 10 : 12];
            hello[0] = (byte)'A';
            hello[1] = (byte)'P';
            BinaryPrimitives.WriteUInt64LittleEndian(hello.AsSpan(2), address);
            if (psm != AapPsm) BinaryPrimitives.WriteUInt16LittleEndian(hello.AsSpan(10), psm);
            pipe.Write(hello);
            byte[] reply = new byte[16];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            int count = pipe.ReadAsync(reply, timeout.Token).AsTask().GetAwaiter().GetResult();
            error = count == 0 ? ErrorBrokenPipe
                : count == 5 && reply[0] == AapOpenReply ? BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(1))
                : ErrorInvalidData;
        }
        catch (OperationCanceledException)
        {
            error = ErrorTimeout;
        }
        catch (IOException e)
        {
            error = e.HResult & 0xFFFF;
        }
        if (error == 0) return pipe;
        pipe.Dispose();
        return null;
    }

    public static string DescribeError(int error) => error switch
    {
        0 => "başarılı",
        ErrorNoPipe => "köprü servisi çalışmıyor",
        ErrorAccessDenied => "kanal köprü servisine ait değil ya da erişim reddedildi",
        ErrorBrokenPipe => "servis kanalı kapattı",
        ErrorTimeout => "yanıt gelmedi",
        ErrorBusy => "kontrol kanalı başka bir programda açık",
        ErrorPipeBusy => "servis meşgul",
        ErrorNotConnected => "kulaklık bu bilgisayara bağlı değil",
        ErrorRefused => "kulaklık bağlantıyı reddetti",
        ErrorInvalidData => "servisten beklenmeyen yanıt",
        _ => $"Win32 hatası {error}",
    };

    // Kanalın sunucusu gerçekten köprü servisinin süreci mi?
    private static unsafe bool IsFromService(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint server)) return false;
        nint manager = OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
        if (manager == 0) return false;
        try
        {
            nint service = OpenService(manager, ServiceName, 0x0004 /* SERVICE_QUERY_STATUS */);
            if (service == 0) return false;
            try
            {
                // SERVICE_STATUS_PROCESS: yedi DWORD durum, ardından dwProcessId ve dwServiceFlags.
                uint* status = stackalloc uint[9];
                return QueryServiceStatusEx(service, 0 /* SC_STATUS_PROCESS_INFO */, (byte*)status, 9 * sizeof(uint), out _)
                    && status[7] == server;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, uint timeoutMs);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenService(nint manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool QueryServiceStatusEx(nint service, int level, byte* buffer, uint size, out uint needed);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
