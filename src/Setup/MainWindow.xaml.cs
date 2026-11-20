using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace PodPanel.Setup;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
#if SINAMA
        // Gömülü dosyaları verilen klasöre açıp çıkar (açma kodunu yönetici olmadan sınamak için).
        if (args.Length == 2 && args[0] == "/ac")
        {
            Payload.Extract(args[1], _ => true, null);
            return 0;
        }
        // Kendini silme komutunu verilen dosya ve klasörle çalıştırıp çıkar (kaldırmanın son adımını sınamak için).
        if (args.Length == 3 && args[0] == "/sil")
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"), MainWindow.SelfRemovalCommand(args[1], args[2]))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.SystemDirectory,
            })?.Dispose();
            return 0;
        }
#endif
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow(args);
        app.Run(window);
        return window.ExitCode;
    }
}

/// <summary>Denetim sonucundaki bir satır.</summary>
public sealed class Finding
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

public enum StepState { Pending, Active, Done, Failed }

/// <summary>İlerleme listesindeki bir adım.</summary>
public sealed class StepItem : INotifyPropertyChanged
{
    private static readonly Brush Accent = Frozen(0x0A, 0x63, 0xD6);
    private static readonly Brush Good = Frozen(0x0F, 0x7B, 0x0F);
    private static readonly Brush Bad = Frozen(0xC4, 0x2B, 0x1C);
    private static readonly Brush Dim = Frozen(0xB4, 0xB4, 0xB4);
    private static readonly Brush Ink = Frozen(0x1B, 0x1B, 0x1B);
    private static readonly Brush Muted = Frozen(0x8A, 0x8A, 0x8A);

    private StepState _state;

    public StepItem(string text) => Text = text;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Text { get; }

    public StepState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            foreach (string name in new[] { nameof(Glyph), nameof(Brush), nameof(TextBrush), nameof(Weight) })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    public string Glyph => _state switch
    {
        StepState.Done => "\uE73E",
        StepState.Failed => "\uE711",
        StepState.Active => "\uEA3B",
        _ => "\uEA3A",
    };

    public Brush Brush => _state switch
    {
        StepState.Done => Good,
        StepState.Failed => Bad,
        StepState.Active => Accent,
        _ => Dim,
    };

    public Brush TextBrush => _state == StepState.Pending ? Muted : Ink;

    public FontWeight Weight => _state == StepState.Active ? FontWeights.SemiBold : FontWeights.Normal;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Kurulum ve kaldırma penceresi. İçinde kurulum dosyaları gömülüyse (Kurulum.exe) kurar ya da günceller; gömülü dosya
/// yoksa (Kaldir.exe) yanındaki kurulumu kaldırır. İşin kendisini kurulum\kur.ps1 ve kurulum\kaldir.ps1 betikleri yapar;
/// pencere onların çıktısını gösterir.
/// </summary>
public partial class MainWindow : Window
{
    private enum Stage { Checking, Ready, Running, Done }

    /// <summary>kur.ps1'in "panel kuruldu, köprü kurulamadı" sonucu.</summary>
    private const int PartialCode = 100;

    private static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"PodPanel\kurulum.log");

    private readonly bool _install;
    private readonly string _root;
    private readonly string _stage;
    private readonly ObservableCollection<StepItem> _steps = new ObservableCollection<StepItem>();
    private readonly ObservableCollection<Finding> _findings = new ObservableCollection<Finding>();
    private readonly List<string> _blockers = new List<string>();
    private readonly List<string> _warnings = new List<string>();
    private readonly StringBuilder _log = new StringBuilder();
    private Stage _state = Stage.Checking;
    private ScriptResult? _result;
    private bool _createdRoot;
    private bool _alreadyInstalled;
    // Denetimden: bu kurulumda geri yükleme noktası gerekiyor mu, ve Sistem Koruması kapalı olduğu için oluşturulamaz mı.
    private bool _restoreNeeded = true;
    private bool _restoreUnavailable;
    private double _lastProgress;

    // Yalnızca deneme derlemesinde (-p:Sinama=1) doldurulur; dağıtılan derlemede hep boştur.
#pragma warning disable CS0169, CS0649
    private readonly string? _testStage;
    private readonly string? _testScript;
    private readonly string? _testShot;
    private readonly int _testShotDelay;
#pragma warning restore CS0169, CS0649

    public int ExitCode { get; private set; }

    private bool Testing => _testStage != null;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        bool install = Payload.Exists;
#if SINAMA
        for (int i = 0; i < args.Length; i++)
        {
            string? next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "/sahne": _testStage = next; break;
                case "/betik": _testScript = next; break;
                case "/goruntu": _testShot = next; break;
                case "/bekle": _testShotDelay = int.Parse(next ?? "0"); break;
                case "/kur": install = true; break;
                case "/kaldir": install = false; break;
            }
        }
#endif
        _install = install;
        string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        _root = _install ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PodPanel") : exeDir;
        _stage = _testStage ?? (_install ? Path.Combine(_root, "_kurulum") : _root);

        StepList.ItemsSource = _steps;
        FindingList.ItemsSource = _findings;
        if (_install)
        {
            ReadyIntro.Text = "PodPanel, köprü servisi ve (kurulu değilse) usbip-win2 sürücüsü kurulur. Köprü servisi Bluetooth adaptörünü kendi "
                + "üzerinden çalıştırır; dinleme modu, gecikme ayarı ve AirPods mikrofonu bu sayede çalışır.";
            OptionBox.Content = "Önce geri yükleme noktası oluştur (önerilir)";
            ReadyNote.Text = "Kurulum sırasında Bluetooth birkaç saniyeliğine kesilebilir. usbip-win2 ilk kez kuruluyorsa sonunda bilgisayarın "
                + "yeniden başlatılması gerekir.";
        }
        else
        {
            Title = "PodPanel'i Kaldır";
            HeaderSub.Text = "Kaldırma";
            ReadyIntro.Text = "PodPanel ve köprü servisi kaldırılır; Bluetooth adaptörü Windows'un kendi sürücüsüne geri verilir.";
            OptionBox.Content = "usbip-win2 sürücüsünü de kaldır";
            OptionBox.IsChecked = false;
            ReadyNote.Text = "Kaldırma sırasında Bluetooth birkaç saniyeliğine kesilir. Kişisel ayarların silinmez.";
            PrimaryButton.Content = "Kaldır";
        }
        Loaded += async (_, _) => await CheckAsync();
        Closing += OnClosing;
    }

    private string ScriptPath(string name) => Path.Combine(_stage, "kurulum", name);

    // ---- 1. Denetim ----

    private async Task CheckAsync()
    {
        try
        {
            if (_install && !Testing)
            {
                // Dosyalar Program Files altına açılır: orayı yalnızca yöneticiler değiştirebilir.
                _createdRoot = !Directory.Exists(_root);
                if (Directory.Exists(_stage)) Directory.Delete(_stage, true);
                Directory.CreateDirectory(_stage);
                await Task.Run(() => Payload.Extract(_stage,
                    name => name.StartsWith("kurulum\\", StringComparison.OrdinalIgnoreCase) || name.Equals("surum.txt", StringComparison.OrdinalIgnoreCase), null));
            }
            if (_install)
            {
                string[] version = File.ReadAllLines(Path.Combine(_stage, "surum.txt"), Encoding.UTF8);
                HeaderSub.Text = "Sürüm " + version[0].Trim();
                if (version.Length >= 3 && version[2].Trim() == "koprusuz")
                {
                    ReadyIntro.Text = "PodPanel paneli kurulur. Bu sürüm köprü servisi ve sürücü kurmaz, Bluetooth'a dokunmaz: pil, bağlantı, "
                        + "oyun modu ve uzamsal ses çalışır; dinleme modu, gecikme ayarı ve köprü mikrofonu yoktur.";
                    ReadyNote.Text = "";
                }
            }
            string script = ScriptPath(_install ? "kur.ps1" : "kaldir.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("Kurulum betikleri bulunamadı: " + script);

            var environment = new Dictionary<string, string> { ["PODPANEL_KAYNAK"] = _stage, ["PODPANEL_MOD"] = "denetim" };
            ScriptResult result = await ScriptRunner.RunAsync(script, environment, line => Dispatcher.BeginInvoke(new Action(() => OnCheckLine(line))));
            if (!result.Reported || result.Code != 0) _blockers.Add("Denetim çalıştırılamadı. " + result.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            _blockers.Add("Kurulum hazırlanamadı: " + e.Message);
        }
        ShowReady();
#if SINAMA
        if (_testShot != null && _testScript == null)
        {
            await Task.Delay(300);
            SaveShot(_testShot);
            Close();
        }
        else if (_testScript != null)
        {
            await RunAsync();
        }
#endif
    }

    private void OnCheckLine(string line)
    {
        if (!line.StartsWith("@@BILGI ", StringComparison.Ordinal))
        {
            if (!line.StartsWith("@@", StringComparison.Ordinal)) _log.AppendLine(line);
            return;
        }
        string[] parts = line.Split(new[] { ' ' }, 3);
        if (parts.Length < 3) return;
        string key = parts[1], value = parts[2];
        switch (key)
        {
            case "engel": _blockers.Add(value); break;
            case "uyari": _warnings.Add(value); break;
            case "windows": AddFinding("Windows", value); break;
            case "bluetooth": AddFinding("Bluetooth", value == "bulunamadı" ? "Bulunamadı" : value); break;
            case "kurulu":
                _alreadyInstalled = value != "yok";
                AddFinding("PodPanel", value == "yok" ? "Kurulu değil" : value == "yalnızca köprü servisi" ? "Yalnızca köprü servisi kurulu"
                    : value == "bilinmiyor" ? "Kurulu" : value + " kurulu");
                break;
            case "servis": AddFinding("Köprü servisi", Capitalize(value)); break;
            case "denetim": AddFinding("Uygulama Denetimi", Capitalize(value)); break;
            case "geriyukleme":
                _restoreNeeded = value != "gerekmez";
                _restoreUnavailable = value == "kapalı";
                break;
            case "usbip": AddFinding("usbip-win2", value == "yok" ? "Kurulu değil" : Capitalize(value)); break;
            case "usbipbizim": OptionBox.IsChecked = value == "1"; break;
        }
    }

    private void AddFinding(string label, string value) => _findings.Add(new Finding { Label = label, Value = value });

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], System.Globalization.CultureInfo.GetCultureInfo("tr-TR")) + text.Substring(1);

    private void ShowReady()
    {
        _state = Stage.Ready;
        ReadyWait.Visibility = _findings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_findings.Count == 0) ReadyWait.Text = "Denetim sonucu alınamadı.";
        if (_blockers.Count > 0 || _warnings.Count > 0)
        {
            ReadyProblems.Text = string.Join("\n", _blockers.Count > 0 ? _blockers : _warnings);
            ReadyProblems.Foreground = (Brush)FindResource(_blockers.Count > 0 ? "Bad" : "Warn");
            ReadyProblems.Visibility = Visibility.Visible;
        }
        if (!_install)
        {
            // usbip-win2 kurulu değilse seçenek anlamsız.
            bool usbip = false;
            foreach (Finding finding in _findings) usbip |= finding.Label == "usbip-win2" && finding.Value == "Kurulu";
            OptionBox.Visibility = usbip ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            if (_alreadyInstalled) PrimaryButton.Content = "Güncelle";
            if (!_restoreNeeded)
            {
                // Sürücü ve servis değişmeyecek: geri yükleme noktası seçeneği anlamsız.
                OptionBox.IsChecked = false;
                OptionBox.Visibility = Visibility.Collapsed;
                ReadyNote.Text = "Bu kurulumda sürücülere, köprü servisine ve Bluetooth'a dokunulmayacak.";
            }
        }
        PrimaryButton.IsEnabled = _blockers.Count == 0;
    }

    // ---- 2. Kurulum ya da kaldırma ----

    private async Task RunAsync()
    {
        _state = Stage.Running;
        ReadyPage.Visibility = Visibility.Collapsed;
        RunPage.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = false;
        SecondaryButton.IsEnabled = false;
        _steps.Clear();
        string[] names = _install
            ? new[] { "Dosyalar açılıyor", "Denetimler", "Geri yükleme noktası", "Panel dosyaları", "usbip-win2 sürücüsü", "Köprü servisi" }
            : new[] { "Panel kapatılıyor", "Köprü servisi kaldırılıyor", "usbip-win2 sürücüsü", "Dosyalar siliniyor" };
        foreach (string name in names) _steps.Add(new StepItem(name));
        SetStep(0);
#if SINAMA
        if (_testShot != null && _testShotDelay > 0)
        {
            _ = Task.Delay(_testShotDelay).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
            {
                SaveShot(_testShot);
                _state = Stage.Done;
                Close();
            })));
        }
#endif
        if (_install && !Testing)
        {
            StatusLine.Text = "Dosyalar açılıyor…";
            try
            {
                await Task.Run(() => Payload.Extract(_stage, _ => true, ReportProgress));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                Finish(new ScriptResult { Code = 1, Message = "Kurulum dosyaları açılamadı: " + e.Message, Reported = true });
                return;
            }
        }
        Progress.IsIndeterminate = true;
        var environment = new Dictionary<string, string>
        {
            ["PODPANEL_KAYNAK"] = _stage,
            ["PODPANEL_MOD"] = _install ? "kur" : "kaldir",
            ["PODPANEL_GERIYUKLEME"] = OptionBox.IsChecked == true && OptionBox.Visibility == Visibility.Visible ? "1" : "0",
            ["PODPANEL_USBIP_KALDIR"] = OptionBox.IsChecked == true && OptionBox.Visibility == Visibility.Visible ? "1" : "0",
        };
        string script = _testScript ?? ScriptPath(_install ? "kur.ps1" : "kaldir.ps1");
        ScriptResult result = await ScriptRunner.RunAsync(script, environment, line => Dispatcher.BeginInvoke(new Action(() => OnRunLine(line))));
        Finish(result);
    }

    private void ReportProgress(double value)
    {
        if (value < 1 && value - _lastProgress < 0.005) return;
        _lastProgress = value;
        Dispatcher.BeginInvoke(new Action(() => Progress.Value = value));
    }

    private void OnRunLine(string line)
    {
        if (line.StartsWith("@@ADIM ", StringComparison.Ordinal))
        {
            string[] parts = line.Split(new[] { ' ' }, 4);
            // Kurulumda ilk adım (dosyaları açmak) bu programındır; betiğin adımları ondan sonra gelir.
            if (parts.Length >= 3 && int.TryParse(parts[1], out int number)) SetStep(number - 1 + (_install ? 1 : 0));
            return;
        }
        if (line.StartsWith("@@", StringComparison.Ordinal)) return;
        _log.AppendLine(line);
        if (LogBox.Visibility == Visibility.Visible)
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }
        string shown = line.Trim();
        if (shown.Length > 0) StatusLine.Text = shown;
    }

    private void SetStep(int index)
    {
        for (int i = 0; i < _steps.Count; i++)
        {
            _steps[i].State = i < index ? StepState.Done : i == index ? StepState.Active : StepState.Pending;
        }
    }

    // ---- 3. Sonuç ----

    private void Finish(ScriptResult result)
    {
        _result = result;
        _state = Stage.Done;
        bool ok = result.Reported && result.Code == 0;
        bool partial = _install && result.Reported && result.Code == PartialCode;
        ExitCode = ok ? 0 : result.Code == 0 ? 1 : result.Code;
        foreach (StepItem step in _steps)
        {
            if (ok) step.State = StepState.Done;
            else if (step.State == StepState.Active) step.State = StepState.Failed;
        }
        RunPage.Visibility = Visibility.Collapsed;
        DonePage.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = true;
        SecondaryButton.IsEnabled = true;
        PrimaryButton.Content = "Kapat";
        SecondaryButton.Visibility = Visibility.Collapsed;
        LogButton.Visibility = Visibility.Visible;

        DoneGlyph.Text = ok ? "\uE73E" : "\uE7BA";
        DoneGlyph.Foreground = (Brush)FindResource(ok ? "Good" : partial ? "Warn" : "Bad");
        if (partial)
        {
            // Panel kullanılabilir durumda: başlatılır, yalnızca köprü eksik.
            DoneTitle.Text = "Panel kuruldu, köprü kurulamadı";
            // Betik, iletinin "||" sonrasını ek açıklama olarak gönderir (ör. köprünün nasıl kurulabileceği).
            int split = result.Message.IndexOf("||", StringComparison.Ordinal);
            DoneText.Text = split < 0 ? result.Message : result.Message.Substring(0, split).Trim();
            DoneNote.Text = split < 0 ? "Ayrıntılar günlükte: " + LogPath : result.Message.Substring(split + 2).Trim();
            if (result.Restart) OfferRestart();
            StartPanel();
        }
        else if (!ok)
        {
            DoneTitle.Text = _install ? "Kurulum tamamlanamadı" : "Kaldırma tamamlanamadı";
            DoneText.Text = result.Message;
            DoneNote.Text = "Ayrıntılar günlükte: " + LogPath;
            if (result.Restart) OfferRestart();
        }
        else if (_install)
        {
            DoneTitle.Text = "Kurulum tamamlandı";
            if (result.Restart)
            {
                DoneText.Text = "Köprünün devreye girmesi için bilgisayarı yeniden başlat. Açılışta PodPanel kendiliğinden başlar.";
                DoneNote.Text = "PodPanel görev çubuğunun sağındaki bildirim alanında durur. Simgeyi göremezsen gizli simgeler (^) bölümüne bak; "
                    + "oradan görev çubuğuna sürükleyebilirsin.";
                OfferRestart();
            }
            else
            {
                DoneText.Text = "PodPanel çalışıyor: görev çubuğunun sağındaki bildirim alanında kulaklık simgesine tıkla.";
                DoneNote.Text = "Simgeyi göremezsen gizli simgeler (^) bölümüne bak; oradan görev çubuğuna sürükleyebilirsin.";
            }
            StartPanel();
        }
        else
        {
            DoneTitle.Text = "PodPanel kaldırıldı";
            DoneText.Text = result.Restart
                ? "usbip-win2 sürücüsünün tamamen kalkması için bilgisayarı yeniden başlat."
                : "Bluetooth adaptörü Windows'un kendi sürücüsüyle çalışıyor.";
            DoneNote.Text = "";
            if (result.Restart) OfferRestart();
        }
#if SINAMA
        if (_testShot != null && _testShotDelay == 0)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
            {
                SaveShot(_testShot);
                Close();
            }));
        }
#endif
    }

    private void OfferRestart()
    {
        PrimaryButton.Content = "Yeniden başlat";
        SecondaryButton.Content = "Sonra";
        SecondaryButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Paneli yönetici yetkisi olmadan, oturumdaki kullanıcı olarak başlatır (Gezgin üzerinden). İlk çalıştığında
    /// "Windows ile başlat" ayarını kendisi açar.
    /// </summary>
    private void StartPanel()
    {
        if (Testing) return;
        string exe = Path.Combine(_root, @"App\PodPanel.exe");
        if (!File.Exists(exe)) return;
        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer, "\"" + exe + "\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            _log.AppendLine("Panel başlatılamadı: " + e.Message);
        }
    }

    // ---- Düğmeler ----

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (_state == Stage.Ready)
        {
            if (_install && _restoreUnavailable && OptionBox.IsChecked == true && !Testing)
            {
                // Sistem Koruması kapalıyken nokta oluşturulamaz; kurulum yarıda durmasın diye önce sorulur.
                MessageBoxResult answer = MessageBox.Show(this,
                    "Sistem Koruması kapalı olduğu için geri yükleme noktası oluşturulamaz." + Environment.NewLine + Environment.NewLine
                    + "Geri yükleme noktası olmadan kurulsun mu?" + Environment.NewLine + Environment.NewLine
                    + "Hayır dersen kurulum başlamaz; Sistem Koruması'nı açıp yeniden deneyebilirsin.",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
                OptionBox.IsChecked = false;
            }
            await RunAsync();
        }
        else if (_state == Stage.Done)
        {
            if (_result is { Restart: true } && !Testing)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    MessageBox.Show(this, "Yeniden başlatılamadı: " + ex.Message + "\nBilgisayarı Başlat menüsünden yeniden başlat.", Title);
                }
            }
            Close();
        }
    }

    private void OnSecondary(object sender, RoutedEventArgs e) => Close();

    private void OnToggleDetails(object sender, RoutedEventArgs e)
    {
        bool show = LogBox.Visibility != Visibility.Visible;
        LogBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Content = show ? "Ayrıntıları gizle" : "Ayrıntılar";
        if (show)
        {
            LogBox.Text = _log.ToString();
            LogBox.ScrollToEnd();
        }
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = LogPath;
            if (!File.Exists(path))
            {
                // Günlük dosyası yazılamadıysa penceredeki kayıt geçici bir dosyada gösterilir.
                path = Path.Combine(Path.GetTempPath(), "PodPanel-kurulum.txt");
                File.WriteAllText(path, _log.ToString(), Encoding.UTF8);
            }
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + path + "\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, "Günlük açılamadı: " + ex.Message, Title);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_state == Stage.Running)
        {
            // Sürücü ve servis işlemleri yarıda kesilmemeli.
            e.Cancel = true;
            return;
        }
        if (Testing) return;
        try
        {
            if (_install)
            {
                if (Directory.Exists(_stage)) Directory.Delete(_stage, true);
                // Kurulumdan vazgeçildiyse bu programın açtığı boş klasör de kalmasın.
                if (_createdRoot && Directory.Exists(_root) && Directory.GetFileSystemEntries(_root).Length == 0) Directory.Delete(_root);
            }
            else if (_result is { Reported: true, Code: 0 })
            {
                RemoveSelfAfterExit();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temizlik yapılamadıysa kurulum bundan etkilenmez; klasör bir sonraki çalıştırmada silinir.
        }
    }

    /// <summary>
    /// Kaldırmadan sonra bu programın kendi dosyasını ve boş kalan kurulum klasörünü siler: çalışan program kendini
    /// silemediği için işi, program kapandıktan sonra çalışan kısa bir komuta bırakır. Klasör boş değilse silinmez.
    /// </summary>
    private void RemoveSelfAfterExit()
    {
        string exe = Assembly.GetExecutingAssembly().Location;
        // Dosya Windows'un "yeniden başlatmada sil" listesine YAZILMAZ: o kayıt, kaldırmadan sonra yeniden kurulan
        // sürümün kaldırıcısını ilk yeniden başlatmada siliyordu (1.0.3 ve 1.0.4'te böyle oldu). Kaldırmanın ardından
        // hemen yeniden başlatılırsa bu küçük dosya kalabilir; sonraki kurulum üzerine yazar.
        string command = SelfRemovalCommand(exe, _root);
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        })?.Dispose();
    }

    /// <summary>
    /// cmd.exe için: birkaç saniye bekle, programın dosyasını sil, klasör boşsa onu da sil. Tırnaklı yollar taşıdığı
    /// için komutun tamamı bir kez daha tırnağa alınır; cmd ilk ve son tırnağı atar.
    /// </summary>
    internal static string SelfRemovalCommand(string exe, string folder) =>
        "/c \"ping -n 6 127.0.0.1 >nul & del /f /q \"" + exe + "\" & rmdir \"" + folder + "\"\"";

#if SINAMA
    /// <summary>Pencerenin içeriğini PNG olarak kaydeder (görsel deneme).</summary>
    private void SaveShot(string path)
    {
        var root = (FrameworkElement)Content;
        const double scale = 2;
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            var area = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            context.DrawRectangle(Brushes.White, null, area);
            context.DrawRectangle(new VisualBrush(root), null, area);
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (FileStream stream = File.Create(path)) encoder.Save(stream);
    }
#endif
}
