# Son Doğrulama

**Durum**

| Kabul | Sonuç |
| --- | --- |
| **ERP kabulü** (sipariş, satın alma, stok, destek, yetki, idempotency, yığın) | **Kapıların tamamı son kaynak parmak izinde geçti** (§1) |
| **Canlı AI kabulü** (gerçek bir dil modeliyle) | **YOK — yerel model yok; doğrulanamadı** (§7) |
| Uzak CI | Çalıştırılmadı; depo hiç push edilmedi |

Aşağıda yalnız gerçekten çalıştırılan komutlar ve gerçek sonuçları vardır. "Kapı" sözü yalnız `scripts/verify.ps1` üzerinden, kaynak parmak iziyle kaydedilen koşular için kullanılır. Kapılar bellek kısıtı nedeniyle **ayrı çağrılarla ve sırayla** koşuldu; tek çağrıda bütün kapıların koştuğu bir sonuç yoktur. Başarısız ve kesilen koşular §3'te ayrıca yazılıdır.

Ortam: Windows 11 Home, 15,7 GB RAM, Docker Desktop 29.8.2 (VM sınırı 7,59 GiB), .NET SDK 10.0.401, Node 24.18.0, SQL Server 2022 CU27 Developer (container), Chrome (Playwright, tek işçi). Saatler UTC; tarih 2026-10-11 (yerel saatle 12 Ekim 00:39–01:04).

## 1. Son kaynak durumu: kapı sonuçları

Kaynak parmak izi: `source-fingerprint sha256:1cd663dcc5ec6be55b9054bf746abfefb8835bc88d711c6b0725944f9220c6a0 files=316`
(parmak izi `docs/`, `artifacts/`, `design/` ve kökteki README/NOTICE/PRODUCT/DESIGN dosyalarını kapsamaz).

| Kapı | Sonuç | Kapı çıkış kodu | Geçen / kalan / atlanan | Çağrı | Başlangıç | Süre |
| --- | --- | --- | --- | --- | --- | --- |
| `scripts` | **geçti** | 0 | 42 / 0 / 0 (21 test × tr-TR, en-US) | `20261011T215637Z-dwkl` | 21:56:37 | 0,4 sn |
| `restore` | **geçti** | 0 | – | aynı | 21:56:37 | 1,5 sn |
| `build` | **geçti** | 0 | Release, uyarılar hata | aynı | 21:56:39 | 5,4 sn |
| `unit` | **geçti** | 0 | 2036 / 0 / 0 | aynı | 21:56:45 | 8,4 sn |
| `scans` | **geçti** | 0 | gizli bilgi 0 bulgu (374 dosya, öz denetim geçti); yazarlık 0; NuGet tavsiyesi yok; `npm audit` (çalışma zamanı, yüksek+) yok; lisans envanteri 110 NuGet / 11 npm | `20261011T215704Z-9jii` | 21:57:04 | 10,7 sn |
| `migrations` | **geçti** | 0 | model eşit; gerçek uygulama testleri 20 / 0 / 0 | `20261011T215716Z-kvzx` | 21:57:16 | 25,2 sn |
| `integration` | **geçti** | 0 | 330 / 0 / 0 | `20261011T215747Z-a0uo` | 21:57:47 | 96,3 sn |
| `web` | **geçti** | 0 | typecheck, lint, build; bileşen testleri 110 / 0 / 0 | `20261011T215934Z-5a30` | 21:59:34 | 45,4 sn |
| `e2e` | **geçti** | 0 | 29 / 0 / 0, kararsız 0 (Chrome, 1 işçi; imaj `c922f387bd27`) | `20261011T220024Z-5p7e` | 22:00:25 | 108,9 sn |
| `smoke` | **geçti** | 0 | 30 denetimin 30'u | `20261011T220226Z-2du5` | 22:02:26 | 102,1 sn |
| `ai-live` | **koşulamadı (`not-run`)** | 4 | yerel model yapılandırılmamış; canlı model davranışı doğrulanmadı | `20261011T220422Z-5eib` | 22:04:23 | 0 sn |

Her `verify.ps1` çağrısı çıkış kodu **0** ile bitti. `ai-live` çağrısında betiğin çıkış kodu da 0'dır, çünkü betik "bu ortamda koşulamaz" durumunu başarısızlık saymaz; kapının kendi sonucu `not-run`, çıkış kodu 4'tür ve bu **geçti demek değildir**.

Çağrılar (depo kökünden, bu sırayla, her biri ayrı süreç):

```powershell
./scripts/verify.ps1 -Gate scripts,restore,build,unit
./scripts/verify.ps1 -Gate scans
./scripts/verify.ps1 -Gate migrations
./scripts/verify.ps1 -Gate integration
./scripts/verify.ps1 -Gate web
./scripts/verify.ps1 -Gate e2e
./scripts/verify.ps1 -Gate smoke
./scripts/verify.ps1 -Gate ai-live
```

Kapıların çalıştırdığı komutlar:

| Kapı | Komut |
| --- | --- |
| `scripts` | `scripts/tests/run.ps1` |
| `restore` | `dotnet restore Kantar.slnx --locked-mode --disable-build-servers` |
| `build` | `dotnet build Kantar.slnx -c Release --no-restore --disable-build-servers -m:2 -v:minimal` |
| `unit` | `dotnet test --project tests/Kantar.UnitTests -c Release --no-build` |
| `integration` | `dotnet test --project tests/Kantar.IntegrationTests -c Release --no-build` (tek kullanımlık SQL Server container'ı; uygulama kısıtlı `kantar_app` girişiyle) |
| `web` | `scripts/gates/web.ps1`: typecheck, lint, bileşen testleri (Vitest), build |
| `migrations` | `dotnet ef migrations has-pending-model-changes` + `dotnet test … --filter-class '*MigrationTests'` (boş kurulum ve veriyle yükseltme, gerçek sunucuda) |
| `e2e` | `scripts/gates/e2e.ps1`: `local-stack init` → `up` (imajı çalışma ağacından derler) → `seed` → `npm run e2e` → `reset` (proje `kantar-e2e`, port 8963) |
| `smoke` | `scripts/gates/stack-smoke.ps1` (proje `kantar-smoke`, port 8953) |
| `scans` | `scripts/gates/scan.ps1` |
| `ai-live` | `scripts/gates/ai-live.ps1` |

Ham günlükler: `artifacts/verification/raw/<kapı>.log` (git dışı; her kapının **son** koşusu). Kayıt: `artifacts/verification/checkpoints.json` (parmak izi başına son sonuç).

### Duman kapısının 30 denetimi (hepsi `ok`)

Boş kurulum: `health/live` 200; migration sonrası `health/ready` 200; oturumsuz API çağrısı JSON 401. Seed: depo sorumlusu girişi; ikinci seed bakiye değiştirmiyor; ikinci seed günlük satırı eklemiyor (16 satır). Komut: satış temsilcisi girişi; sipariş taslağı 201. Sertleştirme: API container'ı kök değil (kullanıcı 1654); kök dosya sistemi salt okunur; ortam değişkenlerinde parola/bağlantı dizgisi yok; yalnız API portu ve yalnız `127.0.0.1`. API yeniden başlatma: yeniden sağlıklı; oturum sürüyor. Veritabanı durdur/başlat: `ready` 503; `live` 200; API HTML'siz JSON 5xx/401 veriyor, asılmıyor; `ready` geri geliyor; giriş yeniden çalışıyor; kesintiden önce yazılan sipariş okunuyor. `down` + `up`: giriş; veri duruyor. Sağlayıcı `Disabled`: durum "disabled"; soru `ProviderUnavailable` (hata değil); kaynak arama çalışıyor; ERP komutu (rezervasyonlu onay) 200. `reset` koruması: Kantar etiketi olmayan volume varken reddediyor; reddedilen `reset` hiçbir şey silmedi; yanlış onay değerini reddediyor. Sonunda kendi yığını tamamen kaldırıldı.

### Tarayıcı testlerinin kapsadığı akışlar (29 test, 9 dosya)

PO → limit reddi → onay → kısmi mal kabul → stok ve hareket; satış taslağı → onay → kısmi sevk → kalan iptal → bakiye/günlük; eski sürüm (412); rol/depo reddi, oturum ve önbellek yalıtımı, zorunlu parola değişimi; destek talebi → kaynaklı yardım → açık onayla öneriyi kaydet; kaynak yok ve düşmanca girdi; axe + klavye; üç genişlik. "Sağlayıcı kullanılamıyor" durumu tarayıcıda **taklit yanıtla** sınanır; gerçek kapalı-sağlayıcı yolu duman kapısındadır.

## 2. Bu doğrulama sırasında kaynak nasıl değişti

Önceki checkpoint'ten sonra **ürün kodu (`src/`, `db/`, `Dockerfile`, `compose.yaml`) değişmedi.** Değişiklikler yalnız `tests/` ve `scripts/` altındadır. Her kaynak durumunun sonuçları ayrı tutulur:

| # | Parmak izi | Bir öncekine göre fark | Bu durumda koşulan kapılar ve sonuçları |
| --- | --- | --- | --- |
| A | `f734d881…5616` (314 dosya) | Önceki oturumun sonu | `scripts` 36/36, `restore`, `build`, `unit` 2036/2036 geçti (çağrı `20261011T202247Z-uhy5`). `integration` **kesildi, sonuç yok** (§3) |
| B | `a0c9942e…978d` (316) | + `Database/TransactionRetryTests.cs`, `Infrastructure/TestLogs.cs`, fixture'a günlük yakalayıcı; `stack-smoke.ps1` (başlıklar dosyadan, etkin müşteri süzgeci); `e2e.ps1` (eski sonuç dosyası silinir, imaj kimliği kaydedilir) | `scripts` 36/36, `restore`, `build`, `unit` 2036/2036 (`…213951Z-szro`); `integration` 330/330 (`…214022Z-ndi4`); `web` 110/110 (`…214219Z-ymnf`); `migrations` 20/20 (`…214315Z-vyu9`); `e2e` 29/29, imaj `99fef3d94e7d` (`…214429Z-yvi1`); `smoke` 30/30 (`…214823Z-dtj1`); `scans` **başarısız** (`…215032Z-fwgx`, §3) |
| C | `7f881734…5bc9` (316) | Tarayıcı düzeltmesi (ilk deneme) + betik testleri; `MigrationTests.cs` içinde bir satıra işaret yorumu; `migrations.ps1` | `scripts` 40/40, `restore`, `build`, `unit` 2036/2036 (`…215336Z-uao3`); `scans` **başarısız** (`…215401Z-m4fl`, §3) |
| D | `1ca269ce…28ff` (316) | Tarayıcı testlerinin kendi örnekleri çalışma zamanında birleştirilir; bir yorum yeniden yazıldı | `scripts` 40/40, `restore`, `build`, `unit` 2036/2036 (`…215450Z-h7bp`); `scans` geçti (`…215507Z-dims`); `migrations` 20/20 (`…215523Z-6a1d`) |
| E | `1cd663dc…c6a0` (316) | `Disable-BuildNodeReuse` (`common.ps1`, `verify.ps1`, `migrations.ps1`, `scan.ps1`) + betik testi | **§1'deki tablo: bütün kapılar** |

B'de geçen `integration`, `web`, `e2e` ve `smoke` kapıları E'de **yeniden koşuldu** ve yine geçti; B sonuçları E sonucu olarak sunulmaz.

## 3. Başarısız ve kesilen koşular

| Ne | Kaynak | Sonuç | Neden | Ne yapıldı |
| --- | --- | --- | --- | --- |
| `integration` kapısı (önceki oturum, çağrı `…202247Z-uhy5`) | A | **Kesildi, sonuç yok** | Çalışma ortamı, sistem belleği kritik düzeye indiği için süreci durdurdu. Test hatası değil; geçti de sayılmaz | Yeniden başlatılmadı; bu oturumda istenince koşuldu (B ve E) |
| `scans` kapısı, ilk koşu (`…215032Z-fwgx`, çıkış 1) | B | **Başarısız**: 14 gizli bilgi bulgusu; `.local`, `.local-e2e`, `.local-smoke` "git-ignore edilmemiş" | Kapı betiği ilk kez çalışıyordu. 14 satır tek tek okundu: 9'u C# kodunda değişkenler arası atama (nesne başlatıcıda parola özelliğine bir değişkenin atanması, boş dizgiyle alan tanımı vb.), 4'ü betik testlerinin bilerek yazdığı sahte örnek, 1'i erişilemeyen sunucuya giden sahte değerli test bağlantı dizgisi. **Gerçek gizli bilgi yok.** Git-ignore uyarısı betik hatasıydı: desen `.local/` dizin desenidir ve var olmayan dizin eğik çizgisiz sorulunca git eşleştirmez | Tarayıcı düzeltildi (§4, madde 5–6) |
| `scans` kapısı, ikinci koşu (`…215401Z-m4fl`, çıkış 1) | C | **Başarısız**: 7 bulgu | Hepsi bu düzeltme için yazılan yeni satırlardaydı: betik testinin örnek satırları (6) ve tarayıcıdaki bir yorum (1) | Örnekler çalışma zamanında birleştirilir hale getirildi, yorum yeniden yazıldı; üçüncü koşu (D) ve dördüncü koşu (E) geçti |

Bu oturumda `unit`, `integration`, `web`, `migrations`, `e2e` ve `smoke` kapılarında başarısız koşu **olmadı**; `e2e` ve `smoke` betikleri ilk çalıştırmalarında geçti (koşmadan önce okunarak bulunan hatalar §4'te).

Önceki oturumların geliştirme koşularındaki başarısızlıklar §8'dedir.

## 4. Bu doğrulamada bulunan ve düzeltilen kusurlar

Hiçbiri ürün kodunda değildir; hepsi doğrulama betikleri ve testlerdedir. Hiçbir denetim kaldırılmadı, hiçbir test atlanmadı.

1. **Yeniden deneme yolunun çalışma zamanı kanıtı yoktu.** Yalnız hata iletisi ayrıştırma testi vardı. `Database.TransactionRetryTests` eklendi (§5).
2. **Duman betiği `If-Match` başlığını bozardı.** Windows PowerShell 5.1, yerel programlara verilen argümanlardaki iç çift tırnakları düşürür; `If-Match: "<etag>"` tırnaksız gider ve sunucu 412 döner. Başlıklar artık dosyadan okunur (`curl -H @dosya`); CSRF belirteci de komut satırında görünmez. İlk koşudan **önce**, betik okunurken bulundu.
3. **Duman betiği ilk müşteriyi seçerken etkinlik süzmüyordu**; `?active=true` eklendi.
4. **E2E betiği eski bir koşunun sonuç dosyasını sayabilirdi** (tarayıcı hiç başlamasa bile). Dosya koşudan önce silinir. Ayrıca API container'ının gerçekten çalıştırdığı imajın kimliği ve derleme zamanı günlüğe ve kapı notuna yazılır.
5. **Gizli bilgi taraması kod atamalarını bağlantı dizgisi sanıyordu.** Kaynak kod dosyalarında (`.cs`, `.ts`, `.tsx`, `.js`, `.ps1`, `.sh` …) dize sabiti **dışındaki** eşleşme değişken atamasıdır ve bulgu sayılmaz; dize sabiti **içindeki** bağlantı dizgisi hâlâ bulgudur; yapılandırma dosyalarında kural değişmedi. İki yeni betik testi bunu iki yönden sınar (atama → 0 bulgu; üç dilde sabit içi dizgi → 3 bulgu; yapılandırma dosyası → 1; işaretli satır atlanır, işaretsizi atlanmaz). Tarayıcının kendi öz denetimi (pozitif ve yanlış-pozitif kontrol) her koşuda geçmeye devam ediyor.
6. **Git-ignore denetimi yanlış alarm veriyordu**; artık dizinin içindeki bir yol sorulur (`.local/secrets/probe`).
7. **`migrations` ve `scans` kapıları boşta MSBuild işçi süreçleri bırakıyordu** (toplam 10 süreç, her biri ~100 MB, 15 dakikaya kadar). Bellek kısıtlı bu makinede kapılar arasında elle kapatıldılar (yalnız bu görevin başlattığı, komut satırı ve başlangıç zamanı doğrulanan süreçler). `verify.ps1` artık bütün kapılar için işçi yeniden kullanımını kapatır; E koşularından sonra geride `dotnet` süreci kalmadığı denetlendi.

## 5. Transaction yeniden deneme: deadlock ve kilit zaman aşımı

Sınıf `tests/Kantar.IntegrationTests/Database/TransactionRetryTests.cs`; `integration` kapısının 330 testine dahildir (B ve E koşularında geçti; ayrıca tek başına bir kez: 2/2, 38 sn). Hata enjekte edilmez; SQL Server'ın kendisi üretir. Ayrıntı ve sınırlar: `inventory-correctness-verification.md` §9.

| Gösterilen | Kanıt |
| --- | --- |
| **Gerçek deadlock oluştu** | İkinci oturum transferin ikinci dokunacağı bakiye satırını tutar; transfer ilk satırı güncelledikten sonra bekletilir; ikinci oturumun kilit beklediği `sys.dm_exec_requests` ile görülür; transfer bırakılınca döngü kapanır. Sunucunun kendi sayacı (`Number of Deadlocks/sec`, `_Total`) **tam 1** artar; uygulama hata **1205** ile yeniden deneme kaydeder |
| **Sınırlı yeniden deneme çalıştı** | Deadlock: tam bir yeniden deneme kaydı (1205, deneme 1), vazgeçme yok, sonuç `201`. Kilit zaman aşımı: (1222, deneme 1), (1222, deneme 2), sonra vazgeçme (1222, deneme 3) ve `503 retry_later` |
| **Mükerrer iş etkisi yok** | Geri alınan deneme hiçbir iz bırakmıyor (bakiye, hareket, belge, denetim, idempotency kaydı yok). Başarılı sonuçta tek belge, tek `TransferOut` + tek `TransferIn`, tek denetim ve tek idempotency kaydı; aynı anahtarın tekrarı bayt bayt aynı yanıt ve ek etki yok; mutabakat tutuyor. Vazgeçilen komut sıfır etki bırakıyor ve kilit kalkınca aynı anahtar tam bir kez etki ediyor |

Test edilmeyen: art arda üç deadlock ile vazgeçme (sınır kilit zaman aşımıyla gösterildi), anlık görüntü çakışması (3960) ve bağlantı hatasıyla yeniden deneme.

## 6. E2E: güncel kaynaktan derlenen imaj

- Önceki oturumda tarayıcı testleri Vite geliştirme sunucusu üzerinden geçmiş, **eski** imaja karşı 29 testten 5'i kalmıştı (§8). O sonuçlar güncel imaj doğrulaması **sayılmaz**.
- Bu oturumda imaj çalışma ağacından yeniden derlendi (`docker build`, 21:44:15 UTC). `e2e` kapısı ayrıca kendi `up --build` adımını çalıştırır ve API container'ının gerçekten kullandığı imajı kaydeder: B koşusunda `sha256:99fef3d94e7d…`, E koşusunda `c922f387bd27…`; ikisinin de içerik derleme zamanı 21:44:15 UTC'dir (sonraki derlemeler katman önbelleğinden geldi; `src/` o andan beri değişmedi).
- Sonuç iki koşuda da 29 / 29, 0 atlanan, 0 kararsız. Eski imajda kalan beş test bu imajda geçti.
- Yardım paneli başlığının yapışkan yapıldığı son CSS düzeltmesi, kapının ürettiği `artifacts/verification/screenshots/talep-yardim-1440.png` görüntüsünde doğrulandı.

## 7. Kaynaklı yardım ve canlı model

- **Yerel model yok; canlı model doğrulanamadı.** Denetlendi: `KANTAR_AI_LOCAL_BASE_URL` tanımlı değil; `ollama`, `lms`, `llama-server` PATH'te yok; `127.0.0.1` üzerinde 11434, 1234 ve 8080 portları yanıt vermiyor. Model indirilmedi, kurulmadı; ücretli veya uzak API çağrılmadı.
- `ai-live` kapısı `not-run` kaydetti (çıkış kodu 4).
- Demo sağlayıcı bir dil modeli değildir. Onunla ve sahte (scripted) sağlayıcıyla geçen testler — getirme, yetki süzgeci, atıf doğrulama, üretim ortasında yetki iptali, açık onayla kaydetme, sağlayıcı kapalıyken ERP'nin çalışması — **gerçek model doğrulaması sayılmaz**. Gerçek bir modelin yanıt kalitesi, kaynağa sadakati, enjeksiyona direnci ve Türkçe başarımı ölçülmedi.
- Ayrıntı: `ai-verification.md`.

## 8. Önceki oturumların kapı dışı geliştirme koşuları (tarihsel)

Bunlar `verify.ps1` üzerinden değil, elle ve ayrı ayrı çalıştırıldı ve yukarıdaki parmak izlerinden **önceki** kaynak durumlarına aittir. Kapı sonucu yerine geçmezler.

| Ne | Sonuç | Not |
| --- | --- | --- |
| Entegrasyon, tam paket | 328 / 328, 1 dk 40 sn | Arayüzün son hali ve üç küçük sonraki değişiklik hariç |
| Entegrasyon, önceki tam koşu | 328 testten 306'sı geçti, **22 başarısız** | İki ürün kusuru (talep tanılama özeti; KB sürüm ekleme) — düzeltildi |
| Entegrasyon, daha önceki koşu | 188 testten 187'si geçti, **1 başarısız** | Test, yeni depo görünürlük kuralına göre düzeltildi |
| Entegrasyon, ilk koşu | 31 testten 30'u geçti, **1 başarısız** | Test hatası (sahte saat geri alınamıyor) — düzeltildi |
| Web: typecheck, lint, 110 bileşen testi, build | geçti | Yardımcı ajanın raporuydu. Bu oturumda `web` kapısıyla ana oturumda iki kez koşuldu (B ve E): 110 / 110 |
| Tarayıcı testleri, Vite geliştirme sunucusu → gerçek yığın | 29 / 29, iki kez | Güncel imaj doğrulaması değildir |
| Tarayıcı testleri, o günkü imaja karşı | 29 testten 24'ü geçti, **5 başarısız** | İmaj eski arayüzü içeriyordu |
| Yığın, seed ×2, HTTPS elle denetim | sağlıklı; ikinci seed 0 kullanıcı, 0 fark | Duman kapısı artık bunları otomatik denetliyor |

Testlerin bulduğu ve düzeltilen 12 ürün kusurunun listesi `implementation-progress.md` içindedir.

## 9. Hiç çalıştırılmayanlar ve açık kalanlar

- **Gerçek yerel modelle `ai-live`** (§7).
- **Uzak CI** (`.github/workflows/ci.yml`): depo hiç push edilmedi.
- Yük/performans, çok örnekli çalışma, ekran okuyucu, Firefox/Safari, sızma testi.
- Ekran görüntüleri bir insan tarafından incelenmedi (`ui-review.md`).
- Ürün günlüğünde, seed sırasında iki EF Core **uyarısı** görülüyor (hata değil): `First` işlecinin `OrderBy` olmadan kullanılması (olay 10103) ve birden çok koleksiyonun tek sorguda yüklenmesi (olay 20504). Seed sonucu iki kez aynı çıktığı (0 fark) duman kapısında denetleniyor; uyarıların kendisi giderilmedi.
- Belgeler (`docs/`, `README.md`) `scans` kapısından **sonra** güncellendi; bunlar parmak izinin dışındadır. Belgeler bittikten sonra tarayıcının gizli bilgi ve yazarlık işlevleri ağacın tamamında doğrudan bir kez daha çalıştırıldı (375 dosya; 0 ve 0 bulgu). Bu bir kapı koşusu değildir ve paket/lisans denetimlerini içermez.
- `scans` bir güvenlik denetimi değildir; "bulgu yok", "açık yok" demek değildir. Tarama, kod içinde tırnaklı parola sabitlerini (ör. test fixture parolaları) hedeflemez.

## 10. Temizlik ve bırakılan durum

- Bu görevin oluşturduğu çalışan container, volume, ağ, `.local*` dizini veya `dotnet` süreci **yok** (son kapıdan sonra denetlendi).
- Kalan imajlar: `kantar:local` (455 MB), `mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04` (2,34 GB) ve Docker derleme önbelleği. Silinmedi.
- Komşu container'lar (`kulvar-*`, `loca-*`, `lst-*`) oturum boyunca çalışır ve sağlıklı kaldı; onlara, portlarına ve verilerine dokunulmadı. Aynı anda en çok bir Kantar SQL Server örneği çalıştı.
- Geliştirme yığını (`kantar`, `.local/`) hiç oluşturulmadı.
- Depo: yerel `git init`, commit yok. Commit, push, PR ve dağıtım yapılmadı.
