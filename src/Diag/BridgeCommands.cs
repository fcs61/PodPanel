using System.Globalization;
using System.IO.Pipes;
using System.Text;
using PodPanel.Core;
using PodPanel.Core.Bridge;

/// <summary>Köprü servisi ve kulaklığın kontrol kanalı için tanı komutları.</summary>
internal static class BridgeCommands
{
    public static void Run(string command, string argument, Settings settings)
    {
        switch (command)
        {
            case "bridge":
                Status(Seconds(argument, 4));
                break;
            case "aap":
                Probe(ResolveAddress(settings), Seconds(argument, 12));
                break;
            case "mode":
                SetMode(ResolveAddress(settings), argument);
                break;
            case "aaptest":
                SelfTest();
                break;
            case "micdump":
                MicDump(ResolveAddress(settings), argument);
                break;
            case "micdecode":
                MicDecode(argument);
                break;
            case "eldprobe":
                EldProbe(ResolveAddress(settings));
                break;
            case "jitter":
                Jitter(ResolveAddress(settings), argument);
                break;
            case "latency":
                LatencyMeasurement.Run(ResolveAddress(settings), argument);
                break;
            case "srclatency":
                SourceLatency.Run(ResolveAddress(settings), argument);
                break;
            case "bitrate":
                Bitrate(ResolveAddress(settings), Seconds(argument, 20));
                break;
            case "micrec":
                MediaCaptureRecord(argument, settings).GetAwaiter().GetResult();
                break;
            case "a2dp":
            {
                // a2dp: Windows'un ses profilinin durumunu yazar; a2dp on|off: açar ya da kapatır (birkaç kez dener).
                ulong address = ResolveAddress(settings);
                Guid sink = PodPanel.Core.Bluetooth.BluetoothServices.AudioSink;
                Console.WriteLine($"Windows ses profili: {Describe(PodPanel.Core.Bluetooth.BluetoothServices.IsEnabled(address, sink))}");
                if (argument is "on" or "off")
                {
                    bool want = argument == "on";
                    for (int attempt = 1; attempt <= 6; attempt++)
                    {
                        bool ok = PodPanel.Core.Bluetooth.BluetoothServices.SetEnabled(address, sink, want, out string error);
                        Thread.Sleep(2500);
                        bool? now = PodPanel.Core.Bluetooth.BluetoothServices.IsEnabled(address, sink);
                        Console.WriteLine($"deneme {attempt}: {(ok ? "istek uygulandı" : "başarısız: " + error)}; durum: {Describe(now)}");
                        if (now == want) break;
                    }
                }
                break;
            }
            case "hfp":
            {
                // hfp: durumu yazar; hfp on|off: el-serbest servisini açar ya da kapatır.
                ulong address = ResolveAddress(settings);
                Console.WriteLine($"el-serbest servisi: {Describe(PodPanel.Core.Bluetooth.HandsFree.IsEnabled(address))}");
                if (argument is "on" or "off")
                {
                    bool ok = PodPanel.Core.Bluetooth.HandsFree.SetEnabled(address, argument == "on", out string error);
                    Console.WriteLine(ok ? "istek uygulandı" : "başarısız: " + error);
                    Thread.Sleep(2500);
                    Console.WriteLine($"el-serbest servisi: {Describe(PodPanel.Core.Bluetooth.HandsFree.IsEnabled(address))}");
                }
                break;
            }
        }
    }

    private static double Seconds(string argument, double fallback) =>
        double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : fallback;

    // Kayıtlı adres yoksa kulaklığı ses uç noktalarından bulur.
    private static ulong ResolveAddress(Settings settings)
    {
        string? text = settings.LastAddress;
        if (string.IsNullOrEmpty(text))
        {
            using var monitor = new PodMonitor(settings, "-Diag");
            monitor.Start();
            for (int i = 0; i < 25 && monitor.Current.Address.Length == 0; i++) Thread.Sleep(200);
            text = monitor.Current.Address;
        }
        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address) ? address : 0;
    }

    private static string Stamp => DateTime.Now.ToString("HH:mm:ss.fff");

    private static string Describe(bool? enabled) => enabled switch { true => "açık", false => "kapalı", _ => "okunamadı" };

    /// <summary>Servisin durum kanalını dinler.</summary>
    private static void Status(double seconds)
    {
        using NamedPipeClientStream? pipe = BridgePipes.Connect(BridgePipes.ServicePipe, out int error);
        if (pipe is null)
        {
            Console.WriteLine($"Servise bağlanılamadı: {BridgePipes.DescribeError(error)} ({error})");
            return;
        }
        Console.WriteLine("Servise bağlanıldı.");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var buffer = new byte[2048];
        try
        {
            while (true)
            {
                int n = pipe.ReadAsync(buffer, stop.Token).AsTask().GetAwaiter().GetResult();
                if (n == 0) break;
                if (buffer[0] == BridgePipes.ServiceStateMessage && n >= 2)
                {
                    string state = buffer[1] switch
                    {
                        0 => "adaptör devralınıyor",
                        1 => "çalışıyor",
                        2 => "koruma devrede: adaptör Windows'un kendi sürücüsünde",
                        3 => "devralınacak adaptör yok",
                        4 => "güncelleniyor",
                        _ => $"durum {buffer[1]}",
                    };
                    string detail = Encoding.UTF8.GetString(buffer, 2, n - 2);
                    Console.WriteLine($"[{Stamp}] servis: {state}{(detail.Length > 0 ? $" ({detail})" : "")}");
                }
                else if (buffer[0] == 0x22 && n >= 3)
                {
                    string[] fields = Encoding.UTF8.GetString(buffer, 3, n - 3).Split('\0');
                    Console.WriteLine($"[{Stamp}] sürüm {fields[0]}, otomatik güncelleme {(buffer[2] & 1) != 0}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Süre doldu.
        }
    }

    private static AapClient? Open(ulong address)
    {
        if (address == 0)
        {
            Console.WriteLine("AirPods adresi bulunamadı.");
            return null;
        }
        AapClient? client = AapClient.Open(address, out int error);
        if (client is null) Console.WriteLine($"Kontrol kanalı açılamadı: {BridgePipes.DescribeError(error)} ({error})");
        return client;
    }

    /// <summary>Kanalı açar, bildirimleri ister ve gelen paketleri yazar. Kulaklığın hiçbir ayarını değiştirmez.</summary>
    private static void Probe(ulong address, double seconds)
    {
        using AapClient? client = Open(address);
        if (client is null) return;
        Console.WriteLine($"[{Stamp}] kanal açıldı: {address:X12}");
        var summary = new Dictionary<string, string>();
        client.Packet += packet =>
        {
            string text = Aap.Describe(packet);
            Console.WriteLine($"[{Stamp}] {text}");
            int split = text.IndexOf(':');
            if (split > 0 && (Aap.Opcode(packet) is 0x04 or 0x06 || Aap.TryListeningMode(packet, out _))) summary[text[..split]] = text[(split + 2)..];
        };
        client.Ended += code => Console.WriteLine($"[{Stamp}] kanal kapandı: {BridgePipes.DescribeError(code)} ({code})");
        client.CallAudio += on => Console.WriteLine($"[{Stamp}] arama sesi: {(on ? "başladı" : "bitti")}");
        bool ready = client.InitializeAsync().GetAwaiter().GetResult();
        Console.WriteLine($"[{Stamp}] açılış: {(ready ? "tamam" : "kulaklık yanıt vermedi")}");
        if (ready) Thread.Sleep((int)(seconds * 1000));
        Console.WriteLine("ÖZET : " + (summary.Count == 0 ? "(bildirim gelmedi)" : string.Join(" | ", summary.Select(p => $"{p.Key}: {p.Value}"))));
    }

    /// <summary>Dinleme modunu değiştirir ve kulaklığın onayını bekler.</summary>
    private static void SetMode(ulong address, string argument)
    {
        ListeningMode? wanted = argument.ToLowerInvariant() switch
        {
            "off" or "kapali" => ListeningMode.Off,
            "nc" or "anc" => ListeningMode.NoiseCancellation,
            "transparency" or "seffaf" => ListeningMode.Transparency,
            "adaptive" or "uyarlanabilir" => ListeningMode.Adaptive,
            _ => null,
        };
        if (wanted is not { } mode)
        {
            Console.WriteLine("Kullanım: mode off|nc|transparency|adaptive");
            return;
        }
        using AapClient? client = Open(address);
        if (client is null) return;
        ListeningMode? current = null;
        bool requested = false;
        using var changed = new AutoResetEvent(false);
        client.Packet += packet =>
        {
            if (!Aap.TryListeningMode(packet, out ListeningMode reported))
            {
                // İstekten sonra gelen diğer paketler de yazılır: kulaklığın isteğe verdiği yanıtı görmek için.
                if (requested) Console.WriteLine($"[{Stamp}]   {Aap.Describe(packet)}");
                return;
            }
            current = reported;
            Console.WriteLine($"[{Stamp}] kulaklık bildirdi: {Aap.ModeName(reported)}");
            changed.Set();
        };
        if (!client.InitializeAsync().GetAwaiter().GetResult())
        {
            Console.WriteLine("Kulaklık yanıt vermedi.");
            return;
        }
        // Açılıştan sonra kulaklık mevcut modu kendisi bildirir (iki kez). Ayar paketleri ilk durum dökümü bitmeden
        // gönderilirse yok sayılıyor; özgün uygulama da bildirim isteğinden sonra 1 sn bekliyor.
        changed.WaitOne(TimeSpan.FromSeconds(5));
        Thread.Sleep(1200);
        Console.WriteLine($"ÖNCE : {(current is { } before ? Aap.ModeName(before) : "bilinmiyor")}");
        if (current == mode)
        {
            Console.WriteLine("Kulaklık zaten bu modda.");
            return;
        }
        changed.Reset();
        requested = true;
        bool sent = client.Send(Aap.SetListeningMode(mode));
        Console.WriteLine($"[{Stamp}] istendi: {Aap.ModeName(mode)}{(sent ? "" : " (GÖNDERİLEMEDİ)")}");
        DateTime until = DateTime.UtcNow.AddSeconds(5);
        while (current != mode && DateTime.UtcNow < until) changed.WaitOne(TimeSpan.FromMilliseconds(250));
        Console.WriteLine($"SONRA: {(current is { } after ? Aap.ModeName(after) : "bilinmiyor")} " +
            (current == mode ? "(kulaklık onayladı)" : "(onay gelmedi)"));
    }

    private static readonly byte[] StartMicrophone = { 0x04, 0x00, 0x04, 0x00, 0x58, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x01, 0x82, 0x00, 0x00, 0x00, 0x04, 0x96, 0x00 };
    private static readonly byte[] StopMicrophone = { 0x04, 0x00, 0x04, 0x00, 0x58, 0x00, 0x00, 0x00, 0x02, 0x00, 0x03, 0x01 };

    /// <summary>
    /// Kulaklığın mikrofon akışını 4 saniye açar ve ham paketleri dosyaya yazar (çözücüyü geliştirmek için).
    /// Dosya biçimi: her paket için u32 milisaniye, u16 uzunluk, paket baytları.
    /// </summary>
    private static void MicDump(ulong address, string path)
    {
        if (path.Length == 0)
        {
            Console.WriteLine("Kullanım: micdump <dosya>");
            return;
        }
        using AapClient? client = Open(address);
        if (client is null) return;
        var packets = new List<(int Ms, byte[] Data)>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool streaming = false;
        client.Packet += packet =>
        {
            if (Aap.Opcode(packet) != 0x58) return;
            lock (packets)
            {
                if (streaming) packets.Add(((int)clock.ElapsedMilliseconds, packet));
            }
        };
        if (!client.InitializeAsync().GetAwaiter().GetResult())
        {
            Console.WriteLine("Kulaklık yanıt vermedi.");
            return;
        }
        Thread.Sleep(1200);
        lock (packets) streaming = true;
        clock.Restart();
        client.Send(StartMicrophone);
        Thread.Sleep(4000);
        client.Send(StopMicrophone);
        Thread.Sleep(300);
        lock (packets) streaming = false;

        int units = 0, bytes = 0, audioPackets = 0;
        var sizes = new SortedDictionary<int, int>();
        using (var file = new BinaryWriter(File.Create(path)))
        {
            foreach ((int ms, byte[] data) in packets)
            {
                file.Write((uint)ms);
                file.Write((ushort)data.Length);
                file.Write(data);
                if (data.Length < 22 || data[6] != 0x01 || data[7] != 0x00) continue;
                audioPackets++;
                // Kayıtlar: u32 zaman damgası, u8 uzunluk, erişim birimi.
                for (int offset = 22; data.Length - offset >= 5;)
                {
                    int length = data[offset + 4];
                    if (length == 0 || offset + 5 + length > data.Length) break;
                    units++;
                    bytes += length;
                    sizes[length / 16 * 16] = sizes.GetValueOrDefault(length / 16 * 16) + 1;
                    offset += 5 + length;
                }
            }
        }
        double seconds = packets.Count > 1 ? (packets[^1].Ms - packets[0].Ms) / 1000.0 : 0;
        Console.WriteLine($"paket: {packets.Count} (ses taşıyan {audioPackets}), süre {seconds:0.00} sn, ilk paket {(packets.Count > 0 ? packets[0].Ms : -1)} ms");
        Console.WriteLine($"erişim birimi: {units} ({(seconds > 0 ? units / seconds : 0):0.0}/sn), ortalama {(units > 0 ? bytes / units : 0)} bayt, {(seconds > 0 ? bytes * 8 / seconds / 1000 : 0):0.0} kbps");
        Console.WriteLine("boyut dağılımı (16'lık dilimler): " + string.Join(", ", sizes.Select(p => $"{p.Key}+:{p.Value}")));
        foreach ((int ms, byte[] data) in packets.Take(3))
        {
            Console.WriteLine($"  {ms} ms, {data.Length} bayt, başlık: {Convert.ToHexString(data.AsSpan(0, Math.Min(22, data.Length)))}");
        }
    }

    /// <summary>
    /// Windows Ses Kaydedici'nin kullandığı yolla (MediaCapture, M4A) varsayılan mikrofondan kayıt alır.
    /// Kullanım: micrec "saniye;dosya.m4a". Geçen gerçek süreyi yazar; dosyanın süresi ayrıca ölçülür.
    /// </summary>
    private static async Task MediaCaptureRecord(string argument, Settings settings)
    {
        string[] parts = argument.Split(';', 2);
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            Console.WriteLine("Kullanım: micrec \"saniye;dosya.m4a\"");
            return;
        }
        string path = Path.GetFullPath(parts[1]);
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path));
        var file = await folder.CreateFileAsync(Path.GetFileName(path), Windows.Storage.CreationCollisionOption.ReplaceExisting);
        using var capture = new Windows.Media.Capture.MediaCapture();
        capture.Failed += (_, e) => Console.WriteLine($"[{Stamp}] MediaCapture hatası: 0x{e.Code:X8} {e.Message}");
        capture.RecordLimitationExceeded += _ => Console.WriteLine($"[{Stamp}] kayıt sınırı aşıldı");
        await capture.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Audio,
            MediaCategory = Windows.Media.Capture.MediaCategory.Other,
        });
        var properties = capture.AudioDeviceController.GetMediaStreamProperties(Windows.Media.Capture.MediaStreamType.Audio) as Windows.Media.MediaProperties.AudioEncodingProperties;
        Console.WriteLine($"[{Stamp}] aygıt biçimi: {properties?.Subtype} {properties?.SampleRate} Hz, {properties?.ChannelCount} kanal, {properties?.BitsPerSample} bit");
        var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateM4a(Windows.Media.MediaProperties.AudioEncodingQuality.Auto);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await capture.StartRecordToStorageFileAsync(profile, file);
        Console.WriteLine($"[{Stamp}] kayıt başladı ({clock.ElapsedMilliseconds} ms)");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await capture.StopRecordAsync();
        Console.WriteLine($"[{Stamp}] kayıt durdu; gerçek süre {clock.Elapsed.TotalSeconds:0.00} sn, dosya {new FileInfo(path).Length / 1024} KB");
    }

    /// <summary>
    /// Kulaklığın gecikme tamponu komutunu dener: kontrol komutu 0x0B, değer baytı milisaniye (USBPods kaynağındaki
    /// tanıma göre; paket biçimi diğer kontrol komutlarıyla aynı varsayılmıştır). Verilen değerleri sırayla gönderir
    /// ve her birinden sonra kulaklığın yanıtını ve Windows'a bildirdiği gecikmeyi (sürücü telemetrisinden) yazar.
    /// Kullanım: jitter 90,60. Panel kapalı olmalıdır.
    /// </summary>
    private static void Jitter(ulong address, string argument)
    {
        var values = argument.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => byte.TryParse(v.Trim(), out byte b) ? (int)b : -1).Where(v => v >= 0).ToList();
        if (values.Count == 0)
        {
            Console.WriteLine("Kullanım: jitter 90,60");
            return;
        }
        using var telemetry = new PodPanel.Core.Telemetry.BtAudioTelemetry();
        telemetry.RawEvent += e =>
        {
            if (e.Name.Contains("Delay", StringComparison.OrdinalIgnoreCase)) Console.WriteLine($"[{Stamp}]   Windows: bildirilen gecikme {e.Number("A2dpRemoteDelayReportDurationInMs", -1)} ms");
        };
        telemetry.Start("-Diag");
        using AapClient? client = Open(address);
        if (client is null) return;
        bool listen = false;
        client.Packet += packet =>
        {
            // Kulaklığın komuta verdiği yanıtlar: 0x0B içeren ayar bildirimleri ve dinleme sırasında gelen diğer paketler.
            if (!listen) return;
            int opcode = Aap.Opcode(packet);
            string detail = opcode == 0x09 && packet.Length >= 8 ? $"ayar 0x{packet[6]:X2} = {Convert.ToHexString(packet.AsSpan(7, Math.Min(4, packet.Length - 7)))}" : Aap.Describe(packet);
            Console.WriteLine($"[{Stamp}]   kulaklık: {detail}");
        };
        if (!client.InitializeAsync().GetAwaiter().GetResult())
        {
            Console.WriteLine("Kulaklık yanıt vermedi.");
            return;
        }
        Thread.Sleep(1500);
        listen = true;
        foreach (int value in values)
        {
            Console.WriteLine($"[{Stamp}] gönderiliyor: kontrol 0x0B = {value}");
            client.Send(Aap.Setting(0x0B, (byte)value));
            Thread.Sleep(4000);
        }
        Console.WriteLine($"[{Stamp}] bitti");
    }

    /// <summary>
    /// Kulaklığın her ses ucunu (AAC, SBC, Apple AAC-ELD) sırayla yapılandırır ve o uç için bildirdiği gecikmeyi
    /// okur; ses göndermez. Bunun için Windows'un kendi ses profili (A2DP) kısa süreliğine kapatılır, çünkü kulaklık
    /// aynı anda tek bir işaretleşme kanalı kabul eder; sonunda geri açılır. Panel kapalı olmalıdır (kanal tektir).
    /// </summary>
    private static void EldProbe(ulong address)
    {
        if (address == 0)
        {
            Console.WriteLine("AirPods adresi bulunamadı.");
            return;
        }
        bool? sink = PodPanel.Core.Bluetooth.BluetoothServices.IsEnabled(address, PodPanel.Core.Bluetooth.BluetoothServices.AudioSink);
        Console.WriteLine($"[{Stamp}] Windows ses profili: {Describe(sink)}, el-serbest: {Describe(PodPanel.Core.Bluetooth.HandsFree.IsEnabled(address))}");
        if (sink != true)
        {
            Console.WriteLine("Windows ses profili açık değil ya da okunamadı; yoklama yapılmadı.");
            return;
        }
        var results = new List<string>();
        try
        {
            if (!PodPanel.Core.Bluetooth.BluetoothServices.SetEnabled(address, PodPanel.Core.Bluetooth.BluetoothServices.AudioSink, false, out string error))
            {
                Console.WriteLine("Windows ses profili kapatılamadı: " + error);
                return;
            }
            Console.WriteLine($"[{Stamp}] Windows ses profili kapatıldı");
            Thread.Sleep(1500);

            AapClient? channel = null;
            int code = 0;
            for (int attempt = 0; attempt < 16 && channel == null; attempt++)
            {
                channel = AapClient.Open(address, out code, BridgePipes.AvdtpPsm);
                if (channel == null)
                {
                    Console.WriteLine($"[{Stamp}] kanal açılamadı: {BridgePipes.DescribeError(code)} ({code})");
                    if (code is not (BridgePipes.ErrorNotConnected or BridgePipes.ErrorRefused or BridgePipes.ErrorTimeout or BridgePipes.ErrorBusy)) break;
                    Thread.Sleep(700);
                }
            }
            if (channel == null) return;
            using (channel)
            using (var avdtp = new AvdtpSignaling(channel))
            {
                Console.WriteLine($"[{Stamp}] işaretleşme kanalı açıldı");
                avdtp.Trace += line => Console.WriteLine($"[{Stamp}]   {line}");
                var delays = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
                using var reported = new AutoResetEvent(false);
                // Gecikme bildirimindeki uç numarası bizim ucumuzdur (hep 1), kulaklığınki değil: son gelen değer tutulur.
                avdtp.DelayReported += (_, delay) =>
                {
                    delays[0] = delay;
                    reported.Set();
                };

                AvdtpResponse discover = avdtp.Request(AvdtpSignaling.Discover, ReadOnlySpan<byte>.Empty);
                if (!discover.Accepted)
                {
                    Console.WriteLine("DISCOVER: " + discover.Describe());
                    return;
                }
                // Uç başına iki bayt: uç numarası ve kullanımda biti, ortam türü ve yönü.
                var codecs = new Dictionary<int, (byte Type, byte[] Info)>();
                for (int i = 0; i + 1 < discover.Payload.Length; i += 2)
                {
                    int seid = discover.Payload[i] >> 2;
                    AvdtpResponse caps = avdtp.Request(AvdtpSignaling.GetAllCapabilities, new[] { (byte)(seid << 2) });
                    // Yetenekler: kategori, uzunluk, veri. Kategori 7 = ortam codec'i (ortam türü, codec türü, bilgi).
                    for (int at = 0; caps.Accepted && at + 1 < caps.Payload.Length; at += 2 + caps.Payload[at + 1])
                    {
                        int length = caps.Payload[at + 1];
                        if (caps.Payload[at] == 0x07 && length >= 2 && at + 2 + length <= caps.Payload.Length)
                        {
                            codecs[seid] = (caps.Payload[at + 3], caps.Payload.AsSpan(at + 4, length - 2).ToArray());
                        }
                    }
                }

                // Her codec için seçilen yapılandırma: 48 kHz stereo.
                var plans = new (string Name, byte Type, Func<byte[], bool> Match, byte[] Config)[]
                {
                    ("AAC (Windows'un kullandığı)", 0x02, _ => true, new byte[] { 0x80, 0x00, 0x84, 0x83, 0xE8, 0x00 }),
                    ("SBC", 0x00, _ => true, new byte[] { 0x11, 0x15, 0x02, 0x35 }),
                    ("Apple AAC-ELD", 0xFF, info => info.Length >= 6 && info[0] == 0x4C && info[4] == 0x01 && info[5] == 0x80,
                        new byte[] { 0x4C, 0x00, 0x00, 0x00, 0x01, 0x80, 0x00, 0x80, 0x00, 0x84, 0x00, 0x83, 0xE8, 0x00 }),
                };
                foreach (var plan in plans)
                {
                    int seid = codecs.Where(c => c.Value.Type == plan.Type && plan.Match(c.Value.Info)).Select(c => c.Key).DefaultIfEmpty(-1).First();
                    if (seid < 0)
                    {
                        results.Add($"{plan.Name}: kulaklık bu ucu sunmuyor");
                        continue;
                    }
                    Console.WriteLine($"[{Stamp}] {plan.Name}: uç {seid} yapılandırılıyor");
                    delays.TryRemove(0, out _);
                    reported.Reset();
                    // Kulaklığın ucu, bizim ucumuz (1), ortam taşıma, ortam codec'i, gecikme bildirimi.
                    var request = new List<byte> { (byte)(seid << 2), 1 << 2, 0x01, 0x00, 0x07, (byte)(2 + plan.Config.Length), 0x00, plan.Type };
                    request.AddRange(plan.Config);
                    request.AddRange(new byte[] { 0x08, 0x00 });
                    AvdtpResponse set = avdtp.Request(AvdtpSignaling.SetConfiguration, request.ToArray());
                    if (!set.Accepted)
                    {
                        results.Add($"{plan.Name}: yapılandırma {set.Describe()}");
                        continue;
                    }
                    bool got = reported.WaitOne(2500);
                    if (!got)
                    {
                        // Bazı alıcılar gecikmeyi akış açılırken bildirir.
                        AvdtpResponse open = avdtp.Request(AvdtpSignaling.Open, new[] { (byte)(seid << 2) });
                        Console.WriteLine($"[{Stamp}] OPEN: {open.Describe()}");
                        got = reported.WaitOne(2500);
                    }
                    results.Add(got && delays.TryGetValue(0, out int delay)
                        ? $"{plan.Name}: yapılandırma kabul edildi, bildirilen gecikme {delay / 10.0:0.#} ms"
                        : $"{plan.Name}: yapılandırma kabul edildi, gecikme bildirilmedi");
                    AvdtpResponse abort = avdtp.Request(AvdtpSignaling.Abort, new[] { (byte)(seid << 2) });
                    Console.WriteLine($"[{Stamp}] ABORT: {abort.Describe()}");
                    Thread.Sleep(400);
                }
            }
        }
        finally
        {
            // Windows profili kapatmayı bitirmeden gelen açma isteği 87 hatasıyla reddedilir: birkaç kez denenir.
            for (int attempt = 1; attempt <= 6; attempt++)
            {
                bool restored = PodPanel.Core.Bluetooth.BluetoothServices.SetEnabled(address, PodPanel.Core.Bluetooth.BluetoothServices.AudioSink, true, out string error);
                Thread.Sleep(2500);
                bool back = PodPanel.Core.Bluetooth.BluetoothServices.IsEnabled(address, PodPanel.Core.Bluetooth.BluetoothServices.AudioSink) == true;
                Console.WriteLine($"[{Stamp}] Windows ses profilini geri açma, deneme {attempt}: {(back ? "açık" : restored ? "bekleniyor" : error)}");
                if (back) break;
            }
            Console.WriteLine($"[{Stamp}] Windows ses profili: {Describe(PodPanel.Core.Bluetooth.BluetoothServices.IsEnabled(address, PodPanel.Core.Bluetooth.BluetoothServices.AudioSink))}");
            Console.WriteLine("SONUÇ:");
            foreach (string line in results) Console.WriteLine("  " + line);
            if (results.Count == 0) Console.WriteLine("  (ölçüm alınamadı)");
        }
    }

    /// <summary>
    /// Servisin bildirdiği gönderim hacminden kulaklığa giden sesin bit hızını yazar (ses çalarken anlamlıdır).
    /// </summary>
    private static void Bitrate(ulong address, double seconds)
    {
        using var client = new BridgeStatusClient();
        var samples = new List<LinkVolume>();
        DateTime last = DateTime.MinValue;
        client.Changed += () =>
        {
            if (client.Get(address, TimeSpan.FromSeconds(3)) is not { } volume || volume.At == last) return;
            last = volume.At;
            lock (samples) samples.Add(volume);
            Console.WriteLine($"[{Stamp}] kanal 0x{volume.Cid:X4}: {volume.RawKbps,6:0.0} kbps ham, {volume.AudioKbps,6:0.0} kbps ses, {volume.PdusPerSecond,5:0.0} paket/sn, paket başına {(volume.PdusPerSecond > 0 ? volume.BytesPerSecond / volume.PdusPerSecond : 0):0} bayt");
        };
        client.Start();
        Thread.Sleep((int)(seconds * 1000));
        lock (samples)
        {
            var busy = samples.Where(v => v.AudioKbps >= 32).ToList();
            Console.WriteLine(busy.Count == 0
                ? (samples.Count == 0 ? "ÖZET : servis hacim bildirmedi (eski servis sürümü ya da veri akmıyor)" : "ÖZET : ses akışı görülmedi")
                : $"ÖZET : {busy.Count} ölçüm, ses ortalama {busy.Average(v => v.AudioKbps):0.0} kbps (en az {busy.Min(v => v.AudioKbps):0.0}, en çok {busy.Max(v => v.AudioKbps):0.0}), paket/sn {busy.Average(v => v.PdusPerSecond):0.0}");
        }
    }

    /// <summary>micdump dosyasındaki erişim birimlerini çözer; örnek sayısını ve ses düzeyini yazar (ses dosyası üretmez).</summary>
    private static void MicDecode(string path)
    {
        byte[] dump = File.ReadAllBytes(path);
        using var decoder = new AacEldDecoder();
        Console.WriteLine($"çözücü açıldı: {decoder.LibraryVersion}");
        var output = new float[AacEldDecoder.MaxSamplesPerUnit];
        int units = 0, failed = 0, samples = 0;
        double energy = 0;
        float peak = 0;
        var perUnit = new List<int>();
        for (int position = 0; position + 6 <= dump.Length;)
        {
            int length = BitConverter.ToUInt16(dump, position + 4);
            ReadOnlySpan<byte> packet = dump.AsSpan(position + 6, length);
            position += 6 + length;
            if (packet.Length < 22 || packet[6] != 0x01 || packet[7] != 0x00) continue;
            for (int offset = 22; packet.Length - offset >= 5;)
            {
                int size = packet[offset + 4];
                if (size == 0 || offset + 5 + size > packet.Length) break;
                int count = decoder.Decode(packet.Slice(offset + 5, size), output);
                units++;
                if (count < 0) failed++;
                else
                {
                    perUnit.Add(count);
                    samples += count;
                    for (int i = 0; i < count; i++)
                    {
                        energy += output[i] * output[i];
                        peak = Math.Max(peak, Math.Abs(output[i]));
                    }
                }
                offset += 5 + size;
            }
        }
        double rms = samples > 0 ? Math.Sqrt(energy / samples) : 0;
        Console.WriteLine($"erişim birimi: {units}, reddedilen: {failed}, örnek: {samples} ({samples / 48000.0 * 1000:0} ms, 48 kHz)");
        Console.WriteLine($"birim başına örnek: {string.Join(",", perUnit.Take(12))}{(perUnit.Count > 12 ? ",…" : "")}");
        Console.WriteLine($"düzey: RMS {(rms > 0 ? 20 * Math.Log10(rms) : -120):0.0} dBFS, tepe {(peak > 0 ? 20 * Math.Log10(peak) : -120):0.0} dBFS");
    }

    /// <summary>
    /// Servis olmadan istemciyi sınar: bu sürecin içinde sahte bir kulaklık kanalı açar, açılış sırasını, paket
    /// çözümünü ve mod değişimini uçtan uca dener.
    /// </summary>
    private static void SelfTest()
    {
        string name = "PodPanel.AapTest." + Environment.ProcessId;
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task accept = server.WaitForConnectionAsync();
        pipe.Connect(2000);
        accept.Wait(2000);
        pipe.ReadMode = PipeTransmissionMode.Message;

        var received = new List<byte[]>();
        byte mode = (byte)ListeningMode.NoiseCancellation;
        void Reply(params byte[] packet) => server.Write(new byte[] { BridgePipes.AapPacket }.Concat(packet).ToArray());
        var fake = Task.Run(() =>
        {
            var buffer = new byte[2048];
            while (true)
            {
                int n;
                try { n = server.Read(buffer, 0, buffer.Length); }
                catch (IOException) { return; }
                if (n == 0) return;
                byte[] packet = buffer.AsSpan(0, n).ToArray();
                lock (received) received.Add(packet);
                if (packet.AsSpan().SequenceEqual(Aap.Handshake))
                {
                    Reply(0x01, 0x00, 0x04, 0x00, 0x00, 0x00, 0x01, 0x00);
                }
                else if (Aap.Opcode(packet) == 0x0F)
                {
                    Reply(0x04, 0x00, 0x04, 0x00, 0x04, 0x00, 0x03, 0x02, 0x01, 0x5A, 0x02, 0x01, 0x04, 0x01, 0x5F, 0x02, 0x01, 0x08, 0x01, 0x32, 0x01, 0x01);
                    Reply(0x04, 0x00, 0x04, 0x00, 0x06, 0x00, 0x00, 0x00);
                    Reply(0x04, 0x00, 0x04, 0x00, 0x09, 0x00, 0x0D, mode, 0x00, 0x00, 0x00);
                }
                else if (Aap.Opcode(packet) == 0x09 && packet.Length >= 8 && packet[6] == Aap.ListeningModeId)
                {
                    mode = packet[7];
                    Reply(0x04, 0x00, 0x04, 0x00, 0x09, 0x00, 0x0D, mode, 0x00, 0x00, 0x00);
                }
            }
        });

        var lines = new List<string>();
        ListeningMode? current = null;
        using var client = new AapClient(pipe);
        client.Packet += packet =>
        {
            lock (lines) lines.Add(Aap.Describe(packet));
            if (Aap.TryListeningMode(packet, out ListeningMode reported)) current = reported;
        };
        client.Start();
        bool ready = client.InitializeAsync().GetAwaiter().GetResult();
        Thread.Sleep(300);
        client.Send(Aap.SetListeningMode(ListeningMode.Transparency));
        Thread.Sleep(300);

        bool Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "TAMAM " : "HATA  ")} {what}");
            return ok;
        }
        bool all = true;
        lock (received)
        {
            all &= Check("açılış tamamlandı", ready);
            all &= Check("gönderilen sıra: el sıkışma, özellik bayrakları, bildirim isteği, mod",
                received.Count == 4 && received[0].AsSpan().SequenceEqual(Aap.Handshake) && received[1].AsSpan().SequenceEqual(Aap.FeatureFlags)
                && received[2].AsSpan().SequenceEqual(Aap.RequestNotifications(true)) && received[3].AsSpan().SequenceEqual(Aap.SetListeningMode(ListeningMode.Transparency)));
        }
        lock (lines)
        {
            foreach (string line in lines) Console.WriteLine("       " + line);
            all &= Check("pil çözüldü", lines.Contains("pil: sağ %90, sol %95, kutu %50 (şarjda)"));
            all &= Check("kulak durumu çözüldü", lines.Contains("kulak: birincil kulakta, ikincil kulakta"));
            all &= Check("ilk mod çözüldü", lines.Contains("dinleme modu: Gürültü Engelleme"));
        }
        all &= Check("mod değişimi onaylandı", current == ListeningMode.Transparency);
        Console.WriteLine(all ? "SONUÇ: hepsi geçti" : "SONUÇ: başarısız");
        server.Dispose();
        fake.Wait(1000);
    }
}
