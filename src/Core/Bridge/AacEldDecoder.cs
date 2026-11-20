using System.Runtime.InteropServices;

namespace PodPanel.Core.Bridge;

/// <summary>
/// FFmpeg kitaplıklarının (avutil, swresample, avcodec) çalışma zamanında yüklenen işlevleri. Kitaplıklar uygulamanın
/// yanındaki "ffmpeg" klasöründen yüklenir; adlarındaki sürüm numarası (avcodec-62 gibi) önemli değildir.
/// </summary>
internal sealed unsafe class FfmpegLibrary
{
    private static readonly object Gate = new();
    private static FfmpegLibrary? _instance;
    private static string? _error;

    public readonly delegate* unmanaged[Cdecl]<int, void> av_log_set_level;
    public readonly delegate* unmanaged[Cdecl]<nuint, void*> av_mallocz;
    public readonly delegate* unmanaged[Cdecl]<byte*> av_frame_alloc;
    public readonly delegate* unmanaged[Cdecl]<byte**, void> av_frame_free;
    public readonly delegate* unmanaged[Cdecl]<byte*, int, void> av_channel_layout_default;
    public readonly delegate* unmanaged[Cdecl]<void*, byte*, long, int, int> av_opt_set_int;
    public readonly delegate* unmanaged[Cdecl]<void*, byte*, int, long*, int> av_opt_get_int;

    public readonly delegate* unmanaged[Cdecl]<uint> avcodec_version;
    public readonly delegate* unmanaged[Cdecl]<byte*, void*> avcodec_find_decoder_by_name;
    public readonly delegate* unmanaged[Cdecl]<void*, byte*> avcodec_alloc_context3;
    public readonly delegate* unmanaged[Cdecl]<byte**, void> avcodec_free_context;
    public readonly delegate* unmanaged[Cdecl]<byte*> avcodec_parameters_alloc;
    public readonly delegate* unmanaged[Cdecl]<byte**, void> avcodec_parameters_free;
    public readonly delegate* unmanaged[Cdecl]<byte*, byte*, int> avcodec_parameters_from_context;
    public readonly delegate* unmanaged[Cdecl]<byte*, byte*, int> avcodec_parameters_to_context;
    public readonly delegate* unmanaged[Cdecl]<byte*, void*, void**, int> avcodec_open2;
    public readonly delegate* unmanaged[Cdecl]<byte*, void> avcodec_flush_buffers;
    public readonly delegate* unmanaged[Cdecl]<byte*, byte*, int> avcodec_send_packet;
    public readonly delegate* unmanaged[Cdecl]<byte*, byte*, int> avcodec_receive_frame;
    public readonly delegate* unmanaged[Cdecl]<byte*> av_packet_alloc;
    public readonly delegate* unmanaged[Cdecl]<byte**, void> av_packet_free;
    public readonly delegate* unmanaged[Cdecl]<byte*, int, int> av_new_packet;
    public readonly delegate* unmanaged[Cdecl]<byte*, void> av_packet_unref;

    public readonly delegate* unmanaged[Cdecl]<void**, byte*, int, int, byte*, int, int, int, void*, int> swr_alloc_set_opts2;
    public readonly delegate* unmanaged[Cdecl]<void*, int> swr_init;
    public readonly delegate* unmanaged[Cdecl]<void*, byte**, int, byte**, int, int> swr_convert;
    public readonly delegate* unmanaged[Cdecl]<void**, void> swr_free;

    public string Directory { get; }
    public string Version { get; }

    private FfmpegLibrary(string directory)
    {
        Directory = directory;
        // Bağımlılık sırası: avutil, swresample, avcodec.
        nint util = Load(directory, "avutil-*.dll");
        nint resample = Load(directory, "swresample-*.dll");
        nint codec = Load(directory, "avcodec-*.dll");

        av_log_set_level = (delegate* unmanaged[Cdecl]<int, void>)NativeLibrary.GetExport(util, "av_log_set_level");
        av_mallocz = (delegate* unmanaged[Cdecl]<nuint, void*>)NativeLibrary.GetExport(util, "av_mallocz");
        av_frame_alloc = (delegate* unmanaged[Cdecl]<byte*>)NativeLibrary.GetExport(util, "av_frame_alloc");
        av_frame_free = (delegate* unmanaged[Cdecl]<byte**, void>)NativeLibrary.GetExport(util, "av_frame_free");
        av_channel_layout_default = (delegate* unmanaged[Cdecl]<byte*, int, void>)NativeLibrary.GetExport(util, "av_channel_layout_default");
        av_opt_set_int = (delegate* unmanaged[Cdecl]<void*, byte*, long, int, int>)NativeLibrary.GetExport(util, "av_opt_set_int");
        av_opt_get_int = (delegate* unmanaged[Cdecl]<void*, byte*, int, long*, int>)NativeLibrary.GetExport(util, "av_opt_get_int");

        avcodec_version = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(codec, "avcodec_version");
        avcodec_find_decoder_by_name = (delegate* unmanaged[Cdecl]<byte*, void*>)NativeLibrary.GetExport(codec, "avcodec_find_decoder_by_name");
        avcodec_alloc_context3 = (delegate* unmanaged[Cdecl]<void*, byte*>)NativeLibrary.GetExport(codec, "avcodec_alloc_context3");
        avcodec_free_context = (delegate* unmanaged[Cdecl]<byte**, void>)NativeLibrary.GetExport(codec, "avcodec_free_context");
        avcodec_parameters_alloc = (delegate* unmanaged[Cdecl]<byte*>)NativeLibrary.GetExport(codec, "avcodec_parameters_alloc");
        avcodec_parameters_free = (delegate* unmanaged[Cdecl]<byte**, void>)NativeLibrary.GetExport(codec, "avcodec_parameters_free");
        avcodec_parameters_from_context = (delegate* unmanaged[Cdecl]<byte*, byte*, int>)NativeLibrary.GetExport(codec, "avcodec_parameters_from_context");
        avcodec_parameters_to_context = (delegate* unmanaged[Cdecl]<byte*, byte*, int>)NativeLibrary.GetExport(codec, "avcodec_parameters_to_context");
        avcodec_open2 = (delegate* unmanaged[Cdecl]<byte*, void*, void**, int>)NativeLibrary.GetExport(codec, "avcodec_open2");
        avcodec_flush_buffers = (delegate* unmanaged[Cdecl]<byte*, void>)NativeLibrary.GetExport(codec, "avcodec_flush_buffers");
        avcodec_send_packet = (delegate* unmanaged[Cdecl]<byte*, byte*, int>)NativeLibrary.GetExport(codec, "avcodec_send_packet");
        avcodec_receive_frame = (delegate* unmanaged[Cdecl]<byte*, byte*, int>)NativeLibrary.GetExport(codec, "avcodec_receive_frame");
        av_packet_alloc = (delegate* unmanaged[Cdecl]<byte*>)NativeLibrary.GetExport(codec, "av_packet_alloc");
        av_packet_free = (delegate* unmanaged[Cdecl]<byte**, void>)NativeLibrary.GetExport(codec, "av_packet_free");
        av_new_packet = (delegate* unmanaged[Cdecl]<byte*, int, int>)NativeLibrary.GetExport(codec, "av_new_packet");
        av_packet_unref = (delegate* unmanaged[Cdecl]<byte*, void>)NativeLibrary.GetExport(codec, "av_packet_unref");

        swr_alloc_set_opts2 = (delegate* unmanaged[Cdecl]<void**, byte*, int, int, byte*, int, int, int, void*, int>)NativeLibrary.GetExport(resample, "swr_alloc_set_opts2");
        swr_init = (delegate* unmanaged[Cdecl]<void*, int>)NativeLibrary.GetExport(resample, "swr_init");
        swr_convert = (delegate* unmanaged[Cdecl]<void*, byte**, int, byte**, int, int>)NativeLibrary.GetExport(resample, "swr_convert");
        swr_free = (delegate* unmanaged[Cdecl]<void**, void>)NativeLibrary.GetExport(resample, "swr_free");

        uint version = avcodec_version();
        Version = $"avcodec {version >> 16}.{(version >> 8) & 0xFF}.{version & 0xFF}";
        av_log_set_level(16); // AV_LOG_ERROR
    }

    private static nint Load(string directory, string pattern)
    {
        string? path = System.IO.Directory.GetFiles(directory, pattern).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (path == null) throw new DllNotFoundException($"{pattern} bulunamadı: {directory}");
        return NativeLibrary.Load(path);
    }

    /// <summary>Kitaplıkların arandığı klasörler: PODPANEL_FFMPEG değişkeni, uygulamanın yanındaki "ffmpeg" klasörü.</summary>
    public static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable("PODPANEL_FFMPEG") is { Length: > 0 } custom) yield return custom;
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PodPanel", "app", "ffmpeg");
    }

    /// <summary>Kitaplıkları bir kez yükler; bulunamaz ya da yüklenemezse null döner ve nedeni <paramref name="error"/> taşır.</summary>
    public static FfmpegLibrary? Get(out string error)
    {
        lock (Gate)
        {
            if (_instance == null && _error == null)
            {
                string? directory = Candidates().FirstOrDefault(d => System.IO.Directory.Exists(d) && System.IO.Directory.GetFiles(d, "avcodec-*.dll").Length > 0);
                if (directory == null)
                {
                    _error = Loc.T("ffmpeg_missing");
                }
                else
                {
                    try
                    {
                        _instance = new FfmpegLibrary(directory);
                    }
                    catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or IOException)
                    {
                        _error = Loc.T("ffmpeg_failed", e.Message);
                    }
                }
            }
            error = _error ?? "";
            return _instance;
        }
    }
}

/// <summary>
/// AirPods mikrofon akışının çözücüsü: AAC-ELD erişim birimlerinden 48 kHz mono kayan noktalı ses (-1..1) üretir.
/// İş parçacığı güvenli değildir; tek bir çağıran kullanır.
/// </summary>
public sealed unsafe class AacEldDecoder : IDisposable
{
    public const int OutputRate = 48_000;
    /// <summary>Bir erişim biriminden çıkabilecek en çok örnek için güvenli arabellek boyu.</summary>
    public const int MaxSamplesPerUnit = 1024;

    // Akışın yapılandırması 48 kHz bildirir, ama kulaklık 480 örneklik çerçeveleri saniyede 133,3 kez gönderir
    // (çerçeve başına 7,5 ms): çözülen ses 64 kHz'dir ve öyle yeniden örneklenir.
    private const int StreamRate = 64_000;
    private const int MaxUnitLength = byte.MaxValue;
    private const int PaddingSize = 64; // AV_INPUT_BUFFER_PADDING_SIZE
    private const int ErrorAgain = -11; // AVERROR(EAGAIN)
    private const int CodecIdAac = 0x15002;
    private const int MediaTypeAudio = 1;
    private const int SampleFormatFloat = 3; // AV_SAMPLE_FMT_FLT

    // Yapı alanlarının adresleri. FFmpeg sürümleri arasında değişmeyen baş kısımlar kullanılır ve her biri
    // açılışta sınanır (aşağıda); AVCodecContext'in sürüme göre değişen alanlarına dokunulmaz.
    private const int ParametersCodecType = 0;
    private const int ParametersCodecId = 4;
    private const int ParametersExtradata = 16;
    private const int ParametersExtradataSize = 24;
    private const int PacketData = 24;
    private const int PacketSize = 32;
    private const int FrameExtendedData = 96;
    private const int FrameSamples = 112;
    private const int FrameFormat = 116;

    // ER AAC-ELD (nesne türü 39), 48 kHz dizini, mono, 480 örneklik çerçeve.
    private static readonly byte[] AudioSpecificConfig = { 0xF8, 0xE6, 0x30, 0x00 };

    private readonly FfmpegLibrary _ff;
    private readonly byte* _unit;
    private byte* _context;
    private byte* _packet;
    private byte* _frame;
    private void* _resampler;
    private bool _disposed;

    public string LibraryVersion => _ff.Version;

    /// <summary>Çözücüyü açar; FFmpeg yoksa ya da akış yapılandırmasını kabul etmezse nedeniyle birlikte hata fırlatır.</summary>
    public AacEldDecoder()
    {
        _ff = FfmpegLibrary.Get(out string error) ?? throw new InvalidOperationException(error);
        _unit = (byte*)NativeMemory.AllocZeroed(MaxUnitLength + PaddingSize);
        try
        {
            void* codec;
            fixed (byte* name = "aac\0"u8)
            {
                codec = _ff.avcodec_find_decoder_by_name(name);
            }
            if (codec == null || (_context = _ff.avcodec_alloc_context3(codec)) == null)
            {
                throw new InvalidOperationException("FFmpeg'in AAC çözücüsü yok.");
            }

            // Yapılandırma, sürümler arasında yerleşimi değişmeyen AVCodecParameters üzerinden verilir.
            byte* parameters = _ff.avcodec_parameters_alloc();
            try
            {
                if (parameters == null || _ff.avcodec_parameters_from_context(parameters, _context) < 0
                    || *(int*)(parameters + ParametersCodecType) != MediaTypeAudio || *(int*)(parameters + ParametersCodecId) != CodecIdAac
                    || *(nint*)(parameters + ParametersExtradata) != 0)
                {
                    throw new InvalidOperationException($"Bu FFmpeg sürümünün yapı yerleşimi beklenenden farklı ({_ff.Version}).");
                }
                byte* extradata = (byte*)_ff.av_mallocz((nuint)(AudioSpecificConfig.Length + PaddingSize)); // parameters ile birlikte serbest kalır
                if (extradata == null) throw new InvalidOperationException("FFmpeg bellek ayıramadı.");
                AudioSpecificConfig.CopyTo(new Span<byte>(extradata, AudioSpecificConfig.Length));
                *(byte**)(parameters + ParametersExtradata) = extradata;
                *(int*)(parameters + ParametersExtradataSize) = AudioSpecificConfig.Length;
                if (_ff.avcodec_parameters_to_context(_context, parameters) < 0)
                {
                    throw new InvalidOperationException("FFmpeg çözücü yapılandırmasını kabul etmedi.");
                }
            }
            finally
            {
                _ff.avcodec_parameters_free(&parameters);
            }

            // ELD için örnekleme hızı, yapılandırmaya rağmen açılıştan önce verilmeli.
            long rate = 0;
            fixed (byte* option = "ar\0"u8)
            {
                if (_ff.av_opt_set_int(_context, option, OutputRate, 0) < 0 || _ff.av_opt_get_int(_context, option, 0, &rate) < 0 || rate != OutputRate)
                {
                    throw new InvalidOperationException("FFmpeg örnekleme hızını kabul etmedi.");
                }
            }
            if (_ff.avcodec_open2(_context, codec, null) < 0)
            {
                throw new InvalidOperationException("FFmpeg AAC-ELD çözücüsünü açamadı.");
            }

            _packet = _ff.av_packet_alloc();
            // Paket yapısının veri ve boyut alanları beklenen yerde mi?
            if (_packet == null || _ff.av_new_packet(_packet, 77) < 0 || *(int*)(_packet + PacketSize) != 77 || *(nint*)(_packet + PacketData) == 0)
            {
                throw new InvalidOperationException($"Bu FFmpeg sürümünün paket yerleşimi beklenenden farklı ({_ff.Version}).");
            }
            _ff.av_packet_unref(_packet);
            if ((_frame = _ff.av_frame_alloc()) == null) throw new InvalidOperationException("FFmpeg bellek ayıramadı.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Bir erişim birimini (1..255 bayt) çözer ve yeniden örneklenmiş sesi <paramref name="output"/> başına yazar.
    /// Örnek sayısını döndürür; FFmpeg birimi reddederse -1 (çözücü kullanılabilir kalır).
    /// </summary>
    public int Decode(ReadOnlySpan<byte> unit, Span<float> output)
    {
        if (unit.Length is 0 or > MaxUnitLength) return -1;
        unit.CopyTo(new Span<byte>(_unit, MaxUnitLength));
        new Span<byte>(_unit + unit.Length, PaddingSize).Clear();
        *(byte**)(_packet + PacketData) = _unit;
        *(int*)(_packet + PacketSize) = unit.Length;
        if (_ff.avcodec_send_packet(_context, _packet) < 0) return -1;

        int total = 0;
        fixed (float* start = output)
        {
            while (true)
            {
                int result = _ff.avcodec_receive_frame(_context, _frame); // önceki çerçeveyi kendisi bırakır
                if (result == ErrorAgain) return total;
                if (result < 0) return -1;
                int samples = *(int*)(_frame + FrameSamples);
                int format = *(int*)(_frame + FrameFormat);
                if (samples is <= 0 or > 8192 || format is < 0 or > 11) return -1;
                if (_resampler == null && !CreateResampler(format)) return -1;
                byte* destination = (byte*)(start + total);
                int converted = _ff.swr_convert(_resampler, &destination, output.Length - total, *(byte***)(_frame + FrameExtendedData), samples);
                if (converted < 0) return -1;
                total += converted;
            }
        }
    }

    /// <summary>Çözücünün durumunu sıfırlar (akışta boşluk olduktan sonra).</summary>
    public void Flush() => _ff.avcodec_flush_buffers(_context);

    // İlk çözülen çerçevenin biçimiyle bir kez kurulur: mono 64 kHz -> mono 48 kHz kayan nokta.
    private bool CreateResampler(int inputFormat)
    {
        byte* mono = stackalloc byte[64]; // AVChannelLayout 24 bayt; fazlası güvenlik payı
        new Span<byte>(mono, 64).Clear();
        _ff.av_channel_layout_default(mono, 1);
        void* resampler = null;
        if (_ff.swr_alloc_set_opts2(&resampler, mono, SampleFormatFloat, OutputRate, mono, inputFormat, StreamRate, 0, null) < 0) return false;
        if (_ff.swr_init(resampler) < 0)
        {
            _ff.swr_free(&resampler);
            return false;
        }
        _resampler = resampler;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_resampler != null)
        {
            void* resampler = _resampler;
            _ff.swr_free(&resampler);
            _resampler = null;
        }
        if (_frame != null)
        {
            byte* frame = _frame;
            _ff.av_frame_free(&frame);
            _frame = null;
        }
        if (_packet != null)
        {
            byte* packet = _packet;
            _ff.av_packet_free(&packet);
            _packet = null;
        }
        if (_context != null)
        {
            byte* context = _context;
            _ff.avcodec_free_context(&context);
            _context = null;
        }
        NativeMemory.Free(_unit);
    }
}
