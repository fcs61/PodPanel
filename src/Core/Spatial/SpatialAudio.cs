using Windows.Media.Audio;

namespace PodPanel.Core.Spatial;

/// <summary>Windows'un uzamsal ses (Windows Sonic vb.) ayarını uç nokta bazında okur ve değiştirir.</summary>
public static class SpatialAudio
{
    private const string RenderInterfaceClass = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    private static string InterfacePath(string endpointId) => @"\\?\SWD#MMDEVAPI#" + endpointId + "#" + RenderInterfaceClass;

    public static (bool Supported, string ActiveFormat) Get(string endpointId)
    {
        var configuration = SpatialAudioDeviceConfiguration.GetForDeviceId(InterfacePath(endpointId));
        return (configuration.IsSpatialAudioSupported, configuration.ActiveSpatialAudioFormat ?? "");
    }

    /// <summary>Windows Sonic'i açar ya da uzamsal sesi kapatır; sonucu metin olarak döndürür.</summary>
    public static string Set(string endpointId, bool on)
    {
        var configuration = SpatialAudioDeviceConfiguration.GetForDeviceId(InterfacePath(endpointId));
        string format = on ? SpatialAudioFormatSubtype.WindowsSonic : Guid.Empty.ToString("B");
        var result = configuration.SetDefaultSpatialAudioFormatAsync(format).AsTask().GetAwaiter().GetResult();
        return result.Status.ToString();
    }

    /// <summary>Tanı için ham değerler.</summary>
    public static string Describe(string endpointId)
    {
        var configuration = SpatialAudioDeviceConfiguration.GetForDeviceId(InterfacePath(endpointId));
        return $"destek={configuration.IsSpatialAudioSupported} etkin='{configuration.ActiveSpatialAudioFormat}' " +
               $"varsayılan='{configuration.DefaultSpatialAudioFormat}' sonic='{SpatialAudioFormatSubtype.WindowsSonic}' " +
               $"sonicDestek={configuration.IsSpatialAudioFormatSupported(SpatialAudioFormatSubtype.WindowsSonic)}";
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
