# Kantar — Portföy Özeti

Yazar: **TCoderr**

Kantar, küçük ve orta ölçekli bir toptan ticaret işletmesi için yerelde uçtan uca çalışan bir **sipariş, satın alma, stok ve uygulama destek merkezi** dilimidir. Kapsam bilerek dardır; amaç çok özellik değil, az sayıda iş akışının **eşzamanlılık altında doğru** çalıştığını gerçek veritabanında göstermektir.

Bu belge kısa bir özet ve yeniden üretilebilir bir turdur. Sayıların kaynağı [`final-verification.md`](final-verification.md), sınırların kaynağı [`known-limitations.md`](known-limitations.md) dosyasıdır; ikisiyle çelişen bir cümle varsa onlar geçerlidir.

## 1. Ne gösteriyor

| Konu | Nasıl çözüldü | Kanıt |
| --- | --- | --- |
| Fazla satış olmaması | Bakiye tek deyimli koşullu `UPDATE … OUTPUT` ile değişir; `OnHand ≥ Reserved ≥ 0` ayrıca veritabanı kısıtıdır | 25 eşzamanlı onay, 10'luk stok: tam 10 onay, 15 ret (`ConfirmConcurrencyTests`) |
| Ya hep ya hiç | Komut başına tek transaction: idempotency kaydı + iş etkisi + saklanan yanıt birlikte commit edilir | Transferin ortasında enjekte edilen hata hiçbir iz bırakmıyor (`TransferTests`) |
| Çift etki olmaması | `Idempotency-Key` ile bayt bayt aynı yanıt; ayrıca iş referansı (irsaliye, sevk, transfer, düzeltme numarası) benzersiz | 10 paralel aynı anahtar → 10 özdeş yanıt, tek etki (`IdempotencyTests`) |
| Deadlock ve kilit zaman aşımı | Bütün transaction en çok 3 kez yeniden denenir, sonra `503` | Gerçek deadlock (sunucu sayacı +1, hata 1205) → geri alma → yeniden deneme → tek etki; 3 denemede vazgeçme ve sıfır etki (`TransactionRetryTests`) |
| Değiştirilemez günlük | Hareket ve rezervasyon tabloları yalnız eklenir; çalışma zamanı girişi (`kantar_app`) `UPDATE`/`DELETE`/şema değişikliği yapamaz | 14 tabloda `UPDATE`/`DELETE`/`TRUNCATE` reddi (`RuntimeRolePermissionTests`) |
| Yetki | Rol + depo kapsamı + sahiplik; kapsam dışı kayıt "yok" gibi yanıtlanır; tekrar oynatma güncel yetkiyi atlamaz | Tablo güdümlü 1350+ satırlık politika testi ve HTTP düzeyinde kapsam testleri |
| Kaynaklı yardım | Yanıta giren metin yalnız kullanıcının okuyabildiği yayınlı belgelerdir; atıflar sunucuda doğrulanır; üretim sırasında yetki değişirse yanıt teslim edilmez; araç, SQL veya otomatik işlem yoktur | `KnowledgeAccessTests`, `AssistOrchestrationTests` (sahte ve demo sağlayıcıyla) |
| İşletilebilirlik | Tek imaj, kök olmayan kullanıcı, salt okunur kök dosya sistemi, yalnız `127.0.0.1` üzerinde tek port; veritabanı düşünce `ready` 503, `live` 200 | Duman kapısı: 30 denetim (`scripts/gates/stack-smoke.ps1`) |

Eşleme tablosunun tamamı: [`inventory-correctness-verification.md`](inventory-correctness-verification.md).

## 2. Mimari, tek bakışta

- **Modüler monolit**, ASP.NET Core Minimal API, .NET 10: `Kantar.Domain ← Kantar.Application ← Kantar.Infrastructure ← Kantar.Api`. Modüller klasördür: Identity, MasterData, Inventory, Sales, Purchasing, Support, Assistance, Operations.
- **SQL Server 2022 Developer** (container), EF Core 10. Stok kilitleme ve günlük yazımı sağlayıcıya özgü SQL ile; `rowversion` + `ETag`/`If-Match` ile kayıp güncelleme koruması. Neden SQL Server: [`adr/0001-database-provider.md`](adr/0001-database-provider.md).
- **Sunucu taraflı oturum** (çerez + CSRF); roller ve depo yetkileri her istekte veritabanından okunur.
- **Outbox + worker**: kiralamalı, çitli teslim; salt okunur mutabakat (günlük ↔ bakiye).
- **Arayüz**: React 19 + TypeScript + Vite, aynı origin, `default-src 'self'` CSP. Türkçe, açık tema, üç genişlikte (360 / 768 / 1440) sınandı.
- **Yanıt üretimi**: deterministik demo sağlayıcı (dil modeli **değildir**) veya zaten çalışan yerel bir OpenAI-uyumlu sunucu. Ücretli veya uzak servis çağrısı yoktur.

Kararlar: [`adr/`](adr/). Hedef mimari: [`03-target-architecture.md`](03-target-architecture.md).

## 3. Doğrulama durumu

Kapıların tamamı `scripts/verify.ps1` ile, bellek kısıtı nedeniyle **ayrı çağrılarla ve sırayla** koşuldu; tek çağrıda bütün kapıların koştuğu bir sonuç yoktur. Çağrı kimlikleri, kaynak parmak izleri ve başarısız ara koşular [`final-verification.md`](final-verification.md) içindedir.

| Kapı | Sonuç (son kaynak parmak izinde) |
| --- | --- |
| Betik testleri | 42 / 42 (21 test × tr-TR / en-US) |
| Birim testleri | 2036 / 2036, 0 atlanan |
| Entegrasyon (gerçek SQL Server, kısıtlı giriş) | 330 / 330, 0 atlanan |
| Arayüz: typecheck, lint, build, bileşen testleri | geçti; 110 / 110 |
| Migration: model eşitliği + gerçek uygulama | geçti; 20 / 20 |
| Tarayıcı (Chrome, tek işçi, güncel kaynaktan derlenen imaj) | 29 / 29, 0 atlanan, 0 kararsız |
| Compose duman testi | 30 / 30 denetim |
| Taramalar (gizli bilgi desenleri, yazarlık, bilinen açıklı paketler, lisans envanteri) | bulgu yok — güvenlik denetimi değildir |
| Canlı yerel dil modeli (`ai-live`) | **doğrulanamadı — bu makinede yerel model yok** |

İki kabul ayrı tutulur:

- **ERP kabulü** (sipariş, satın alma, stok, destek, yetki, idempotency, yığın): yukarıdaki kapılara dayanır.
- **Canlı AI kabulü**: **yok.** Kaynaklı yardımın getirme, yetki, atıf doğrulama ve hata davranışı test edildi; gerçek bir dil modelinin yanıt kalitesi, uydurma eğilimi, enjeksiyona direnci ve Türkçe başarımı ölçülmedi. Demo sağlayıcıyla geçen testler bunun yerine geçmez. Ayrıntı: [`ai-verification.md`](ai-verification.md).

Doğrulanmayanlar (özet): uzak CI, yük/performans, çok örnekli çalışma, ekran okuyucu, Chrome dışı tarayıcılar, sızma testi.

## 4. Yeniden üretilebilir tur (yaklaşık 15 dakika)

Önkoşullar ve bellek notu için [`../README.md`](../README.md). Komutlar depo kökünden çalıştırılır.

```powershell
./scripts/local-stack.ps1 init      # gizli dosyalar (.local/, git dışı)
./scripts/local-stack.ps1 up        # imajı derler, yığını başlatır
./scripts/local-stack.ps1 seed      # demo verisi (tekrar çalıştırılabilir)
```

Adres: https://localhost:8943 (kendinden imzalı geliştirme sertifikası). Parolalar `.local/seed/Seed__Passwords__<kullanıcı>` dosyalarındadır; hiçbir yerde yazılı değildir.

| Adım | Kullanıcı | Ne yapılır | Ne görülür |
| --- | --- | --- | --- |
| 1 | `satinalma1` | Satın alma siparişleri → yeni taslak (IST-01) → onayla | Onay limiti 0 olduğu için ret |
| 2 | `satinalma.onay` | Aynı siparişi onayla | Onaylandı |
| 3 | `depo1` | Siparişe kısmi mal kabul (irsaliye numarasıyla) | Stok bakiyeleri ve Hareketler'de artış; sipariş "Kısmen kabul edildi" |
| 4 | `satis1` | Satış → Yeni sipariş (IST-01) → onayla | Rezervasyonlar'da kayıt; Kullanılabilir = Eldeki − Rezerve |
| 5 | `satis1` | Stoktan fazla miktarla ikinci bir sipariş onayla | `STK-409`: hiçbir satır rezerve edilmez |
| 6 | `depo1` | Onaylı siparişi kısmen sevk et | Eldeki ve Rezerve birlikte düşer; sipariş "Kısmen sevk edildi" |
| 7 | `satis1` | Kalan miktarı iptal et | Yalnız sevk edilmemiş kalan serbest kalır |
| 8 | `satis2`, sonra `satis1` | `satis2` ile bir taslak oluştur; adresini `satis1` oturumunda aç | "Bulunamadı" (sahiplik: temsilci yalnız kendi siparişini görür) |
| 9 | `depo.sorumlu` | Depo → Düzeltme (IST-01), gerekçeli | Hareket günlüğünde düzeltme satırı; ANK-01'de düzeltme yetkisi yok |
| 10 | `satis1` | Destek → Yeni talep (hata kodu `STK-409`) → "Bu talep için Kaynaklı Yardım" | Atıflı yanıt, "Demo sağlayıcı — dil modeli değil" etiketi; öneri yalnız açık onayla talebe kaydedilir |
| 11 | `admin` | Kullanıcılar ekranı | Kullanıcı yönetimi var; ticari belge ve stok görünmez |

Aynı akışlar tarayıcı testlerinde otomatik koşar: `src/web/e2e/` (`a-procure-to-stock`, `b-order-to-dispatch`, `c-stale-version`, `d-access-and-session`, `e-support-and-help`, …).

Kapatma: `./scripts/local-stack.ps1 down` (veritabanı kalır). Her şeyi silmek: `./scripts/local-stack.ps1 reset -ConfirmReset kantar`.

## 5. Kodda nereye bakılır

| Soru | Dosya |
| --- | --- |
| Stok nasıl kilitleniyor? | `src/Kantar.Infrastructure/Persistence/SqlStock.cs`, `src/Kantar.Application/Inventory/StockPoster.cs` |
| Transaction ve yeniden deneme | `src/Kantar.Infrastructure/Persistence/SqlUnitOfWork.cs` |
| Idempotency | `src/Kantar.Application/Common/CommandExecutor.cs` |
| Durum makineleri | `src/Kantar.Domain/Sales/SalesOrder.cs`, `src/Kantar.Domain/Purchasing/PurchaseOrder.cs` |
| Yetki politikası | `src/Kantar.Domain/Identity/AccessPolicy.cs` |
| Çalışma zamanı veritabanı rolü | `db/grants.sql` |
| Bilgi bankası erişimi ve kaynaklı yardım | `src/Kantar.Application/Support/KnowledgeService.cs`, `src/Kantar.Application/Assistance/AssistService.cs` |
| Eşzamanlılık testleri | `tests/Kantar.IntegrationTests/{Sales,Inventory,Purchasing,Idempotency,Database}/` |
| Doğrulama kapıları | `scripts/verify.ps1`, `scripts/gates/` |

## 6. Bu çalışma ne değildir

Üretim dağıtımı, muhasebe, e-fatura, ödeme, kargo veya KVKK uyumluluğu iddiası yoktur. Tüm veriler sentetiktir. Depo hiç push edilmedi; uzak CI çalıştırılmadı. Sınırların tamamı: [`known-limitations.md`](known-limitations.md).
