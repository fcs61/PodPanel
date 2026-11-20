# WPods servisi: yerel değişiklikler

Bu klasör, [WPods](https://github.com/FPNDev/WPods) projesinin yalnızca servis kısmının değiştirilmiş bir kopyasıdır.

- Kaynak: `f02e957d789428b64a6d715410bd71d92aa7b2d5` (v0.3.2), `src/WPods.Service`
- Lisans: GPL-3.0 (bkz. `LICENSE`). PodPanel kurulum paketi bu servisi derlenmiş olarak içerir; paket, lisans metnini
  (`LISANS.txt`) ve bu kaynağı (`kaynak.zip`) birlikte taşır.
- Değişiklik tarihi: 2026-11-16
- Derlenen sürüm etiketi: `0.3.2+f02e957.noupdate.9`

## Değişiklikler

| Dosya | Değişiklik |
|---|---|
| `Updater.cs` | Silindi. Servis GitHub'a bağlanmaz, kurulum indirmez ve çalıştırmaz. |
| `SessionLauncher.cs` | Silindi. Yalnızca güncelleme sonrası uygulamayı yeniden açmak için kullanılıyordu. |
| `Engine.cs` | Güncelleyici çağrıları kaldırıldı. Klasör koruması `DataFolder.Protect()` üzerinden sürüyor. |
| `DataFolder.cs` | Yeni. `%ProgramData%\WPods` klasörünün erişim kuralları (eskiden güncelleyicinin içindeydi). |
| `Pipes.cs`, `Protocol.cs` | Durum kanalındaki 0x23–0x26 komutları (otomatik güncelleme, denetle, şimdi güncelle, uygulama açıldı) yok sayılır. 0x22 iletisi her zaman "güncelleme yok, otomatik güncelleme kapalı" der. |
| `Log.cs` | Günlük sınırı 500 MB yerine 16 MB. Paket başına izleme yalnızca `HKLM\SOFTWARE\WPods\Service\VerboseLog = 1` iken yazılır. |
| `HciProxy.cs`, `RadioLink.cs` | Paket başına günlük satırları `Log.Verbose` koşuluna bağlandı. Anahtar taşıyan komut listesine 0x042E ve 0x0C11 eklendi. |
| `MicDevice.cs`, `UsbIp.cs` | (2026-11-17, `.noupdate.2`) Sanal mikrofonun eşzamanlı aktarım yanıtlarındaki `StartFrame`, usbip-win2 0.9.8.1 kuruluyken veri yolu saatinden (açılıştan beri milisaniye) hesaplanır. Özgün kod isteğin değerini geri gönderir; 0.9.8.1 veri yolu zamanını başka bir saatten bildirdiği için Windows Ses Kaydedici gibi akış konumuna güvenen kaydediciler sesin neredeyse tamamını atıyordu. `IsoStartFrameClock` kayıt değeri (0/1) seçimi zorlar. |
| `HciProxy.cs`, `Engine.cs`, `Pipes.cs`, `Protocol.cs` | (2026-11-17, `.noupdate.3`) Windows'un her bağlantıda gönderdiği L2CAP paketlerinin uzunluğu ve kanal numarası sayılır; en yoğun kanalın bayt ve paket sayısı 2 saniyede bir durum kanalından (ileti 0x28) bildirilir. Panel bundan sesin gerçek bit hızını hesaplar. Yalnızca L2CAP başlığı (uzunluk, kanal) okunur; içerik okunmaz ve hiçbir şey günlüğe yazılmaz. |
| `HciProxy.cs` | (2026-11-17, `.noupdate.4`) AirPods'un Windows'a verdiği AVDTP yetenek yanıtları (bir akış ucunun desteklediği codec listesi) geçerken kopyalanır ve servis günlüğüne yazılır; bağlantı başına en çok 12 yanıt. Ses ya da kullanıcı verisi içermez; paket Windows'a değiştirilmeden iletilir. |
| `HciProxy.cs`, `Pipes.cs`, `Protocol.cs` | (2026-11-17, `.noupdate.5`) İstemci, kulaklık kanalını AAP (0x1001) yerine AVDTP işaretleşmesine (0x0019) açtırabilir: ilk ileti 12 bayt olur ve sonunda PSM taşır. Başka PSM reddedilir; bağlantı başına yine tek kanal vardır. Kulaklığın düşük gecikmeli codec ucunu yoklamak için eklendi. |

| `Engine.cs`, `RadioLink.cs`, `RadioDriver.cs` | (2026-11-17, `.noupdate.6`) Kapatma ve uyku. Hızlı Başlangıç açıkken "Kapat" bir hazırda bekletmedir ve servis çalışmaya devam eder; usbip-win2 sanal aygıtları kapanmanın başında, servise uyku bildirimi gelmeden saniyeler önce çıkarır. Özgün kod adaptörü 2 saniye sonra yeniden takıyordu; kapanma sırasında takılan adaptör usbip denetleyicisinin bağlantı noktasını ölü bırakıyor, açılışta Bluetooth gelmiyordu (Aygıt Yöneticisi'nde "Bilinmeyen Aygıt", kod 43). Üç değişiklik: (1) Windows bağlantıyı kendisi kapattıysa yeniden takmadan önce 30 saniyeye kadar uyku bildirimi beklenir; (2) uyku bildirimi geldiyse uyanana kadar adaptör takılmaz; (3) takıldıktan sonra 10 saniye içinde Windows'tan hiç istek gelmezse oturum sonlandırılır ve bir sonraki takmadan önce usbip'in sanal USB denetleyicisi kapatılıp açılır (`RadioDriver.RestartVhci`). Yönetici, `HKLM\SOFTWARE\WPods\Service\RestartVhciOnce = 1` değeriyle bu yeniden başlatmayı bir kez isteyebilir (sınama ve elle onarım için). `Directory.Build.props`: sürüm etiketine git işleme özeti eklenmez. |

| `Engine.cs`, `Program.cs`, `RadioDriver.cs`, `Sessions.cs` | (2026-11-20, `.noupdate.7`) Hızlı Başlangıç'la kapatıp açınca Bluetooth'un yaklaşık 20 saniye gelmemesi ve USB çıkarma/takma sesleri. Kapatırken Windows kullanıcıları oturumdan çıkarıp hazırda bekler, servis çalışmaya devam eder; gerçek adaptör bu sırada durumunu kaybeder ve açılışta Windows'un Bluetooth sürücüsü eski sanal adaptörde hata verir (BTHUSB 17). Artık Windows kimse oturum açık değilken uyuyorsa (son 2 dakikada oturum kapandıysa ya da açık oturum yoksa) sanal adaptör ve mikrofon uykudan önce çıkarılır, açılışta oturum açılmadan yeniden takılır. Oturum açıkken normal uyku eskisi gibidir. Uyanınca Windows'un sanal adaptörde hata bildirmesi (aygıt sorun kodu) de izlenir; bir saniye sürerse adaptör hemen yeniden takılır. Servis günlüğü açılışta tam sürüm etiketini yazar. |
| `Engine.cs` | (2026-11-20, `.noupdate.8`) Hızlı Başlangıç'la açılışta usbip-win2'nin sanal denetleyicisi, yeniden başlatılmadan takılan adaptörü çalıştırmıyordu (10 saniye beklenip denetleyici yeniden başlatılıyor, Bluetooth yaklaşık 20 saniyede geliyordu). Kapanışta adaptör çıkarıldıysa, açılışta denetleyici ilk takmadan önce hemen yeniden başlatılır. |

| `UsbIp.cs`, `Engine.cs`, `Program.cs` | (2026-11-20, `.noupdate.9`) Kapatıp açınca Bluetooth'un gidip gelmesinin asıl nedeni. usbip-win2'nin kurulumu "USBip Detach All On Reboot Or Shutdown" adlı bir zamanlanmış görev ekler; kapatma istendiğinde (Sistem günlüğü: User32 1074 ya da Kernel-Power 109) `usbip detach --all=closeonly` çalıştırır. Bu, sanal aygıtların bağlantısını kapatır ama aygıtı takılı bırakır; Hızlı Başlangıç'la kapanış bir hazırda bekletme olduğu için açılışta ölü adaptör yerinde durur. ETW izlemesiyle bulundu: bağlantı kapanmadan 18 ms önce Görev Zamanlayıcı `usbip.exe` başlatıyordu. Servis başlarken bu görevi devre dışı bırakır, kaldırılırken yeniden açar (servis durdurulurken aygıtlarını zaten kendisi çıkarır). `.noupdate.7` ve `.8`'deki "kapanışta adaptörü çıkar, açılışta denetleyiciyi yeniden başlat" davranışı kaldırıldı: adaptör hazırda bekletme boyunca takılı kalır. Önceki sürümlerin beklemeleri ve denetleyici onarımı yedek olarak durur. |

Bluetooth paketlerinin iletilmesi, sürücü bağlama ve USB/IP sunucusu kodunda başka değişiklik yoktur.

## Derleme

Proje .NET 10 SDK ister; SDK kullanıcı klasörüne kurulu (`%USERPROFILE%\PodPanel\tools\dotnet10`). Çıktı kendi çalışma zamanını içerir, bu yüzden bilgisayara .NET 10 kurmak gerekmez. Derlenmiş çıktı `kurulum\servis` klasöründe, dosya özetleri `kurulum\servis.sha256` dosyasındadır.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File kurulum\paket\servis-derle.ps1
```

Betik `dotnet publish` komutunu sürüm etiketiyle (`0.3.2+f02e957.noupdate.9`; betiğin başında yazılıdır) çalıştırır, çıktıyı `kurulum\servis` klasörüne koyar ve özet listesini yeniler. SDK'yı önce `%USERPROFILE%\PodPanel\tools\dotnet10` klasöründe, yoksa PATH'te arar. GitHub'daki otomatik derleme de aynı betiği kullanır.
