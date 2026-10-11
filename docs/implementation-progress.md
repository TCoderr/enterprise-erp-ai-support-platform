# Uygulama İlerlemesi

Her aşama sonunda: kararlar, dosyalar, komut / çıkış kodu / sayılar / atlananlar, kaynak parmak izi, sonraki adım. Bu dosya oturum kesilirse kaldığı yerden devam etmek içindir; pazarlama özeti değildir.

## Aşama 0 — Başlangıç, hedef dizin, araçlar, bellek (2026-10-11)

**Çalışma dizini**

- `D:\Enterprise ERP & AI Support Platform` boştu; git deposu değildi; `AGENTS.md`/`CLAUDE.md` yoktu (üst dizinlerde ve kullanıcı profilinde de yok).
- Aynı ERP ürününün başka bir kopyası aranıp bulunamadı (`D:\` altında iki seviye, ad eşleşmesi `erp`). Komşu ürünler farklı alanlardadır: Kulvar (çok kiracılı SaaS), LST (lojistik), Loca (etkinlik bileti), Halka (IAM).
- Ürün dizini oluşturuldu: `D:\Enterprise ERP & AI Support Platform\enterprise-erp-ai-support-platform` (komşu projelerle aynı düzen: başlık dizini altında kebab-case depo dizini).

**Araçlar**

| Araç | Sürüm |
| --- | --- |
| .NET SDK / runtime | 10.0.401 / 10.0.12 |
| Git | 2.55.0.windows.5 (global kimlik `TCoderr`; değiştirilmedi) |
| Node / npm | 24.18.0 / 12.1.0 |
| Docker Engine | 29.8.2 (Docker Desktop, VM sınırı 7,59 GiB, 20 vCPU) |
| Tarayıcı | Chrome ve Edge kurulu; Playwright Chromium önbelleği var |
| Yerel model | **Yok.** `ollama`, `lms`, `llama-server` PATH'te değil; `127.0.0.1:11434` ve `127.0.0.1:1234` yanıt vermiyor; model dizinleri yok |

**Bellek**

- Fiziksel 15,7 GB. Oturum boyunca boş bellek 0,5–1,8 GB arasında ölçüldü (kullanıcının tarayıcısı ve komşu container'lar çalışıyor).
- Komşu container'lar (dokunulmadı): `kulvar-*`, `loca-*`, `lst-*`; host portları 8443, 8643, 8843. Host'ta ayrıca yerel bir `MSSQLSERVER` hizmeti çalışıyor (port 1434); kullanılmadı ve kullanılmayacak.

**Seçilen adlar ve portlar (çakışma denetlendi)**

| | Değer |
| --- | --- |
| Ürün adı | Kantar |
| Compose projeleri | `kantar` / `kantar-smoke` / `kantar-e2e` |
| HTTPS portları (127.0.0.1) | 8943 / 8953 / 8963 |
| Vite geliştirme portu | 5193 |
| İmaj | `kantar:local` |
| Sahiplik etiketi | `dev.kantar.stack=true` (yığın), `dev.kantar.test=<runId>` (test) |

**Veritabanı fizibilite denemesi** (ayrıntı: `adr/0001-database-provider.md`)

| Komut | Sonuç |
| --- | --- |
| `docker pull mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04` | çıkış 0; 2,34 GB |
| `docker run … -e MSSQL_PID=Developer -e MSSQL_MEMORY_LIMIT_MB=1280` (etiketli, port yayınlamadan) | ~3 sn'de hazır; `16.0.4295.3 Developer Edition (64-bit) CU27`; uid 10001; boşta 892 MiB; `IsFullTextInstalled = 0` |
| Etiket doğrulanarak `docker rm -f -v` | kaldırıldı; komşu container'lar çalışmaya devam ediyor |

Karar: SQL Server 2022 Developer. Kural: aynı anda tek Kantar SQL Server örneği.

**Docker'da bu görevin bıraktığı durum**: `mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04` imajı (yeni çekildi). Container, volume veya ağ kalmadı.

**Tasarım kaynakları**

- Impeccable `skill-v4.5.2` (commit `d631a882…`, Apache-2.0) incelendi. Başlatıcı betiği kullanıcı profiline ikili dosya indirdiği için çalıştırılmadı; kanca kurulmadı.
- Vercel Web Interface Guidelines `command.md` okundu. `vercel-labs/agent-skills` deposu GitHub API'sinde lisans bildirmiyor; metni depoya kopyalanmadı.

## Tasarım (2026-10-11)

Belgeler yazıldı: `01`–`10`, `adr/0001`–`0008`, `PRODUCT.md`, `DESIGN.md` (tohum), `design/preview.html`.

Tasarım tutarlılık denetiminde bulunan ve belgelerde kapatılan çelişkiler:

| Çelişki | Karar |
| --- | --- |
| `POST /api/admin/users` hem idempotent hem geçici parola döndürüyordu (parola 24 saat saklanırdı) | Bu uç ve `reset-password` idempotency kapsamı dışında; yanıtları saklanmaz |
| 02 §8 yarış sonuçları (`409`) ile 05'teki zorunlu `If-Match` (`412`) uyuşmuyordu | Sevkiyat ve mal kabulde `If-Match` isteğe bağlı (kalan miktar + belge kilidi + iş referansı korur); diğer komutlarda zorunlu |
| Anonim CSRF belirtecinin neye bağlandığı tanımsızdı | Ön çerez + HMAC; yalnız `login` için geçerli |
| Seed hangi girişle ve kimin adına çalışıyor | `kantar_app` girişiyle; açılış belgelerinin sahibi oturum açamayan `sistem.acilis` |
| 04 §4 `*Lines` izin ifadesi belirsizdi | Kesinleşen belge satırları yalnız-ekleme; sipariş satırları güncellenebilir |
| "Mutabakat farkının çözümü stok düzeltmesidir" ifadesi yanlıştı | Günlük–bakiye farkının uygulama içi onarımı yok; düzeltme yalnız fiziksel sayım farkı içindir (04 §6) |

Prototip (`design/preview.html`) gerçek tarayıcıda **incelenmedi**; yalnız yapısal olarak denetlendi.

## Aşama 1–7 ve 9 — uygulama (2026-10-11, devam ediyor)

**Yazılan kod (ana ağaç, derleniyor: 0 uyarı, 0 hata, Debug)**

| Katman | İçerik |
| --- | --- |
| `Kantar.Domain` | Durum makineleri (satış, satın alma, destek talebi), miktar kuralları, `AccessPolicy` (yetki matrisi), metin normalleştirme, hata modeli |
| `Kantar.Application` | `CommandExecutor` (yetki → tekrar oynatma → tek transaction: idempotency + etki + yanıt baytları), `StockPoster` (sıralı koşullu güncelleme), Identity/Admin/MasterData/Inventory/Purchasing/Sales/Ticket/Knowledge/Assist/Operations servisleri, sözcüksel sıralama, istem kurucu |
| `Kantar.Infrastructure` | `KantarDbContext` + 2 migration (`InitialCore`, `SupportAndOperations`), stok SQL ilkel işlemleri, belge kilitleri, yeniden deneyen unit of work, `db/grants.sql`, PBKDF2, demo ve yerel-model yanıt üreticileri, outbox dağıtıcı (kiralama + çit), worker döngüleri, demo seed |
| `Kantar.Api` | Oturum/CSRF/hız sınırı/güvenlik başlıkları, katı JSON okuyucu, ProblemDetails, tüm uç noktalar, süreç modları (`--bootstrap`, `--migrate`, `--seed`, `--worker`, `--probe`, `--ai-live`) |
| Dağıtım ve betikler | `Dockerfile`, `compose.yaml`, `scripts/local-stack.ps1/.sh`, `scripts/verify.ps1` + `gates/*`, `scripts/source-fingerprint.ps1`, `scripts/tests/run.ps1`, `.github/workflows/ci.yml` |

**Gerçekten çalıştırılan doğrulamalar (hepsi ayrı çağrılar; `verify.ps1` üzerinden değil)**

| Ne | Komut | Sonuç |
| --- | --- | --- |
| Birim testleri (ilk küme: mimari yön, test-DB sahiplik koruması) | `dotnet test --project tests/Kantar.UnitTests --no-build` | 21 toplam, 21 geçti, 0 atlandı, çıkış 0 |
| Entegrasyon testleri, ilk koşu (kimlik/CSRF, DB kısıtlı rol, uçtan uca akış) — gerçek SQL Server 2022 container'ı, uygulama `kantar_app` girişiyle | `dotnet test --project tests/Kantar.IntegrationTests --no-build` | 31 toplam, 30 geçti, **1 başarısız**, 0 atlandı, 33 sn, çıkış 2 |
| Başarısız olan | `Session_expires_after_the_idle_timeout` | Test hatası (sahte saat geri alınamıyor); test düzeltildi, **yeniden koşulmadı** (sıradaki entegrasyon koşusunda) |
| Betik testleri | `powershell -File scripts/tests/run.ps1` | 34 toplam (17 test × tr-TR/en-US), 34 geçti, çıkış 0 |
| `verify.ps1 -List` | – | kapılar ve parmak izi listeleniyor |

Bu tablodaki entegrasyon sonucu **eski bir kaynak durumuna** aittir (sonradan Aşama 7/9 kodu eklendi). Güncel kaynakta entegrasyon kapısı henüz koşulmadı.

**Henüz hiç çalıştırılmayanlar** (bu tablonun yazıldığı anda): Docker imaj derlemesi, Compose yığını, seed, worker süreci, web arayüzü, tarayıcı testleri, duman testi, taramalar, Release derlemesi.

### Güncelleme — test paketlerinin birleştirilmesi (2026-10-11 gece)

İki izole kopyada yazılan testler ana ağaca alındı; sonraki koşuların hepsi ana ağaçta yapıldı (hâlâ ayrı çağrılar, `verify.ps1` dışında).

| Ne | Sonuç |
| --- | --- |
| Birim testleri (güncel kaynak) | 2036 toplam, 2036 geçti, 0 atlandı, çıkış 0 |
| Entegrasyon testleri, tam paket (güncel kaynak; gerçek SQL Server, `kantar_app` girişi) | **328 toplam, 328 geçti, 0 başarısız, 0 atlandı, 1 dk 40 sn, çıkış 0** |
| Ara koşular (tarihsel) | 188 testten 187 (1 test yeni görünürlük kuralına göre düzeltildi); 328 testten 306 (22 hata iki ürün kusurundan; ikisi de düzeltildi) |
| Docker imajı (`docker build -t kantar:local .`) | çıkış 0, 455 MB |
| Web (`src/web`; ajan raporu, ana oturumda yeniden koşulmadı) | typecheck, lint, 110 bileşen testi, build geçti |

**Testlerin bulduğu ve düzeltilen ürün kusurları**

| # | Kusur | Düzeltme |
| --- | --- | --- |
| 1 | Taslak sipariş güncellemesi (satış/PO) her geçerli istekte `412` dönüyordu (EF, izlenen belgenin koleksiyonuna eklenen satırlara `UPDATE` gönderiyordu) | Satırlar açıkça `Add` ile eklenir |
| 2 | Aynı kusur: KB belgesine yeni sürüm eklemek her zaman başarısızdı (yalnız-ekleme tablosuna `UPDATE`) | `db.KbDocumentVersions.Add` |
| 3 | JSON olmayan gövde `415` yerine `404` alıyordu | API geri dönüş (fallback) uç noktası içerik türünü denetler |
| 4 | Dizi içinde `null` eleman (`"lines":[null]`) `500` üretiyordu | Bağlamadan önce reddedilir (`400`) |
| 5 | `?search=%00` tüm satırları döndürüyordu | Arama metni diğer metinler gibi temizlenir |
| 6 | Tedarikçi/depo için tekil okuma ve güncelleme uçları yoktu | Eklendi |
| 7 | Ekran/hata kodu olmayan destek talebini destek personeli okuyamıyordu (`500`; tanılama özeti geri okunamıyordu) | Tanılama kaydında isteğe bağlı alanlar |
| 8 | Yerel model adaptörü nesne olmayan yanıt gövdesinde istisna fırlatıyordu (`500` olurdu) | Geçersiz çıktı olarak sınıflanır |
| 9 | Veritabanı adı denetimi sondaki satır sonunu kabul ediyordu | `\z` |
| 10 | Docker derlemesinde analiz kuralları migration dosyalarına takılıyordu | Kural dosyası kaynakla birlikte taşınır |
| 11 | İstem temizliği yalnız yedi görünmez karakteri çıkarıyordu | Tüm Unicode "Format" karakterleri ve satır/paragraf ayırıcıları |
| 12 | Hiç onaylanmadan iptal edilen belge depo görevlisine görünüyordu | Depo yalnız onaylanmış belgeyi görür |

**Not (süreç)**: Ağaçlar arası kopyalanan dosyalar eski zaman damgasını koruduğu için artımlı derleme bir kez iki düzeltmeyi derlemedi; `--no-incremental` ile giderildi. Dosya kopyalayarak birleştirmeden sonra tam derleme yapılır.

**Docker'da bu görevin bıraktığı durum**: yalnız `mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04` imajı. Test container'ları Testcontainers tarafından kaldırılıyor.

## Aşama 8 ve 10 — arayüz, yığın ve son doğrulama (2026-10-11 gece) — CHECKPOINT

- Arayüz (`src/web`) yazıldı ve gerçek yığına karşı tarayıcıda sınandı: 29 Playwright testi iki kez üst üste geçti (Vite geliştirme sunucusu → gerçek API). Ayrıntı ve bulgular: `docs/ui-review.md`.
- Compose yığını ilk kez kuruldu, seed edildi, elle denetlendi ve etiket korumalı `reset` ile kaldırıldı.
- Son doğrulama `verify.ps1` ile başlatıldı: `scripts`, `restore`, `build`, `unit` kapıları geçti; **`integration` kapısı çalışırken çalışma ortamı sistem belleği kritik olduğu için süreci durdurdu.** Kalan kapılar koşulmadı.
- Kesintiden sonra: bu görevden kalan container/süreç yok; kaynak parmak izi `sha256:f734d881…5616` (314 dosya).

Kesin durum, sayılar ve tarihsel başarısızlıklar: `docs/final-verification.md`. Devam komutları: `docs/next-action.md`.

## Aşama 10 — son doğrulamanın tamamlanması (2026-10-12 00:39–01:10 TR)

Checkpoint'ten devam edildi. Yeni ürün özelliği eklenmedi; ürün kodu (`src/`, `db/`, `Dockerfile`, `compose.yaml`) **değişmedi**. Değişiklikler `tests/` ve `scripts/` altındadır.

**Aşama durumları**

| Aşama | Durum |
| --- | --- |
| 0 Ortam, tasarım belgeleri, ADR'ler | Tamam |
| 1 Temel / kimlik / ana veri | Tamam — `unit` ve `integration` kapıları |
| 2 Docker / yığın / betikler | Tamam — `smoke` kapısı (30 / 30 denetim), `scripts` kapısı |
| 3–6 Stok, satın alma, satış, sevk/iptal | Tamam — `integration` kapısı (330 / 330) |
| 7 Destek / bilgi bankası / kaynaklı yardım | Kod ve testler tamam (demo ve sahte sağlayıcıyla). **Canlı dil modeli doğrulanmadı** |
| 8 Arayüz | Tamam — `web` kapısı (110 bileşen testi), `e2e` kapısı (29 / 29, güncel imaj) |
| 9 Worker / mutabakat | Tamam — `integration` kapısı |
| 10 Son doğrulama | ERP kapılarının tamamı son parmak izinde geçti; `ai-live` koşulamadı (`not-run`); uzak CI çalıştırılmadı |

**Yapılanlar**

- `Database.TransactionRetryTests` eklendi: gerçek deadlock (1205, sunucu sayacı +1) → geri alma → yeniden deneme → tek etki; kilit zaman aşımında (1222) üç denemeden sonra `503` ve sıfır etki. Fixture'a yeniden deneme günlüklerini yakalayan `TestLogs` eklendi.
- İlk kez çalışan kapı betikleri: `e2e` ve `smoke` ilk koşuda geçti (iki hata koşmadan önce okunarak düzeltildi); `scans` iki kez başarısız oldu (tarayıcı kusurları, gerçek gizli bilgi yok) ve düzeltildi.
- `migrations` ve `scans` kapılarının bıraktığı boşta MSBuild işçileri (toplam 10 süreç) elle kapatıldı; `verify.ps1` artık işçi yeniden kullanımını kapatıyor.
- `docs/portfolio-summary.md` yazıldı; `README.md`, `final-verification.md`, `next-action.md`, `known-limitations.md`, `ai-verification.md`, `ui-review.md`, `inventory-correctness-verification.md` ve `09-test-and-verification-plan.md` gerçek sonuçlarla hizalandı.

**Kapılar** (son parmak izi `sha256:1cd663dc…c6a0`, 316 dosya; her biri ayrı çağrı, `verify.ps1` çıkış kodu 0)

| Kapı | Sonuç |
| --- | --- |
| `scripts` | 42 / 42 |
| `restore`, `build` (Release) | geçti |
| `unit` | 2036 / 2036, 0 atlanan |
| `integration` | 330 / 330, 0 atlanan |
| `web` | geçti; bileşen testleri 110 / 110 |
| `migrations` | model eşit; 20 / 20 |
| `e2e` | 29 / 29, 0 atlanan, 0 kararsız |
| `smoke` | 30 / 30 denetim |
| `scans` | bulgu yok |
| `ai-live` | **`not-run`** — yerel model yok; doğrulanamadı |

Ara kaynak durumları (dört parmak izi), başarısız `scans` koşuları ve çağrı kimlikleri: `docs/final-verification.md` §2–§3.

**Bırakılan durum**: bu görevden kalan container, volume, ağ, `.local*` dizini veya süreç yok; komşu container'lara dokunulmadı; commit, push, PR ve dağıtım yapılmadı.
