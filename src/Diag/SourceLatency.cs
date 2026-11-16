using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using PodPanel.Core.Audio;
using PodPanel.Core.Bridge;

/// <summary>
/// Windows tarafındaki gecikmeyi ölçer: sesin ses motoruna verildiği an ile onu taşıyan ilk Bluetooth paketinin
/// gönderildiği an arasındaki süre (motor + AAC kodlama + paketleme).
///
/// Yöntem: köprü servisi her 2 saniyede bir, kulaklığa giden en yoğun kanalın bayt ve paket sayısını bildirir.
/// Sessizlikte paketler çok küçük (yaklaşık 28 bayt), gürültüde en büyük boydadır (yaklaşık 700 bayt). Gürültü bilinen
/// bir anda başlatılır; başladığı penceredeki "dolu" paket sayısı pencerenin bayt toplamından hesaplanır ve pencerenin
/// sonundan geriye sayılarak ilk dolu paketin gönderildiği an bulunur. Çözünürlük bir AAC çerçevesi kadardır (21 ms);
/// birkaç denemenin ortancası alınır. Kulaklığın içindeki süre bu ölçümde yoktur.
/// Kullanım: srclatency "değerler=150,60;deneme=6". Panel kapalı olmalıdır (tamponu ayarlamak için kanal gerekir).
/// </summary>
internal static class SourceLatency
{
    private sealed record Window(long EndTick, double Seconds, long Bytes, int Pdus);

    private static string Stamp => DateTime.Now.ToString("HH:mm:ss.fff");

    public static void Run(ulong address, string argument)
    {
        var options = argument.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        int[] values = options.GetValueOrDefault("değerler", "150,60").Split(',').Select(int.Parse).ToArray();
        int trials = int.Parse(options.GetValueOrDefault("deneme", "6"));
        float amplitude = float.Parse(options.GetValueOrDefault("genlik", "0.2"), CultureInfo.InvariantCulture);

        using var audio = new AudioService();
        EndpointInfo? output = audio.Enumerate().FirstOrDefault(e => e.Flow == PodPanel.Core.Interop.DataFlow.Render && e.Bt?.VendorId == 0x004C && e.IsActive);
        if (output == null || address == 0)
        {
            Console.WriteLine("AirPods çıkışı bulunamadı.");
            return;
        }
        using NamedPipeClientStream? status = BridgePipes.Connect(BridgePipes.ServicePipe, out int pipeError);
        if (status == null)
        {
            Console.WriteLine($"Servise bağlanılamadı: {BridgePipes.DescribeError(pipeError)} ({pipeError})");
            return;
        }
        var windows = new List<Window>();
        using var stop = new CancellationTokenSource();
        Task reader = Task.Run(async () =>
        {
            var buffer = new byte[2048];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    int n = await status.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
                    long tick = Stopwatch.GetTimestamp();
                    if (n == 0) break;
                    if (buffer[0] != 0x28 || n < 5) continue;
                    double seconds = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(1)) / 1000.0;
                    for (int offset = 5; offset + 18 <= n; offset += 18)
                    {
                        if (BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset)) != address) continue;
                        lock (windows)
                        {
                            windows.Add(new Window(tick, seconds, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 10)), (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 14))));
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Kanal kapandı ya da ölçüm bitti.
            }
        });

        using AapClient? client = AapClient.Open(address, out int error);
        if (client == null)
        {
            Console.WriteLine($"Kontrol kanalı açılamadı: {BridgePipes.DescribeError(error)} ({error})");
            return;
        }
        if (!client.InitializeAsync().GetAwaiter().GetResult())
        {
            Console.WriteLine("Kulaklık yanıt vermedi.");
            return;
        }
        Thread.Sleep(1500);
        try
        {
            foreach (int value in values)
            {
                client.Send(Aap.SetJitterBuffer(value));
                Thread.Sleep(2500);
                // Tek bir kesintisiz akış: 2,6 saniyelik gürültü parçaları, aralarında 2,7-3,4 saniye sessizlik.
                // Parçaların başlangıcı rapor pencerelerine göre rastgele düşsün diye aralık değişkendir.
                var starts = new List<int>();
                var random = new Random(value * 7919);
                for (int i = 0, at = 3200; i < trials; i++, at += 2600 + 2700 + random.Next(700)) starts.Add(at);
                var queuedAt = new long[trials];
                long streamStart = Stopwatch.GetTimestamp();
                bool played = audio.PlayBursts(output.Id, starts, 2600, amplitude, 3, (index, tick) => queuedAt[index] = tick);
                Thread.Sleep(2300);
                List<Window> seen;
                lock (windows) seen = windows.Where(w => w.EndTick > streamStart).ToList();
                Report(value, played, queuedAt, seen);
            }
        }
        finally
        {
            client.Send(Aap.SetJitterBuffer(Aap.DefaultJitterMs));
            Thread.Sleep(400);
            stop.Cancel();
        }
    }

    private static void Report(int value, bool played, long[] queuedAt, List<Window> windows)
    {
        if (!played || windows.Count < 6)
        {
            Console.WriteLine($"tampon {value,3} ms: ölçülemedi ({(played ? $"yalnızca {windows.Count} rapor geldi" : "ses çalınamadı")})");
            return;
        }
        double frequency = Stopwatch.Frequency;
        // Paket başına bayt: en küçük pencereler sessizlik, en büyükler tam gürültü.
        var perPdu = windows.Where(w => w.Pdus > 20).Select(w => (double)w.Bytes / w.Pdus).OrderBy(v => v).ToList();
        double silent = perPdu[0], full = perPdu[^1];
        if (full < silent * 4)
        {
            Console.WriteLine($"tampon {value,3} ms: sessiz ve dolu paketler ayrışmadı (paket başına {silent:0}-{full:0} bayt)");
            return;
        }
        var latencies = new List<double>();
        foreach (long tick in queuedAt)
        {
            if (tick == 0) continue;
            // Gürültünün motora verildiği andan sonra biten ilk pencere, içinde dolu paket olan ilk penceredir
            // (ya da hemen sonraki, başlangıç pencere sonuna çok yakınsa).
            foreach (Window w in windows.Where(w => w.EndTick > tick).OrderBy(w => w.EndTick).Take(2))
            {
                if (w.Pdus < 20) continue;
                double average = (double)w.Bytes / w.Pdus;
                double fullPackets = (average - silent) / (full - silent) * w.Pdus;
                if (fullPackets < 0.5) continue; // bu pencerede henüz dolu paket yok
                if (fullPackets > w.Pdus - 0.5) break; // pencerenin tamamı dolu: başlangıç bu pencereden önceydi, ölçülemez
                double packetSeconds = w.Seconds / w.Pdus;
                double firstFull = (w.EndTick / frequency) - (fullPackets * packetSeconds);
                latencies.Add((firstFull - (tick / frequency)) * 1000);
                break;
            }
        }
        latencies.Sort();
        string summary = latencies.Count == 0 ? "geçiş penceresi yakalanamadı"
            : $"ortanca {latencies[latencies.Count / 2]:0} ms (en az {latencies[0]:0}, en çok {latencies[^1]:0}; {latencies.Count} ölçüm)";
        Console.WriteLine($"tampon {value,3} ms: Windows tarafı {summary}; paket başına sessizlikte {silent:0}, gürültüde {full:0} bayt; paket aralığı {windows.Average(w => w.Seconds / Math.Max(1, w.Pdus)) * 1000:0.0} ms");
    }
}
