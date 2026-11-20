using System.Buffers.Binary;

namespace PodPanel.Core.Bridge;

/// <summary>Kulaklığın dinleme modu (kontrol ayarı 0x0D).</summary>
public enum ListeningMode : byte
{
    Off = 1,
    NoiseCancellation = 2,
    Transparency = 3,
    Adaptive = 4,
}

/// <summary>Pil bildirimindeki bir bileşen.</summary>
public readonly record struct BatteryEntry(byte Component, int Level, byte Status)
{
    public bool Charging => Status is 0x01 or 0x05;
    public bool Present => Status is not (0x04 or 0xFF) && Level is >= 0 and <= 100;
}

/// <summary>AirPods kontrol protokolü (AAP) paketleri: gönderilenler ve gelenlerin çözümü.</summary>
public static class Aap
{
    public const byte ListeningModeId = 0x0D;

    /// <summary>
    /// Kulaklığın gecikme tamponu hedefi, milisaniye (kontrol komutu 0x0B). Tanım USBPods projesinin kaynağından;
    /// bu kulaklıkta 30, 60, 90 ve 150 denendi ve her birinde kulaklık Windows'a aynı değeri gecikme olarak bildirdi.
    /// 0 yok sayılır. Kulaklığın kendi varsayılanı 150'dir.
    /// </summary>
    public const byte JitterBufferId = 0x0B;
    public const int DefaultJitterMs = 150;

    public static byte[] SetJitterBuffer(int milliseconds) => Setting(JitterBufferId, (byte)Math.Clamp(milliseconds, 20, 250));

    public const byte HeadsetComponent = 0x01;
    public const byte RightComponent = 0x02;
    public const byte LeftComponent = 0x04;
    public const byte CaseComponent = 0x08;

    private const uint Header = 0x00040004; // 04 00 04 00

    public static readonly byte[] Handshake = { 0x00, 0x00, 0x04, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
    public static readonly byte[] FeatureFlags = { 0x04, 0x00, 0x04, 0x00, 0x4D, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>Bildirim isteği. Üçüncü süzgeç baytı, ana cihazın kulak algılama ayarını bildirir: FF açık, FD kapalı.</summary>
    public static byte[] RequestNotifications(bool earDetection) =>
        new byte[] { 0x04, 0x00, 0x04, 0x00, 0x0F, 0x00, 0xFF, 0xFF, earDetection ? (byte)0xFF : (byte)0xFD, 0xFF };

    public static byte[] Setting(byte id, byte value, byte extra = 0x00) =>
        new byte[] { 0x04, 0x00, 0x04, 0x00, 0x09, 0x00, id, value, extra, 0x00, 0x00 };

    public static byte[] SetListeningMode(ListeningMode mode) => Setting(ListeningModeId, (byte)mode);

    /// <summary>Paketin işlem kodu; AAP başlığı taşımıyorsa -1.</summary>
    public static int Opcode(ReadOnlySpan<byte> p) =>
        p.Length >= 6 && BinaryPrimitives.ReadUInt32LittleEndian(p) == Header ? p[4] | (p[5] << 8) : -1;

    public static bool TryListeningMode(ReadOnlySpan<byte> p, out ListeningMode mode)
    {
        mode = default;
        if (Opcode(p) != 0x09 || p.Length < 8 || p[6] != ListeningModeId || p[7] is < 1 or > 4) return false;
        mode = (ListeningMode)p[7];
        return true;
    }

    /// <summary>Pil bildirimi (0x04): sayı, ardından beşer baytlık kayıtlar (bileşen, 01, düzey, durum, 01).</summary>
    public static bool TryBattery(ReadOnlySpan<byte> p, out List<BatteryEntry> entries)
    {
        entries = new List<BatteryEntry>();
        if (Opcode(p) != 0x04 || p.Length < 7) return false;
        int count = p[6];
        for (int i = 0; i < count && 7 + (i * 5) + 5 <= p.Length; i++)
        {
            ReadOnlySpan<byte> e = p.Slice(7 + (i * 5), 5);
            entries.Add(new BatteryEntry(e[0], e[2], e[3]));
        }
        return entries.Count > 0;
    }

    /// <summary>Kulak durumu (0x06): birincil ve ikincil kulaklık. 00 kulakta, 01 dışarıda, 02 kutuda.</summary>
    public static bool TryEar(ReadOnlySpan<byte> p, out byte primary, out byte secondary)
    {
        primary = secondary = 0xFF;
        if (Opcode(p) != 0x06 || p.Length < 8) return false;
        primary = p[6];
        secondary = p[7];
        return true;
    }

    public static string ModeName(ListeningMode mode) => mode switch
    {
        ListeningMode.Off => Loc.T("mode_off"),
        ListeningMode.NoiseCancellation => Loc.T("mode_nc"),
        ListeningMode.Transparency => Loc.T("mode_transparency"),
        ListeningMode.Adaptive => Loc.T("mode_adaptive"),
        _ => Loc.T("mode_unknown", (byte)mode),
    };

    public static string EarName(byte state) => state switch
    {
        0x00 => "kulakta",
        0x01 => "dışarıda",
        0x02 => "kutuda",
        0x03 => "bağlı değil",
        0x04 => "geçişte",
        _ => $"0x{state:X2}",
    };

    private static string ComponentName(byte component) => component switch
    {
        HeadsetComponent => "kulaklık",
        RightComponent => "sağ",
        LeftComponent => "sol",
        CaseComponent => "kutu",
        _ => $"0x{component:X2}",
    };

    /// <summary>
    /// Gelen paketin kısa açıklaması. Bilinmeyen paketlerin içeriği yazılmaz, yalnızca işlem kodu ve uzunluğu
    /// (aygıt bilgisi paketi seri numarası taşır).
    /// </summary>
    public static string Describe(ReadOnlySpan<byte> p)
    {
        int opcode = Opcode(p);
        if (opcode < 0) return $"başlıksız paket ({p.Length} bayt)";
        if (TryBattery(p, out List<BatteryEntry> battery))
        {
            return "pil: " + string.Join(", ", battery.Select(b =>
                $"{ComponentName(b.Component)} {(b.Present ? $"%{b.Level}" : "yok")}{(b.Present && b.Charging ? " (şarjda)" : "")}"));
        }
        if (TryEar(p, out byte primary, out byte secondary)) return $"kulak: birincil {EarName(primary)}, ikincil {EarName(secondary)}";
        if (TryListeningMode(p, out ListeningMode mode)) return $"dinleme modu: {ModeName(mode)}";
        return opcode switch
        {
            0x09 when p.Length >= 8 => $"ayar 0x{p[6]:X2} = 0x{p[7]:X2}",
            0x08 when p.Length >= 7 => $"birincil kulaklık: {(p[6] == 0x01 ? "sol" : p[6] == 0x02 ? "sağ" : $"0x{p[6]:X2}")}",
            0x1D => $"aygıt bilgisi ({p.Length} bayt)",
            0x4B when p.Length >= 10 => $"konuşma farkındalığı düzeyi {p[9]}",
            _ => $"işlem 0x{opcode:X2} ({p.Length} bayt)",
        };
    }
}
