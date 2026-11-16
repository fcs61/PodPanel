using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PodPanel.Core;

namespace PodPanel;

/// <summary>
/// Görsel doğrulama modu: paneli ekran dışında oluşturur ve PNG olarak kaydeder.
///   PodPanel.exe --snapshot dosya.png [--mock live|normal|mono|game|disconnected|nodevice] [--theme dark|light]
/// "live" gerçek durumu okur; diğerleri arayüz durumlarını denemek için örnek veri kullanır. Adın sonuna "+ayarlar"
/// eklenirse Ayarlar bölümü açık çizilir (ör. --mock normal+ayarlar).
/// </summary>
internal static class Snapshot
{
    public static void Run(App app, string path, string mock, string theme)
    {
        var viewModel = new MainViewModel(null, app.Dispatcher);
        const string detailsSuffix = "+ayarlar";
        if (mock.EndsWith(detailsSuffix, StringComparison.Ordinal))
        {
            mock = mock[..^detailsSuffix.Length];
            viewModel.ShowDetails = true;
        }
        var window = new FlyoutWindow
        {
            DataContext = viewModel,
            StayOpen = true,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -6000,
            Top = -6000,
        };
        Theme.Apply(window.Resources, theme);

        void Render()
        {
            window.Show();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                try
                {
                    Save(window, path, theme);
                }
                catch (Exception ex)
                {
                    Log.Write("Ekran görüntüsü alınamadı: " + ex);
                }
                window.StayOpen = false;
                window.Close();
                app.Shutdown();
            }));
        }

        if (mock != "live")
        {
            viewModel.Apply(Mock(mock));
            Render();
            return;
        }

        var monitor = new PodMonitor(Settings.Load(), "-Snapshot");
        monitor.Start();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            viewModel.Apply(monitor.Current);
            monitor.Dispose();
            Render();
        };
        timer.Start();
    }

    private static void Save(Window window, string path, string theme)
    {
        const double scale = 2.0;
        double width = window.ActualWidth, height = window.ActualHeight;
        int pixelWidth = (int)Math.Ceiling(width * scale), pixelHeight = (int)Math.Ceiling(height * scale);

        var content = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        content.Render(window);

        // Saydam kenarlar görünsün diye masaüstünü andıran düz bir zemin üzerine yerleştir.
        var backdrop = new SolidColorBrush(theme == "light" ? Color.FromRgb(200, 208, 220) : Color.FromRgb(46, 58, 78));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(backdrop, null, new Rect(0, 0, width, height));
            context.DrawImage(content, new Rect(0, 0, width, height));
        }
        var final = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        final.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(final));
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null) Directory.CreateDirectory(directory);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static PodState Mock(string name)
    {
        var mics = new List<MicOption>
        {
            new() { Id = "a", Name = "Mikrofon (Arctis Nova 7P)", Selected = true },
            new() { Id = "b", Name = "Mikrofon (USB masa mikrofonu)" },
        };
        var processes = new List<string> { "cs2", "TslGame" };

        return name switch
        {
            "nodevice" => new PodState { TelemetryAvailable = true, AutoGameProcesses = processes },
            "disconnected" => new PodState
            {
                DeviceFound = true, DeviceName = "Fatih (AirPods Pro)", ModelName = "AirPods Pro 3", Connected = false,
                HasMic = true, DefaultOutputName = "Hoparlör", DefaultMicName = "Mikrofon (Arctis Nova 7P)",
                BatteryKnown = true, BatteryLeft = 80, BatteryRight = 70, BatteryCase = 60, ChargingLeft = true, ChargingRight = true,
                BothInCase = true, BatteryAgeSeconds = 400, TelemetryAvailable = true, Codec = "AAC", SampleRate = 48000, DelayMs = 150,
                LinkFromCache = true, SpatialSupported = true, AutoGameProcesses = processes, Mics = mics, BridgeAvailable = true,
            },
            "normal" => new PodState
            {
                DeviceFound = true, DeviceName = "Fatih (AirPods Pro)", ModelName = "AirPods Pro 3", Connected = true,
                IsDefaultOutput = true, HasMic = true, DefaultMicName = "Headset (A2DP, Fatih (AirPods Pro))", Playing = true,
                BatteryKnown = true, BatteryLeft = 96, BatteryRight = 94, BatteryCase = 71, ChargingCase = true,
                InEarLeft = true, InEarRight = true, BatteryAgeSeconds = 1, Activity = "Ses çalıyor", TelemetryAvailable = true,
                Codec = "AAC", SampleRate = 48000, DelayMs = 60, SpatialSupported = true, SpatialOn = true, SpatialFormat = "Windows Sonic",
                AutoGame = true, AutoGameProcesses = processes, Mics = mics,
                BridgeAvailable = true, ControlOpen = true, BatteryExact = true, Worn = true,
                ListeningMode = PodPanel.Core.Bridge.ListeningMode.NoiseCancellation,
                BridgeMicOn = true, BridgeMicReady = true, BridgeMicIsDefault = true, AudioKbps = 265, JitterMs = 60,
            },
            "mono" => new PodState
            {
                DeviceFound = true, DeviceName = "Fatih (AirPods Pro)", ModelName = "AirPods Pro 3", Connected = true,
                IsDefaultOutput = true, HasMic = true, IsDefaultMic = true, MicUsers = new[] { "cs2", "Discord" }, Playing = true,
                BatteryKnown = true, BatteryLeft = 90, BatteryRight = 80, InEarLeft = true, InEarRight = true, BatteryAgeSeconds = 2,
                Activity = "Arama", TelemetryAvailable = true, Codec = "AAC", SampleRate = 48000, DelayMs = 150, HfpCodec = "mSBC (16 kHz)",
                LinkTroubleCount = 3, SpatialSupported = true, AutoGameProcesses = processes, Mics = mics,
                BridgeAvailable = true, ControlOpen = true, BatteryExact = true, Worn = true,
                ListeningMode = PodPanel.Core.Bridge.ListeningMode.Transparency,
            },
            "game" => new PodState
            {
                DeviceFound = true, DeviceName = "Fatih (AirPods Pro)", ModelName = "AirPods Pro 3", Connected = true,
                IsDefaultOutput = false, DefaultOutputName = "MSI MAG272CQR (NVIDIA High Definition Audio)", HasMic = true, MicDisabled = true,
                DefaultMicName = "Mikrofon (Arctis Nova 7P)", Playing = true,
                BatteryKnown = true, BatteryLeft = 100, BatteryRight = 90, BatteryCase = 50,
                BatteryAgeSeconds = 1, Activity = "Ses çalıyor", TelemetryAvailable = true, Codec = "AAC", SampleRate = 48000,
                DelayMs = 150, SpatialSupported = true, SpatialOn = true, SpatialFormat = "Windows Sonic",
                GameModeManual = false, GameModeActive = true, GameModeReason = "cs2 çalışıyor", LockMic = true, AutoGame = true,
                AutoGameProcesses = processes, Mics = mics, GameMicName = "Mikrofon (Arctis Nova 7P)",
                BridgeAvailable = true, ControlOpen = true, BatteryExact = true, Worn = false,
                ListeningMode = PodPanel.Core.Bridge.ListeningMode.NoiseCancellation,
                BridgeMicOn = true, BridgeMicReady = true, BridgeMicCapturing = true, BridgeMicStreaming = true, BridgeMicIsDefault = true,
                AudioKbps = 256, JitterMs = 60,
            },
            _ => new PodState(),
        };
    }
}
