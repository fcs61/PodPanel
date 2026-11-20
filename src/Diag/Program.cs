// PodPanel tanı aracı: çekirdek servisleri arayüz olmadan çalıştırıp durumu yazar.
//   state [sn]        : durumu bir kez derleyip JSON olarak yazar (varsayılan 9 sn bekler)
//   watch <sn>        : durum değişikliklerini ve telemetri olaylarını izler
//   gametest          : oyun modunu açar, sonucu yazar, kapatır ve geri alındığını doğrular
//   locktest          : mikrofon kilidini (uç noktayı devre dışı bırakma) dener ve geri alır
//   spatial on|off    : uzamsal sesi açar ya da kapatır
//   connect|disconnect: ses profillerini bağlar ya da keser
//   bridge [sn]       : köprü servisinin durum kanalını dinler
//   aap [sn]          : kulaklığın kontrol kanalını açar; pil, kulak durumu ve dinleme modunu yazar (ayar değiştirmez)
//   mode off|nc|transparency|adaptive : dinleme modunu değiştirir ve kulaklığın onayını bekler
//   aaptest           : istemciyi servis olmadan, sahte bir kulaklık kanalıyla sınar
using System.Text.Encodings.Web;
using System.Text.Json;
using PodPanel.Core;

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "state";
string argument = args.Length > 1 ? args[1] : "";

var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
Log.Echo = true;

// Paneldeki "Oyun ekle" listesinin gördüğü açık uygulamalar.
if (command == "apps")
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var apps = RunningApps.List();
    watch.Stop();
    foreach (var app in apps) Console.WriteLine($"{app.Process}.exe  |  {app.Label}");
    Console.WriteLine($"{apps.Count} uygulama, {watch.ElapsedMilliseconds} ms");
    return;
}

var settings = Settings.Load();
if (command is "bridge" or "aap" or "mode" or "aaptest" or "micdump" or "micdecode" or "micrec" or "hfp" or "a2dp" or "bitrate" or "eldprobe" or "jitter" or "latency" or "srclatency")
{
    BridgeCommands.Run(command, argument, settings);
    return;
}
bool gameModeBefore = settings.GameMode, lockBefore = settings.LockMic;
using var monitor = new PodMonitor(settings, "-Diag");

string Summary(PodState s) =>
    $"bulundu={s.DeviceFound} bağlı={s.Connected} çıkış={(s.IsDefaultOutput ? "AirPods" : s.DefaultOutputName)} " +
    $"mikrofon={(s.IsDefaultMic ? "AirPods" : s.DefaultMicName)} mikKapalı={s.MicDisabled} mikKullanan=[{string.Join(",", s.MicUsers)}] " +
    $"pil={s.BatteryLeft}/{s.BatteryRight}/{s.BatteryCase} kulakta={s.InEarLeft}/{s.InEarRight} etkinlik='{s.Activity}' " +
    $"codec={s.Codec} {s.SampleRate}Hz gecikme={s.DelayMs} hfp={s.HfpCodec} önbellek={s.LinkFromCache} " +
    $"uzamsal={s.SpatialOn}({s.SpatialFormat}) atmos={s.SpatialAtmosAvailable} oyun={s.GameModeActive}({s.GameModeReason}) altMik={s.GameMicName} " +
    $"köprü={s.BridgeAvailable} kanal={s.ControlOpen} mod={s.ListeningMode} bekleyen={s.ListeningModePending} takılı={s.Worn} kesinPil={s.BatteryExact} " +
    $"köprüMik={s.BridgeMicOn} hazır={s.BridgeMicReady} kayıt={s.BridgeMicCapturing} akış={s.BridgeMicStreaming} varsayılan={s.BridgeMicIsDefault} not='{s.BridgeMicNote}'";

// Mikrofon köprüsü denemesinde yalnızca köprüyle ilgili alanlar.
string MicSummary(PodState s) =>
    $"bağlı={s.Connected} kanal={s.ControlOpen} takılı={s.Worn} köprüMik={s.BridgeMicOn} hazır={s.BridgeMicReady} kayıt={s.BridgeMicCapturing} " +
    $"akış={s.BridgeMicStreaming} varsayılanMik='{(s.BridgeMicIsDefault ? "köprü" : s.DefaultMicName)}' elSerbestMik={s.HasMic} çıkış={(s.IsDefaultOutput ? "AirPods" : s.DefaultOutputName)} " +
    $"ses='{s.Codec} {s.SampleRate}' mono={s.MonoMode} not='{s.BridgeMicNote}'";

void Wait(double seconds) => Thread.Sleep((int)(seconds * 1000));

switch (command)
{
    case "state":
    {
        monitor.Start();
        Wait(double.TryParse(argument, out double s) ? s : 9);
        Console.WriteLine(JsonSerializer.Serialize(monitor.Current, json));
        break;
    }
    case "watch":
    {
        string last = "";
        monitor.StateChanged += state =>
        {
            string line = Summary(state);
            if (line == last) return;
            last = line;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
        };
        monitor.Start();
        Wait(1.5);
        if (monitor.Telemetry != null) monitor.Telemetry.RawEvent += e => Console.WriteLine($"    olay: {e}");
        Wait(double.TryParse(argument, out double s) ? s : 20);
        break;
    }
    case "gametest":
    {
        monitor.Start();
        Wait(4);
        Console.WriteLine("ÖNCE : " + Summary(monitor.Current));
        monitor.SetGameMode(true);
        Wait(4);
        Console.WriteLine("AÇIK : " + Summary(monitor.Current));
        monitor.SetGameMode(false);
        Wait(4);
        Console.WriteLine("SONRA: " + Summary(monitor.Current));
        break;
    }
    case "locktest":
    {
        monitor.Start();
        Wait(4);
        Console.WriteLine("ÖNCE : " + Summary(monitor.Current));
        monitor.SetLockMic(true);
        monitor.SetGameMode(true);
        Wait(5);
        Console.WriteLine("KİLİT: " + Summary(monitor.Current));
        monitor.SetGameMode(false);
        monitor.SetLockMic(false);
        Wait(5);
        Console.WriteLine("SONRA: " + Summary(monitor.Current));
        break;
    }
    case "sessiontest":
    {
        // Oturum tespiti: mikrofon açmadan, AirPods çıkışına sessiz akış gönderip bu sürecin listelendiğini doğrular.
        monitor.Start();
        Wait(3);
        string endpoint = monitor.Current.OutputEndpointId;
        if (endpoint.Length == 0) { Console.WriteLine("AirPods çıkışı bulunamadı"); break; }
        var player = new Thread(() =>
        {
            using var audio = new PodPanel.Core.Audio.AudioService();
            audio.ProbeSilence(endpoint, 2500);
        });
        player.Start();
        Wait(1.2);
        using (var audio = new PodPanel.Core.Audio.AudioService())
        {
            var sessions = audio.GetActiveSessions(endpoint);
            Console.WriteLine("Etkin oturumlar: " + (sessions.Count == 0 ? "(yok)" : string.Join(", ", sessions.Select(s => $"{s.ProcessName} (pid {s.ProcessId})"))));
            Console.WriteLine("Bu süreç: pid " + Environment.ProcessId);
        }
        player.Join();
        break;
    }
    case "autotest":
    {
        // Otomatik kural: bu aracın kendi süreç adı listedeyken oyun modu kendiliğinden açılmalı.
        var originalList = settings.AutoGameProcesses.ToList();
        bool originalAuto = settings.AutoGame;
        monitor.Start();
        Wait(4);
        Console.WriteLine("ÖNCE : " + Summary(monitor.Current));
        monitor.SetAutoGame(true, new[] { "PodPanel.Diag" });
        Wait(4);
        Console.WriteLine("OTO  : " + Summary(monitor.Current));
        monitor.SetAutoGame(originalAuto, originalList);
        Wait(4);
        Console.WriteLine("SONRA: " + Summary(monitor.Current));
        break;
    }
    case "spatialraw":
    {
        monitor.Start();
        Wait(3);
        Console.WriteLine(PodPanel.Core.Spatial.SpatialAudio.Describe(monitor.Current.OutputEndpointId));
        break;
    }
    case "spatial":
    {
        monitor.Start();
        Wait(4);
        Console.WriteLine("ÖNCE : " + Summary(monitor.Current));
        monitor.SetSpatial(argument.Equals("on", StringComparison.OrdinalIgnoreCase));
        Wait(4);
        Console.WriteLine("SONRA: " + Summary(monitor.Current));
        break;
    }
    case "mictest":
    {
        // Mikrofon köprüsünü açar, verilen süre boyunca durum değişikliklerini yazar, sonra kapatıp her şeyi geri alır.
        bool before = settings.BridgeMic;
        string last = "";
        monitor.StateChanged += state =>
        {
            string line = MicSummary(state);
            if (line == last) return;
            last = line;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
        };
        monitor.Start();
        Wait(4);
        monitor.SetBridgeMic(true);
        Wait(double.TryParse(argument, out double seconds) ? seconds : 30);
        monitor.SetBridgeMic(before);
        Wait(8);
        break;
    }
    case "jittertest":
    {
        // Panelin kullandığı yol: gecikme tamponunu 60 ms yapar, kulaklığın Windows'a bildirdiği değeri yazar, geri alır.
        monitor.Start();
        Wait(1.5);
        if (monitor.Telemetry != null)
        {
            monitor.Telemetry.RawEvent += e =>
            {
                if (e.Name.Contains("Delay")) Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] olay: {e}");
            };
        }
        Wait(6);
        Console.WriteLine($"ÖNCE : kanal={monitor.Current.ControlOpen} seçili={monitor.Current.JitterMs} bildirilen={monitor.Current.DelayMs} ms");
        monitor.SetJitter(60);
        Wait(6);
        Console.WriteLine($"60   : seçili={monitor.Current.JitterMs} bildirilen={monitor.Current.DelayMs} ms");
        monitor.SetJitter(0);
        Wait(5);
        Console.WriteLine($"SONRA: seçili={monitor.Current.JitterMs} bildirilen={monitor.Current.DelayMs} ms");
        break;
    }
    case "modetest":
    {
        // Panelin kullandığı yol: izleyici üzerinden mod isteği, kulaklığın onayı ve eski moda dönüş.
        monitor.Start();
        Wait(4);
        Console.WriteLine("ÖNCE : " + Summary(monitor.Current));
        var original = monitor.Current.ListeningMode;
        if (!monitor.Current.ControlOpen || !monitor.Current.Worn || original == null)
        {
            Console.WriteLine("Kontrol kanalı kapalı ya da kulaklık kulakta değil; deneme yapılmadı.");
            break;
        }
        var target = original == PodPanel.Core.Bridge.ListeningMode.Transparency
            ? PodPanel.Core.Bridge.ListeningMode.NoiseCancellation
            : PodPanel.Core.Bridge.ListeningMode.Transparency;
        monitor.SetListeningMode(target);
        Wait(0.4);
        Console.WriteLine($"İSTEK: mod={monitor.Current.ListeningMode} bekleyen={monitor.Current.ListeningModePending}");
        Wait(2.5);
        Console.WriteLine($"SONUÇ: mod={monitor.Current.ListeningMode} bekleyen={monitor.Current.ListeningModePending} " +
            (monitor.Current.ListeningMode == target ? "(onaylandı)" : "(ONAYLANMADI)"));
        monitor.SetListeningMode(original.Value);
        Wait(3);
        Console.WriteLine($"GERİ : mod={monitor.Current.ListeningMode} bekleyen={monitor.Current.ListeningModePending} " +
            (monitor.Current.ListeningMode == original ? "(eski moda dönüldü)" : "(DÖNÜLEMEDİ)"));
        break;
    }
    case "connect":
    case "disconnect":
    {
        monitor.Start();
        Wait(4);
        Console.WriteLine("ÖNCE : " + Summary(monitor.Current));
        if (monitor.Telemetry != null)
        {
            monitor.Telemetry.RawEvent += e =>
            {
                if (!e.Name.Contains("Pin")) Console.WriteLine($"    olay: {e}");
            };
        }
        if (command == "connect") monitor.Connect(); else monitor.Disconnect();
        Wait(8);
        Console.WriteLine("SONRA: " + Summary(monitor.Current));
        break;
    }
    default:
        Console.WriteLine("Bilinmeyen komut: " + command);
        break;
}

// Testler kullanıcının ayarını değiştirmiş olarak bırakmasın.
if (command is "gametest" or "locktest")
{
    monitor.SetGameMode(gameModeBefore);
    monitor.SetLockMic(lockBefore);
    Wait(1);
}
