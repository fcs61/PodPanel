using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using PodPanel.Core.Audio;
using PodPanel.Core.Bridge;

/// <summary>
/// Akustik geri döngüyle gecikme ölçümü: AirPods'tan kısa gürültü patlamaları çalınır, aynı anda AirPods'un kendi
/// mikrofonu (kontrol kanalı üzerinden) dinlenir. Patlamanın ses motoruna verildiği an ile mikrofonda duyulduğu an
/// arasındaki süre "çalma gecikmesi + mikrofon yolu gecikmesi"dir. Mikrofon yolu sabit olduğu için farklı tampon
/// değerleri arasındaki FARK doğrudan çalma gecikmesindeki farktır; mutlak değer mikrofon yolunu da içerir.
/// Kullanım: latency "değerler=150,60;patlama=8;kanal=3;kulak=0;genlik=0.8". Panel kapalı olmalıdır.
/// </summary>
internal static class LatencyMeasurement
{
    private static readonly byte[] StartMicrophone = { 0x04, 0x00, 0x04, 0x00, 0x58, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x01, 0x82, 0x00, 0x00, 0x00, 0x04, 0x96, 0x00 };
    private static readonly byte[] StopMicrophone = { 0x04, 0x00, 0x04, 0x00, 0x58, 0x00, 0x00, 0x00, 0x02, 0x00, 0x03, 0x01 };

    private const int MicRate = AacEldDecoder.OutputRate; // çözülmüş mikrofon sesi 48 kHz
    private const double UnitTicksPerSecond = 24000;      // erişim birimi zaman damgası: birim başına 180 = 7,5 ms

    private sealed record Unit(uint Stamp, long Arrival, float[] Samples);

    private static string Stamp => DateTime.Now.ToString("HH:mm:ss.fff");

    public static void Run(ulong address, string argument)
    {
        var options = argument.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        int[] values = options.GetValueOrDefault("değerler", "150,60").Split(',').Select(int.Parse).ToArray();
        int bursts = int.Parse(options.GetValueOrDefault("patlama", "8"));
        int channelMask = int.Parse(options.GetValueOrDefault("kanal", "3"));
        bool earDetection = options.GetValueOrDefault("kulak", "0") == "1";
        float amplitude = float.Parse(options.GetValueOrDefault("genlik", "0.8"), CultureInfo.InvariantCulture);

        using var audio = new AudioService();
        EndpointInfo? output = audio.Enumerate().FirstOrDefault(e => e.Flow == PodPanel.Core.Interop.DataFlow.Render && e.Bt?.VendorId == 0x004C && e.IsActive);
        if (output == null || address == 0)
        {
            Console.WriteLine("AirPods çıkışı bulunamadı.");
            return;
        }
        using AapClient? client = AapClient.Open(address, out int error);
        if (client == null)
        {
            Console.WriteLine($"Kontrol kanalı açılamadı: {BridgePipes.DescribeError(error)} ({error})");
            return;
        }
        using var decoder = new AacEldDecoder();
        var units = new List<Unit>();
        var scratch = new float[AacEldDecoder.MaxSamplesPerUnit];
        string ear = "";
        client.Packet += packet =>
        {
            if (Aap.TryEar(packet, out byte primary, out byte secondary)) ear = $"birincil {Aap.EarName(primary)}, ikincil {Aap.EarName(secondary)}";
            if (Aap.Opcode(packet) != 0x58 || packet.Length < 22 || packet[6] != 0x01 || packet[7] != 0x00) return;
            long arrival = Stopwatch.GetTimestamp();
            for (int offset = 22; packet.Length - offset >= 5;)
            {
                int length = packet[offset + 4];
                if (length == 0 || offset + 5 + length > packet.Length) break;
                int count = decoder.Decode(packet.AsSpan(offset + 5, length), scratch);
                if (count > 0)
                {
                    lock (units) units.Add(new Unit(BitConverter.ToUInt32(packet, offset), arrival, scratch.AsSpan(0, count).ToArray()));
                }
                offset += 5 + length;
            }
        };
        if (!client.InitializeAsync(earDetection).GetAwaiter().GetResult())
        {
            Console.WriteLine("Kulaklık yanıt vermedi.");
            return;
        }
        Thread.Sleep(1500);
        Console.WriteLine($"[{Stamp}] kulak durumu: {ear}; kulak algılama bildirimi: {(earDetection ? "açık" : "kapalı")}; çıkış: {output.Name}");

        _ = timeBeginPeriod(1);
        try
        {
            client.Send(StartMicrophone);
            Thread.Sleep(1500);
            int first;
            lock (units) first = units.Count;
            Console.WriteLine($"[{Stamp}] mikrofon akışı: ilk 1,5 saniyede {first} birim ({first / 1.5:0} birim/sn; kesintisiz akışta ~133)");
            if (first < 60)
            {
                // Akış kesilmiş olabilir (kulakta değilken kulaklık keser): bir kez daha istenir.
                client.Send(StartMicrophone);
                Thread.Sleep(1500);
                lock (units) Console.WriteLine($"[{Stamp}] yeniden istendi: toplam {units.Count} birim");
            }

            foreach (int value in values)
            {
                client.Send(Aap.SetJitterBuffer(value));
                Thread.Sleep(3000);
                int before;
                lock (units) before = units.Count;
                var queuedAt = new long[bursts];
                // Patlamalar 700-900 ms aralıklı; aralık sabit değil ki dönemsel bir gürültüyle karışmasın.
                var starts = new List<int>();
                var random = new Random(value);
                for (int i = 0, at = 400; i < bursts; i++, at += 700 + random.Next(200)) starts.Add(at);
                bool played = audio.PlayBursts(output.Id, starts, 6, amplitude, channelMask, (index, tick) => queuedAt[index] = tick);
                Thread.Sleep(700);
                List<Unit> window;
                lock (units) window = units.Skip(Math.Max(0, before - 40)).ToList();
                Report(value, played, queuedAt, window);
            }
        }
        finally
        {
            client.Send(StopMicrophone);
            client.Send(Aap.SetJitterBuffer(Aap.DefaultJitterMs));
            Thread.Sleep(400);
            _ = timeEndPeriod(1);
        }
    }

    private static void Report(int value, bool played, long[] queuedAt, List<Unit> units)
    {
        if (!played || units.Count < 20)
        {
            Console.WriteLine($"tampon {value,3} ms: ölçülemedi ({(played ? $"mikrofondan yalnızca {units.Count} birim geldi" : "ses çalınamadı")})");
            return;
        }
        // Mikrofon zaman çizgisi: birimlerin kendi zaman damgaları (düzgün saat) + en erken varış sınırı.
        uint stamp0 = units[0].Stamp;
        double Seconds(Unit u) => unchecked((int)(u.Stamp - stamp0)) / UnitTicksPerSecond;
        double frequency = Stopwatch.Frequency;
        double origin = units.Min(u => (u.Arrival / frequency) - Seconds(u) - (180 / UnitTicksPerSecond));
        // Ardışık birimler tek bir örnek dizisine yerleştirilir.
        double duration = Seconds(units[^1]) + 0.05;
        var signal = new float[(int)(duration * MicRate) + 2048];
        foreach (Unit u in units)
        {
            int at = (int)Math.Round(Seconds(u) * MicRate);
            for (int i = 0; i < u.Samples.Length && at + i < signal.Length; i++) signal[at + i] = u.Samples[i];
        }
        double rms = Math.Sqrt(signal.Take((int)(duration * MicRate)).Sum(s => (double)s * s) / Math.Max(1, (int)(duration * MicRate)));

        var latencies = new List<double>();
        var ratios = new List<double>();
        foreach (long tick in queuedAt)
        {
            if (tick == 0) continue;
            double t0 = (tick / frequency) - origin; // patlamanın motora verildiği an, mikrofon zaman çizgisinde
            int from = (int)((t0 - 0.08) * MicRate), to = (int)((t0 + 0.75) * MicRate);
            if (from < 0 || to >= signal.Length) continue;
            // Taban: patlamadan önceki 80 ms. Başlangıç: 3 ms'lik pencerenin enerjisi tabanın 8 katını ilk aştığı an.
            int window = MicRate * 3 / 1000;
            double floor = Energy(signal, from, (int)(t0 * MicRate) - from) + 1e-12;
            int onset = -1;
            double best = 0;
            for (int i = (int)(t0 * MicRate); i + window < to; i += window / 3)
            {
                double energy = Energy(signal, i, window);
                best = Math.Max(best, energy / floor);
                if (onset < 0 && energy > floor * 8) onset = i;
            }
            ratios.Add(best);
            if (onset >= 0) latencies.Add(((onset / (double)MicRate) - t0) * 1000);
        }
        latencies.Sort();
        string summary = latencies.Count == 0 ? "patlamalar mikrofonda duyulmadı"
            : $"ortanca {latencies[latencies.Count / 2]:0} ms (en az {latencies[0]:0}, en çok {latencies[^1]:0}; {latencies.Count}/{queuedAt.Length} patlama duyuldu)";
        Console.WriteLine($"tampon {value,3} ms: {summary}; mikrofon düzeyi {(rms > 0 ? 20 * Math.Log10(rms) : -120):0} dBFS, en yüksek patlama/taban oranı {(ratios.Count > 0 ? 10 * Math.Log10(ratios.Max()) : 0):0} dB");
    }

    private static double Energy(float[] signal, int start, int count)
    {
        double sum = 0;
        for (int i = Math.Max(0, start); i < start + count && i < signal.Length; i++) sum += (double)signal[i] * signal[i];
        return sum / Math.Max(1, count);
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);
}
