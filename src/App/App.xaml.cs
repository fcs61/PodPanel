using System;
using System.Threading;
using System.Windows;
using PodPanel.Core;

namespace PodPanel;

public partial class App : Application
{
    private Mutex? _mutex;
    private PodMonitor? _monitor;
    private TrayHost? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Arayüz hatası: " + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write("Beklenmeyen hata: " + args.ExceptionObject);

        // Görsel doğrulama: paneli ekran dışında çizip PNG olarak kaydeder.
        string? snapshotPath = ArgValue(e.Args, "--snapshot");
        if (snapshotPath != null)
        {
            Loc.Use(ArgValue(e.Args, "--lang"));
            Snapshot.Run(this, snapshotPath, ArgValue(e.Args, "--mock") ?? "live", ArgValue(e.Args, "--theme") ?? "system");
            return;
        }

        bool wantsExit = Array.IndexOf(e.Args, "--exit") >= 0;
        bool wantsHide = Array.IndexOf(e.Args, "--hide") >= 0;

        _mutex = new Mutex(true, "PodPanel.SingleInstance", out bool created);
        if (!created)
        {
            // Zaten çalışıyor: komutu çalışan kopyaya ilet (varsayılan: paneli aç).
            Signal(wantsExit ? ExitSignal : wantsHide ? HideSignal : ShowSignal);
            Shutdown();
            return;
        }
        if (wantsExit || wantsHide)
        {
            Shutdown();
            return;
        }

        Settings settings = Settings.Load();
        Loc.Use(settings.Language);
        OfferAutostart(settings);
        _monitor = new PodMonitor(settings);
        var viewModel = new MainViewModel(_monitor, Dispatcher);
        _tray = new TrayHost(_monitor, viewModel, Shutdown);
        _monitor.Start();
        ListenForSignals();
        Log.Write("PodPanel başladı");
    }

    /// <summary>
    /// Kurulum programının kurduğu kopya (Program Files altında) için "Windows ile başlat"ı kurulumdaki seçime göre
    /// ayarlar. Kurulum, seçimi ve bir damgayı HKLM\SOFTWARE\PodPanel altına yazar; her kurulumun seçimi bu kullanıcı
    /// için bir kez uygulanır, sonrasında kullanıcının paneldeki seçimi geçerlidir. Damga yazmayan eski kurulumlarda
    /// (1.0.14 ve öncesi) eskisi gibi ilk çalıştırmada bir kez açılır.
    /// </summary>
    private static void OfferAutostart(Settings settings)
    {
        string installed = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PodPanel") + "\\";
        if (Environment.ProcessPath is not string path || !path.StartsWith(installed, StringComparison.OrdinalIgnoreCase)) return;

        bool enable;
        string? stamp = null;
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PodPanel");
            stamp = key?.GetValue("AutostartStamp") as string;
            enable = stamp == null || (key?.GetValue("AutostartChoice") as int? ?? 1) != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            enable = true;
        }

        if (!string.IsNullOrEmpty(stamp))
        {
            if (settings.AutostartStamp == stamp) return;
            settings.AutostartStamp = stamp!;
        }
        else if (settings.AutostartOffered)
        {
            return;
        }
        settings.AutostartOffered = true;
        settings.Save();
        try
        {
            StartupRegistration.Set(enable);
            Log.Write(enable ? "Kurulumdaki seçim: Windows ile başlat açıldı" : "Kurulumdaki seçim: Windows ile başlat kapalı bırakıldı");
        }
        catch (Exception ex)
        {
            Log.Write("Başlangıç ayarı yazılamadı: " + ex.Message);
        }
    }

    private const string ShowSignal = "PodPanel.Signal.Show";
    private const string HideSignal = "PodPanel.Signal.Hide";
    private const string ExitSignal = "PodPanel.Signal.Exit";

    private static void Signal(string name)
    {
        try
        {
            AllowSetForegroundWindow(-1); // çalışan kopya paneli öne getirebilsin
            using var handle = EventWaitHandle.OpenExisting(name);
            handle.Set();
        }
        catch (Exception ex)
        {
            Log.Write("Çalışan kopyaya ulaşılamadı: " + ex.Message);
        }
    }

    /// <summary>İkinci kez çalıştırılan kopyadan gelen aç / gizle / çık komutlarını dinler.</summary>
    private void ListenForSignals()
    {
        var handles = new WaitHandle[]
        {
            new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal),
            new EventWaitHandle(false, EventResetMode.AutoReset, HideSignal),
            new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignal),
        };
        var thread = new Thread(() =>
        {
            while (true)
            {
                int index = WaitHandle.WaitAny(handles);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (index == 0) _tray?.ShowPanel();
                    else if (index == 1) _tray?.HidePanel();
                    else Shutdown();
                }));
                if (index == 2) return;
            }
        })
        {
            IsBackground = true,
            Name = "PodPanel.Signals",
        };
        thread.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    protected override void OnExit(ExitEventArgs e)
    {
        bool wasRunning = _monitor != null;
        _tray?.Dispose();
        _monitor?.Dispose();
        _mutex?.Dispose();
        if (wasRunning) Log.Write("PodPanel kapandı");
        base.OnExit(e);
    }

    private static string? ArgValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
