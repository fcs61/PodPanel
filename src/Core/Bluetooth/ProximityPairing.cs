namespace PodPanel.Core.Bluetooth;

/// <summary>AirPods'un BLE "Proximity Pairing" yayınından çözülen durum.</summary>
public sealed record PodAdvert(
    DateTime Time,
    short Rssi,
    int Model,
    byte Status,
    int? Left,
    int? Right,
    int? Case,
    bool LeftCharging,
    bool RightCharging,
    bool CaseCharging,
    bool LeftInEar,
    bool RightInEar,
    bool BothInCase,
    byte Color,
    byte ConnectionState)
{
    /// <summary>Zaman ve sinyal gücü dışındaki içerik aynı mı?</summary>
    public bool SameContent(PodAdvert other) =>
        Model == other.Model && Status == other.Status && Left == other.Left && Right == other.Right && Case == other.Case
        && LeftCharging == other.LeftCharging && RightCharging == other.RightCharging && CaseCharging == other.CaseCharging
        && ConnectionState == other.ConnectionState;
}

/// <summary>
/// Apple üretici verisi (şirket 0x004C), tür 0x07 çözücüsü.
/// Düzen: 07 19 01 [model 2] [durum] [pil] [şarj|kutu] [kapak] [renk] [bağlantı durumu] [16 bayt şifreli].
/// </summary>
public static class ProximityPairing
{
    public const ushort AppleCompanyId = 0x004C;

    public static PodAdvert? TryParse(byte[] d, short rssi, DateTime time)
    {
        if (d.Length < 11 || d[0] != 0x07 || d[1] != 0x19 || d[2] != 0x01) return null;

        int model = d[4] << 8 | d[3];
        byte status = d[5];
        bool primaryLeft = (status & 0x20) != 0;
        bool thisPodInCase = (status & 0x40) != 0;
        bool flipped = !primaryLeft;

        int high = d[6] >> 4, low = d[6] & 0x0F;
        int? left = Percent(flipped ? high : low);
        int? right = Percent(flipped ? low : high);
        int? caseLevel = Percent(d[7] & 0x0F);

        bool charge4 = (d[7] & 0x10) != 0, charge5 = (d[7] & 0x20) != 0;
        bool leftCharging = flipped ? charge5 : charge4;
        bool rightCharging = flipped ? charge4 : charge5;
        bool caseCharging = (d[7] & 0x40) != 0;

        // Yayını yapan kulaklık kutudaysa kulakta bitlerinin anlamı yer değiştirir.
        bool swap = flipped ^ thisPodInCase;
        bool bit1 = (status & 0x02) != 0, bit3 = (status & 0x08) != 0;
        bool leftInEar = swap ? bit3 : bit1;
        bool rightInEar = swap ? bit1 : bit3;
        bool bothInCase = (status & 0x04) != 0;

        return new PodAdvert(time, rssi, model, status, left, right, caseLevel,
            leftCharging, rightCharging, caseCharging, leftInEar, rightInEar, bothInCase, d[9], d[10]);
    }

    /// <summary>Pil dilimi: 0–10 → %0–%100, diğer değerler bilinmiyor.</summary>
    private static int? Percent(int nibble) => nibble <= 10 ? nibble * 10 : null;

    public static string ModelName(int productId) => productId switch
    {
        0x2002 => "AirPods (1. nesil)",
        0x200F => "AirPods (2. nesil)",
        0x2013 => "AirPods (3. nesil)",
        0x2019 => "AirPods 4",
        0x201B => "AirPods 4 (ANC)",
        0x200E => "AirPods Pro",
        0x2014 => "AirPods Pro 2",
        0x2024 => "AirPods Pro 2 (USB-C)",
        0x2027 => "AirPods Pro 3",
        0x200A => "AirPods Max",
        0x201F => "AirPods Max (USB-C)",
        _ => $"Apple kulaklık (0x{productId:X4})",
    };

    public static string ActivityName(byte connectionState) => connectionState switch
    {
        0x04 => "Boşta",
        0x05 => "Ses çalıyor",
        0x06 => "Arama",
        0x07 => "Çağrı geliyor",
        0x09 => "Arama bitiyor",
        _ => "",
    };
}
