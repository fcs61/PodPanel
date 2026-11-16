# WPods servisi: yerel değişiklikler

Bu klasör, [WPods](https://github.com/FPNDev/WPods) projesinin yalnızca servis kısmının değiştirilmiş bir kopyasıdır.

- Kaynak: `f02e957d789428b64a6d715410bd71d92aa7b2d5` (v0.3.2), `src/WPods.Service`
- Lisans: GPL-3.0 (bkz. `LICENSE`). PodPanel kurulum paketi bu servisi derlenmiş olarak içerir; paket, lisans metnini
  (`LISANS.txt`) ve bu kaynağı (`kaynak.zip`) birlikte taşır.
- Değişiklik tarihi: 2026-11-16
- Derlenen sürüm etiketi: `0.3.2+f02e957.noupdate.5`

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

Bluetooth paketlerinin iletilmesi, sürücü bağlama ve USB/IP sunucusu kodunda başka değişiklik yoktur.

## Derleme

Proje .NET 10 SDK ister; SDK kullanıcı klasörüne kurulu (`%USERPROFILE%\PodPanel\tools\dotnet10`). Çıktı kendi çalışma zamanını içerir, bu yüzden bilgisayara .NET 10 kurmak gerekmez. Derlenmiş çıktı `kurulum\servis` klasöründe, dosya özetleri `kurulum\servis.sha256` dosyasındadır.

```powershell
& "$env:USERPROFILE\PodPanel\tools\dotnet10\dotnet.exe" publish bridge\src\WPods.Service\WPods.Service.csproj -c Release --self-contained true -o "$env:USERPROFILE\PodPanel\build\bridge-publish" --artifacts-path "$env:USERPROFILE\PodPanel\build\bridge" -p:Version=0.3.2 "-p:InformationalVersion=0.3.2+f02e957.noupdate.5"
```
