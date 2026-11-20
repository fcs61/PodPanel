using Windows.Media.Audio;

namespace PodPanel.Core.Spatial;

/// <summary>Windows'un uzamsal ses (Dolby Atmos, Windows Sonic) ayarını uç nokta bazında okur ve değiştirir.</summary>
public static class SpatialAudio
{
    private const string RenderInterfaceClass = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    private static string InterfacePath(string endpointId) => @"\\?\SWD#MMDEVAPI#" + endpointId + "#" + RenderInterfaceClass;

    /// <summary>
    /// Atmos: bu uç noktada Dolby Atmos for Headphones kullanılabiliyor (Dolby Access kurulu); açarken o seçilir.
    /// </summary>
    public static (bool Supported, string ActiveFormat, bool Atmos) Get(string endpointId)
    {
        var configuration = SpatialAudioDeviceConfiguration.GetForDeviceId(InterfacePath(endpointId));
        return (configuration.IsSpatialAudioSupported, configuration.ActiveSpatialAudioFormat ?? "",
            configuration.IsSpatialAudioFormatSupported(SpatialAudioFormatSubtype.DolbyAtmosForHeadphones));
    }

    /// <summary>
    /// Uzamsal sesi açar ya da kapatır; sonucu metin olarak döndürür. Açarken Dolby Atmos for Headphones
    /// kullanılabiliyorsa o seçilir; yoksa ya da Windows reddederse (ör. lisans süresi dolmuş) Windows Sonic.
    /// </summary>
    public static string Set(string endpointId, bool on)
    {
        var configuration = SpatialAudioDeviceConfiguration.GetForDeviceId(InterfacePath(endpointId));
        if (!on) return Apply(configuration, Guid.Empty.ToString("B")).ToString();

        string atmos = SpatialAudioFormatSubtype.DolbyAtmosForHeadphones;
        if (configuration.IsSpatialAudioFormatSupported(atmos))
        {
            var status = Apply(configuration, atmos);
            if (status == SetDefaultSpatialAudioFormatStatus.Succeeded) return "Dolby Atmos: " + status;
            var fallback = Apply(configuration, SpatialAudioFormatSubtype.WindowsSonic);
            return $"Dolby Atmos: {status}; Windows Sonic: {fallback}";
        }
        return "Windows Sonic: " + Apply(configuration, SpatialAudioFormatSubtype.WindowsSonic);
    }

    private static SetDefaultSpatialAudioFormatStatus Apply(SpatialAudioDeviceConfiguration configuration, string format) =>
        configuration.SetDefaultSpatialAudioFormatAsync(format).AsTask().GetAwaiter().GetResult().Status;

    /// <summary>Tanı için ham değerler.</summary>
    public static string Describe(string endpointId)
    {
        var configuration = SpatialAudioDeviceConfiguration.GetForDeviceId(InterfacePath(endpointId));
        return $"destek={configuration.IsSpatialAudioSupported} etkin='{configuration.ActiveSpatialAudioFormat}' " +
               $"varsayılan='{configuration.DefaultSpatialAudioFormat}' sonic='{SpatialAudioFormatSubtype.WindowsSonic}' " +
               $"sonicDestek={configuration.IsSpatialAudioFormatSupported(SpatialAudioFormatSubtype.WindowsSonic)} " +
               $"atmosDestek={configuration.IsSpatialAudioFormatSupported(SpatialAudioFormatSubtype.DolbyAtmosForHeadphones)} " +
               $"dtsDestek={configuration.IsSpatialAudioFormatSupported(SpatialAudioFormatSubtype.DTSHeadphoneX)}";
    }

    /// <summary>Windows "kapalı" durumunu boş dizgi yerine sıfır GUID ile bildirir.</summary>
    public static bool IsOn(string subtype) => Guid.TryParse(subtype, out var id) && id != Guid.Empty;

    public static string FormatName(string subtype)
    {
        if (!IsOn(subtype)) return "Kapalı";
        if (Same(subtype, SpatialAudioFormatSubtype.WindowsSonic)) return "Windows Sonic";
        if (Same(subtype, SpatialAudioFormatSubtype.DolbyAtmosForHeadphones)) return "Dolby Atmos";
        if (Same(subtype, SpatialAudioFormatSubtype.DTSHeadphoneX)) return "DTS Headphone:X";
        return "Açık";
    }

    private static bool Same(string a, string b) =>
        Guid.TryParse(a, out var left) && Guid.TryParse(b, out var right) && left == right;
}
