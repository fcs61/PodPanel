# PodPanel

AirPods için Windows görev çubuğu paneli. Çekirdek özellikler sürücü gerektirmez; dinleme modu, gecikme ayarı ve
mikrofon köprüsü, köprü servisi kuruluysa çalışır.

## Kurulum

`PodPanel-Kurulum-<sürüm>.exe` tek dosyadır; paneli, köprü servisini ve (kurulu değilse) usbip-win2 sürücüsünü kurar.
Hedef bilgisayarda .NET ya da başka bir şey kurulu olması gerekmez.

Gerekenler: 64 bit Windows 11 (Windows 10 2004 ve sonrası kabul edilir ama denenmedi), **USB üzerinden bağlı** bir
Bluetooth adaptörü (anakarttaki Intel/Realtek/MediaTek adaptörlerin çoğu böyledir) ve yönetici hesabı. Kulaklık
Windows'la önceden eşleştirilmiş olmalıdır.

1. Dosyayı çalıştır. İmzasız olduğu için Windows "Windows kişisel bilgisayarınızı korudu" uyarısını gösterebilir:
   **Ek bilgi > Yine de çalıştır**. Ardından yönetici onayı istenir.
2. Pencere bilgisayarı denetler (Windows sürümü, Bluetooth adaptörü, kurulu sürümler) ve ne yapacağını gösterir.
   "Önce geri yükleme noktası oluştur" kutusu işaretli kalsın.
3. **Kur**. Sıra: geri yükleme noktası, panel, usbip-win2, köprü servisi. usbip-win2 kurulurken USB aygıtlar bir an
   kopabilir; Windows sürücü için onay isterse **Yükle** de.
4. usbip-win2 ilk kez kurulduysa sonunda **Yeniden başlat**. Köprü servisi açılışta Bluetooth adaptörünü devralır;
   panel de kendiliğinden başlar (bildirim alanında, gerekirse gizli simgeler `^` bölümünde).

Bir şey ters giderse:

- Köprü servisi Bluetooth'u devralamazsa kendini geri alır: üç başarısız denemeden sonra adaptörü Windows'un kendi
  sürücüsüne bırakır ve panel "Köprü Bluetooth'u devralamadı" uyarısını **Yeniden dene** düğmesiyle gösterir. Panel
  köprüsüz de çalışır (pil, bağlantı, oyun modu, uzamsal ses).
- Kurulum günlüğü: `C:\ProgramData\PodPanel\kurulum.log`. Servisin günlüğü: `C:\ProgramData\WPods\logs\service.log`.
- Kaldırmak için: Ayarlar > Uygulamalar > Yüklü uygulamalar > **PodPanel** > Kaldır. Kaldırıcı servisi kaldırır,
  Bluetooth'un Windows'un sürücüsüyle geri geldiğini denetler ve istenirse usbip-win2'yi de kaldırır.
- Bluetooth hiç geri gelmezse: Sistem Geri Yükleme ile "PodPanel kurulumu oncesi" noktasına dön.

Kurulum programı her adımı `kurulum\paket\kur.ps1` betiğiyle yapar; kaldırma `kaldir.ps1` ile. İkisi de kurulumdan
sonra `C:\Program Files\PodPanel\kurulum` klasöründe durur ve okunabilir.

## Ne yapar

- AirPods'u tanır; pil (sol / sağ / kutu) ve kulakta durumunu BLE yayınından okur. Köprü servisi varsa bu
  bilgiler kontrol kanalından gelir: yüzde yüzde kesin ve anlık.
- **Dinleme modu:** Şeffaf / Uyarlanabilir / Gürültü Engelleme arasında panelden ya da simgenin sağ tık menüsünden
  geçiş (köprü servisi gerekir). Kulaklık modu yalnızca kulaktayken değiştirir; değilken düğmeler soluk görünür.
- Ses profillerini bağlar ya da keser; ses başka aygıta kaydıysa tek tıkla AirPods'a alır.
- **Gecikme:** kulaklığın ses tamponunu 150 ms'den 90, 60 ya da 30 ms'ye indirir (köprü servisi gerekir). Oyunlarda
  ses daha erken gelir; bağlantı zayıfsa küçük değerlerde ses takılabilir.
- **Mikrofon (köprü):** mikrofon sesini el-serbest profili yerine kontrol kanalından alır ve servisin sanal
  mikrofonuna ("Headset (A2DP, …)") verir; konuşurken ses stereo kalır (köprü servisi ve FFmpeg gerekir).
- **Oyun modu:** varsayılan mikrofonu AirPods'tan uzak tutar, böylece kulaklık stereo kalır. İstenirse AirPods
  mikrofonunu tamamen kilitler ve belirli uygulamalar (ör. `cs2`, `TslGame`) çalışırken kendiliğinden açılır.
- Windows Sonic uzamsal sesi açıp kapatır.
- Bağlantı bilgisini gösterir: kullanılan codec, kulaklığın bildirdiği gecikme, mikrofonu hangi uygulamanın açtığı.

Panel sade tutulur: üstte ad ve tek satır durum, pil, dinleme modu, gecikme ve üç aç/kapa kutusu (Mikrofon, Uzamsal,
Oyun modu). Açıklamalar fare üzerine gelince çıkan ipuçlarındadır. Bir sorun varsa (ses başka aygıtta, ses mono,
köprü çalışmıyor) tek satırlık bir uyarı belirir. Seyrek kullanılan ayarlar ve bağlantı ayrıntıları "Ayarlar"
bağlantısının altındadır.

## Derleme ve çalıştırma

.NET 8 SDK gerekir.

```
dotnet publish src\App\App.csproj -c Release -o "%USERPROFILE%\PodPanel\app"
"%USERPROFILE%\PodPanel\app\PodPanel.exe"
```

Uygulamayı Windows'tan (Başlat, Gezgin ya da kısayol) başlat. Claude masaüstü uygulamasının içinden başlatılan
kopya, ayarlarını uygulamanın özel alanına yazar.

Simge görev çubuğunun bildirim alanında belirir; sol tık paneli açar, sağ tık menüyü. Windows yeni simgeleri
gizli simgeler (^) bölümüne koyabilir; oradan görev çubuğuna sürükleyebilirsin.

Uygulama çalışırken `PodPanel.exe` yeniden çalıştırılırsa panel açılır. `PodPanel.exe --exit` çalışan kopyayı
kapatır (güncelleme öncesinde kullanışlı).

## Gecikme

AirPods, gelen sesi çalmadan önce bir tamponda bekletir ve bu süreyi bağlı cihaza bildirir; varsayılanı 150 ms'dir.
Tampon, kontrol kanalından tek bir komutla küçültülür (kontrol komutu `0x0B`, değer milisaniye). Komutun tanımı
[USBPods](https://github.com/wasdwasd0105/USBPods-Pico2W) projesinin kaynağından; bu kulaklıkta 30, 60, 90 ve 150
denendi ve her birinde kulaklık Windows'a aynı değeri bildirdi. Codec'i değiştirmek tek başına bu süreyi değiştirmez:
AAC, SBC ve Apple'ın AAC-ELD ucu için bildirilen değer aynıdır (150 ms).

Panel seçilen değeri kanal her açıldığında yeniden gönderir ve çıkışta 150'ye geri alır. Bu yalnızca kulaklığın
tamponudur; Windows'un sesi kodlayıp göndermesi için geçen süre ayrıca vardır.

## AirPods mikrofonu (köprü)

Paneldeki "Mikrofon" kutusu açılınca:

1. Windows'ta kulaklığın el-serbest servisi kapatılır (aygıt özelliklerindeki "El-serbest Telefon" kutusuyla aynı
   ayar). Kulaklık, mikrofon akışı sürerken el-serbest bağlantısı başlarsa bağlantıyı düşürdüğü için bu gereklidir.
2. Servis sanal bir USB mikrofon bağlar; panel onu varsayılan mikrofon yapar ve önceki varsayılanı saklar.
3. Bir uygulama bu mikrofondan kayıt alırken panel kulaklıktan mikrofon akışı ister, AAC-ELD sesi FFmpeg ile çözer
   ve 48 kHz mono olarak servise yazar.

Anahtar kapatılınca ya da uygulamadan çıkılınca üçü de geri alınır: önceki varsayılan mikrofon döner, sanal
mikrofon gizlenir, el-serbest servisi yeniden açılır. Kulaklık kulakta değilken mikrofon akışını kendisi keser.

FFmpeg kitaplıkları (`avcodec`, `avutil`, `swresample`) uygulamanın yanındaki `ffmpeg` klasöründen yüklenir.
Kullanılan sürüm 8.1 LGPL paylaşımlı derlemesidir (BtbN/FFmpeg-Builds, ffmpeg.org'un bağlantı verdiği derleyici);
kitaplıklar değiştirilmeden, lisans dosyasıyla birlikte durur. Başka bir klasör için `PODPANEL_FFMPEG` ortam
değişkeni kullanılabilir.

## Tanı aracı

Arayüz olmadan çekirdek servisleri dener:

```
dotnet run --project src\Diag -c Release -- state        # durumu JSON olarak yazar
dotnet run --project src\Diag -c Release -- watch 20     # durum değişikliklerini ve telemetri olaylarını izler
dotnet run --project src\Diag -c Release -- gametest     # oyun modunu açıp kapatır, geri alındığını gösterir
dotnet run --project src\Diag -c Release -- modetest     # dinleme modunu değiştirir, onayı bekler, eski moda döner
dotnet run --project src\Diag -c Release -- mictest 30   # mikrofon köprüsünü 30 sn açar, durumu yazar, geri alır
dotnet run --project src\Diag -c Release -- hfp          # el-serbest servisinin durumu (hfp on|off değiştirir)
dotnet run --project src\Diag -c Release -- jittertest   # gecikme tamponunu 60 ms yapar, bildirilen değeri yazar, geri alır
dotnet run --project src\Diag -c Release -- bitrate 20   # kulaklığa giden sesin ölçülen bit hızı
dotnet run --project src\Diag -c Release -- eldprobe     # her ses ucunun bildirdiği gecikme (panel kapalıyken)
dotnet run --project src\Diag -c Release -- srclatency "değerler=150,60;deneme=6"   # Windows tarafındaki gecikme (motor -> ilk paket)
dotnet run --project src\Diag -c Release -- latency "değerler=150,60"               # akustik geri döngü; kulaklık kulaktayken çalışır
```

Panelin ekran görüntüsü (görsel doğrulama için):

```
"%USERPROFILE%\PodPanel\app\PodPanel.exe" --snapshot panel.png --mock normal --theme dark
```

`--mock`: `live` (gerçek durum), `normal`, `mono`, `game`, `disconnected`, `nodevice`; sonuna `+ayarlar` eklenirse
Ayarlar bölümü açık çizilir.

## Kurulum paketini üretmek

```
powershell -NoProfile -ExecutionPolicy Bypass -File kurulum\paket\paketle.ps1 -Surum 1.0.0 -CalismaZamani 8.0.34
```

Betik paneli kendi .NET çalışma zamanıyla yayımlar, `kurulum\servis` klasöründeki derlenmiş servisi özet listesiyle
doğrular, usbip-win2 kurulum dosyasının özetini denetler, kaynak kodunu `kaynak.zip` olarak ekler, hepsinin özet
listesini (`dosyalar.sha256`) yazar ve tek dosyalık kurulum programını `%USERPROFILE%\PodPanel\dagitim` klasörüne
koyar. `-CalismaZamani` verilmezse SDK'nın bildiği (çoğu zaman eski) .NET 8 yaması eklenir.

Kurulum programı (`src\Setup`) .NET Framework 4.8 için derlenir; kurulum dosyaları içine gömülüdür. Aynı kaynak,
gömülü dosya olmadan `Kaldir.exe` olarak derlenir. Çalışırken dosyalarını `C:\Program Files\PodPanel\_kurulum`
klasörüne açar (yalnızca yöneticiler yazabilir), özetleri doğrular ve işi betiklere bırakır. `-p:Sinama=1` ile
derlenen kopya, pencereyi yönetici olmadan sahte bir kurulumla çizip PNG'ye kaydedebilir (`/sahne`, `/betik`,
`/goruntu`); bu anahtarlar dağıtılan derlemede yoktur.

Kurulum dosyası imzasızdır: başka bilgisayarda SmartScreen uyarısı çıkar ve bazı virüsten koruma programları
bilinmeyen bir servis kurucusu olarak işaretleyebilir.

## Yapı

| Klasör | İçerik |
|---|---|
| `src/Core` | Ses (Core Audio), BLE yayın çözücü, sürücü telemetrisi (ETW), uzamsal ses, oyun modu, durum izleyici, kontrol kanalı istemcisi (`Bridge`) |
| `src/App` | WPF panel ve bildirim alanı simgesi |
| `src/Diag` | Konsol tanı aracı |
| `src/Setup` | Kurulum ve kaldırma penceresi (`Kurulum.exe`, `Kaldir.exe`) |
| `src/simge` | Uygulama simgesi (`kurulum/paket/simge-uret.ps1` üretir) |
| `kurulum/paket` | Kurulum paketi: `kur.ps1`, `kaldir.ps1`, `ortak.ps1`, `paketle.ps1`, lisans notları |
| `bridge` | Kontrol kanalı köprüsü: WPods servisinin güncelleyicisi çıkarılmış kopyası (bkz. `bridge/CHANGES.md`) |
| `kurulum` | Köprü denemesinin aşamalı betikleri (`1-`…`9-`); doğrulanmış usbip-win2 kurulum dosyası ve derlenmiş servis (ikisi depoda yok, bkz. "Depoda olmayan dosyalar") |

## Köprü denemesi (Faz 2)

Bu bölüm, kurulum paketinden önceki aşamalı denemeyi anlatır; kurulum programı aynı adımları tek akışta yapar.

Dinleme modu geçişi için kulaklığın kontrol kanalı gerekir. Windows bu kanalı kullanıcı programlarına açmadığı
için deneme, Bluetooth adaptörünü usbip-win2 üzerinden bir servisin arkasına alır. Aşamalar:

1. `kurulum/1-usbip-kur.ps1`: geri yükleme noktası oluşturur ve yalnızca usbip-win2'yi kurar (yönetici ister).
2. Yeniden başlatmadan sonra `kurulum/denetim.ps1 -Mod karsilastir` aygıt ve sürücü durumunu kurulum öncesiyle
   karşılaştırır (yönetici istemez, hiçbir şeyi değiştirmez).
3. `kurulum/2-servis-kur.ps1`: dosya özetlerini denetler, geri yükleme noktası oluşturur, servisi
   `C:\Program Files\PodPanel\Bridge` klasörüne kurar ve Bluetooth'un sanal adaptör üzerinden geri gelmesini bekler.
   Gelmezse servisi kendisi kaldırır. `kurulum/9-servis-kaldir.ps1` servisi sonradan kaldırır ve adaptörü Windows'un
   kendi sürücüsüne geri verir.
4. Kontrol kanalı tanı aracıyla denenir (`src/Core/Bridge` istemcisi):

```
bin\Diag\Release\PodPanel.Diag.exe bridge                # servisin durumu
bin\Diag\Release\PodPanel.Diag.exe aap 12                # pil, kulak durumu, dinleme modu (ayar değiştirmez)
bin\Diag\Release\PodPanel.Diag.exe mode transparency     # dinleme modunu değiştirir: off|nc|transparency|adaptive
bin\Diag\Release\PodPanel.Diag.exe aaptest               # istemciyi servis olmadan sınar
```

5. Panel kanalı kendiliğinden açar. Kanal kulaklık başına tektir: panel çalışırken `aap` ve `mode` komutları
   "başka bir programda açık" yanıtını alır; bu komutlar için önce paneli kapat (`PodPanel.exe --exit`).

Betiklerin çalıştırılan kopyası `%USERPROFILE%\PodPanel\kurulum` klasöründedir; sonuçlar oradaki `durum`
klasörüne yazılır. (`%LOCALAPPDATA%` kullanılmaz: Claude masaüstü uygulaması paketli çalıştığı için oraya
uygulamanın içinden yazılan dosyalar uygulamanın özel alanına yönlendirilir ve dışarıdan görünmez.) Geri almak için: Ayarlar > Uygulamalar > Yüklü uygulamalar > USBip > Kaldır, ardından
yeniden başlatma.

Ayarlar ve günlük: `%LOCALAPPDATA%\PodPanel` (`settings.json`, `podpanel.log`).

## Kapatma ve uyku

Hızlı Başlangıç açıkken Windows'ta "Kapat" bir hazırda bekletmedir: köprü servisi kapanmaz, uyur ve açılışta kaldığı
yerden sürer. usbip-win2 sanal aygıtları kapanmanın başında çıkarır; servis adaptörü o sırada yeniden takarsa
usbip'in sanal USB denetleyicisinin bağlantı noktası ölü kalır ve açılışta Bluetooth gelmez (Aygıt Yöneticisi'nde
"Bilinmeyen Aygıt", kod 43). Servisin `.noupdate.6` sürümü bunu üç yolla ele alır (bkz. `bridge/CHANGES.md`): Windows
adaptörü kendisi çıkardıysa uyku bildirimi için bekler, uyurken adaptörü takmaz, taktıktan sonra Windows'tan istek
gelmezse usbip denetleyicisini yeniden başlatır.

- `kurulum\4-uyku-duzeltmesi.ps1`: kurulu servisi bu sürüme günceller ve denetleyiciyi yeniden başlatma yolunu sınar.
- `kurulum\bluetooth-onar.ps1`: Bluetooth yine de gelmediyse elle onarır (bozuk aygıt kaydını siler, yetmezse
  denetleyiciyi kapatıp açar). Tam bir **Yeniden başlat** da aynı işi görür; "Kapat" görmez.

## Depoda olmayan dosyalar

Depo yalnızca kaynak kodunu taşır. Kurulum paketini üretmek (`paketle.ps1`) için şu üç ikili girdi gerekir; üçü de
derlenmiş olarak kurulum dosyasının (`PodPanel-Kurulum-<sürüm>.exe`) içinde ve kurulu kopyada da bulunur.

| Yer | Ne | Nasıl elde edilir |
|---|---|---|
| `kurulum\servis\` | Derlenmiş köprü servisi | `bridge/CHANGES.md` içindeki "Derleme" komutu (.NET 10 SDK); çıktıyı bu klasöre kopyala |
| `kurulum\usbip-win2\USBip-0.9.8.1-x64.exe` | usbip-win2 kurulum dosyası | <https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.1/USBip-0.9.8.1-x64.exe>; SHA-256 özeti `paketle.ps1` ve `kur.ps1` içinde yazılıdır, ikisi de denetler |
| `%USERPROFILE%\PodPanel\app\ffmpeg\` | `avcodec-62.dll`, `avutil-60.dll`, `swresample-6.dll`, `LICENSE.txt` | <https://github.com/BtbN/FFmpeg-Builds/releases> içindeki `ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip` (`bin` klasörü ve `LICENSE.txt`) |

Servis yeniden derlendiyse özet listesi de yenilenmelidir (depodaki liste, 1.0.0 paketine giren derlemeye aittir):

```powershell
$kok = (Resolve-Path kurulum\servis).Path
Get-ChildItem $kok -Recurse -File | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash)  $($_.FullName.Substring($kok.Length + 1))" } | Set-Content -Encoding utf8 kurulum\servis.sha256
```

`kurulum\durum\` klasörü de depoda yoktur: denetim betiklerinin o bilgisayara özgü dökümlerini tutar.

## Notlar

- Panelin kendisi yönetici yetkisi istemez (kurulum ister). Codec bilgisi için hesabın "Performance Log Users"
  grubunda (ya da yönetici) olması gerekir; değilse panel bu alanı boş bırakır.
- Kurulum programının kurduğu kopya ilk çalıştığında "Windows ile başlat" seçeneğini bir kez kendisi açar.
- Oyun modu kapatıldığında ya da uygulamadan çıkıldığında mikrofon ayarları önceki haline döner.
- "Windows ile başlat" seçeneği `PodPanel.exe` dosyasının o anki yolunu kaydeder; dosyayı taşırsan seçeneği
  yeniden işaretle.
