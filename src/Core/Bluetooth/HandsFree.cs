using System.Runtime.InteropServices;

namespace PodPanel.Core.Bluetooth;

/// <summary>
/// Eşleşmiş bir aygıtın Bluetooth servislerini Windows'ta açar ya da kapatır; aygıt özelliklerindeki servis kutularıyla
/// aynı ayardır ve yönetici yetkisi istemez. Çağrılar birkaç saniye sürebilir.
/// </summary>
public static unsafe class BluetoothServices
{
    /// <summary>El-serbest (HFP, 0x111E): kapalıyken Windows kulaklığı arama (mono) moduna alamaz.</summary>
    public static readonly Guid HandsFree = new("0000111E-0000-1000-8000-00805F9B34FB");

    /// <summary>Ses alıcısı (A2DP, 0x110B): kapalıyken Windows kulaklığa kendi ses akışını açmaz ve çıkış aygıtı kaybolur.</summary>
    public static readonly Guid AudioSink = new("0000110B-0000-1000-8000-00805F9B34FB");

    private const int DeviceInfoSize = 560; // BLUETOOTH_DEVICE_INFO (x64)
    private const int AddressOffset = 8;
    private const uint ErrorSuccess = 0;
    private const uint ErrorInvalidParameter = 87;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNotFound = 1168;
    private const uint EInvalidArg = 0x80070057;

    /// <summary>Servis açık mı; aygıt bulunamaz ya da durum okunamazsa null.</summary>
    public static bool? IsEnabled(ulong address, Guid service)
    {
        nint radio = OpenRadio();
        try
        {
            byte* device = stackalloc byte[DeviceInfoSize];
            return FillDevice(radio, address, device) ? Query(radio, device, service) : null;
        }
        finally
        {
            if (radio != 0) CloseHandle(radio);
        }
    }

    /// <summary>Servisi açar ya da kapatır; zaten o durumdaysa dokunmaz. Başarısızsa nedeni <paramref name="error"/> taşır.</summary>
    public static bool SetEnabled(ulong address, Guid service, bool enabled, out string error)
    {
        error = "";
        nint radio = OpenRadio();
        try
        {
            byte* device = stackalloc byte[DeviceInfoSize];
            if (!FillDevice(radio, address, device))
            {
                error = "Windows bu kulaklığı eşleşmiş aygıtlar arasında bulamadı";
                return false;
            }
            if (Query(radio, device, service) == enabled) return true;
            uint result = BluetoothSetServiceState(radio, device, &service, enabled ? 1u : 0u);
            // Windows, zaten uygulanmış durumu E_INVALIDARG ya da ERROR_INVALID_PARAMETER ile bildirebilir.
            if (result is ErrorSuccess or EInvalidArg) return true;
            if (result == ErrorInvalidParameter && Query(radio, device, service) == enabled) return true;
            if (Query(radio, device, service) == enabled) return true;
            error = $"Windows servisi {(enabled ? "açamadı" : "kapatamadı")} (hata {result})";
            return false;
        }
        finally
        {
            if (radio != 0) CloseHandle(radio);
        }
    }

    private static nint OpenRadio()
    {
        uint size = 4; // BLUETOOTH_FIND_RADIO_PARAMS: yalnızca dwSize
        nint find = BluetoothFindFirstRadio(&size, out nint radio);
        if (find == 0) return 0;
        BluetoothFindRadioClose(find);
        return radio;
    }

    private static bool FillDevice(nint radio, ulong address, byte* device)
    {
        new Span<byte>(device, DeviceInfoSize).Clear();
        *(uint*)device = DeviceInfoSize;
        *(ulong*)(device + AddressOffset) = address;
        return BluetoothGetDeviceInfo(radio, device) == ErrorSuccess;
    }

    private static bool? Query(nint radio, byte* device, Guid service)
    {
        uint count = 0;
        uint result = BluetoothEnumerateInstalledServices(radio, device, ref count, null);
        if (result == ErrorNotFound) return false;
        if (result is not (ErrorSuccess or ErrorMoreData)) return null;
        if (count == 0) return result == ErrorSuccess ? false : null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var services = new Guid[count];
            uint capacity = count;
            fixed (Guid* p = services)
            {
                result = BluetoothEnumerateInstalledServices(radio, device, ref capacity, p);
            }
            if (result == ErrorNotFound) return false;
            if (result == ErrorMoreData)
            {
                count = capacity > count ? capacity : count + 1;
                continue;
            }
            if (result != ErrorSuccess) return null;
            return services.AsSpan(0, (int)Math.Min(capacity, count)).Contains(service);
        }
        return null;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern nint BluetoothFindFirstRadio(uint* parameters, out nint radio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindRadioClose(nint find);

    [DllImport("bthprops.cpl")]
    private static extern uint BluetoothGetDeviceInfo(nint radio, byte* device);

    [DllImport("bthprops.cpl")]
    private static extern uint BluetoothEnumerateInstalledServices(nint radio, byte* device, ref uint count, Guid* services);

    [DllImport("bthprops.cpl")]
    private static extern uint BluetoothSetServiceState(nint radio, byte* device, Guid* service, uint flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

/// <summary>El-serbest servisinin kısayolları (bkz. <see cref="BluetoothServices"/>).</summary>
public static class HandsFree
{
    public static bool? IsEnabled(ulong address) => BluetoothServices.IsEnabled(address, BluetoothServices.HandsFree);

    public static bool SetEnabled(ulong address, bool enabled, out string error) =>
        BluetoothServices.SetEnabled(address, BluetoothServices.HandsFree, enabled, out error);
}
