# ADR-0001 — Veritabanı sağlayıcısı: SQL Server 2022 Developer

Durum: Kabul edildi · Tarih: 2026-10-11

## Bağlam

Kantar'ın doğruluk iddiaları (stok değişmezleri, eşzamanlı rezervasyon, idempotency) seçilen veritabanının kilit, kısıt ve transaction davranışına dayanır; bu yüzden sağlayıcı, strateji yazılmadan önce seçilmelidir.

İki kısıt birbirine karşı çalışır:

- **Teknoloji çeşitliliği.** Aynı yazarın bu makinedeki diğer portföy projeleri PostgreSQL kullanır. Proje hedefi, işveren teknolojisi çeşitliliği açısından SQL Server + EF Core SQL Server sağlayıcısı + `rowversion` kullanımını da göstermektir.
- **Bellek.** Geliştirme makinesi 15,7 GB RAM'lidir; oturum boyunca boş fiziksel bellek 0,5–1,8 GB arasında ölçüldü. Önceki projelerde doğrulama sırasında bellek sınırına dayanıldı. Komşu container'lar ve kullanıcının tarayıcısı durdurulamaz.

Çift sağlayıcı geliştirmek kapsam dışıdır.

## Ölçümler (bu oturum)

| Ölçüm | Değer |
| --- | --- |
| İmaj | `mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04`, diskte 2,34 GB |
| Sürüm | SQL Server 16.0.4295.3, Developer Edition (64-bit), CU27 |
| Hazır olma | Başlatmadan ~3 sn sonra `sqlcmd` ile sorgu yanıtladı |
| Boşta container belleği | 892 MiB (`MSSQL_MEMORY_LIMIT_MB=1280` ile) |
| Çalışan kullanıcı | Kök değil: `uid=10001(mssql)` |
| Tam metin arama | `SERVERPROPERTY('IsFullTextInstalled') = 0` (resmî imajda kurulu değil) |
| Docker VM bellek sınırı | 7,59 GiB |
| Ana makine | 15,7 GB RAM; oturumda 0,5–1,8 GB boş |
| Komşu PostgreSQL container'ları (boşta) | Her biri 44–65 MiB |

Deneme container'ı sahiplik etiketi doğrulanarak kaldırıldı; komşu container'lara dokunulmadı.

Resmî belgelerden:

- Container için önkoşul: "At least 2 GB of RAM" (ve en az 2 GB disk).
- Developer sürümü: "licensed for use as a development and test system, not as a production server."
- `MSSQL_MEMORY_LIMIT_MB`: SQL Server'ın kullanabileceği azami belleği ayarlar; varsayılan toplam fiziksel belleğin %80'idir.
- `MSSQL_PID=Developer`, 2022 imajı için geçerli sürüm değerlerindendir.

Bu oturumda NuGet dizininden okunan paket sürümleri: `Microsoft.EntityFrameworkCore.SqlServer` 10.0.12 (en yeni kararlı), `Microsoft.Data.SqlClient` 7.1.1, `Testcontainers.MsSql` 4.16.0.

Makinede ayrıca Windows servisi olarak çalışan bir SQL Server örneği (`MSSQLSERVER`) vardır. Bu, paylaşılan/komşu bir kaynak sayılır; Kantar onu **kullanmaz**, ona migration, seed veya test verisi yazmaz.

## Karar

**SQL Server 2022 Developer** (sabit etiket `2022-CU27-ubuntu-22.04`), EF Core 10 SQL Server sağlayıcısı ve `Microsoft.Data.SqlClient` ile kullanılır. Tek kullanımlık test veritabanı Testcontainers ile aynı imajdan açılır.

İşletim kuralları:

1. Bu makinede aynı anda **tek** Kantar SQL Server örneği çalışır (geliştirme yığını, test container'ı veya duman/E2E yığınından biri).
2. `scripts/verify.ps1` ağır kapılar arasında kendi başlattığı yığını sahiplik etiketini doğrulayarak indirir ve her ağır kapıdan önce boş belleği ölçer.
3. SQL Server belleği `MSSQL_MEMORY_LIMIT_MB=1280` ile sınırlanır. Container'ın bellek sınırı resmî asgari olan 2 GB'ın altına **çekilmez**; desteklenmeyen bir yapılandırmayla "çalışıyor" denmez.
4. Developer sürümü yalnız geliştirme ve test içindir; bu depo üretim dağıtımı tanımlamaz.

Kilit, upsert, eşzamanlılık ve yeniden deneme stratejileri SQL Server'a özgü yazılır (`04-database-design.md` §3); PostgreSQL deyimleri (`SKIP LOCKED`, `ON CONFLICT`, `xmin`) kopyalanmaz.

## Değerlendirilen alternatifler

| Alternatif | Artı | Eksi | Sonuç |
| --- | --- | --- | --- |
| PostgreSQL 18 | Boşta ~50 MiB; imaj zaten yerelde; yazarın bildiği araçlar | Portföyde sağlayıcı çeşitliliği sağlamaz | Seçilmedi; SQL Server çalıştırılamasaydı yedek seçenekti |
| SQL Server 2025 | En yeni sürüm; imaj etiketleri mevcut (`2025-CU9-…`) | Bu ürün yeni özelliklerine ihtiyaç duymuyor; bellek davranışı ve imaj boyutu bu oturumda ölçülmedi | Seçilmedi |
| SQL Server Express | Üretimde ücretsiz lisans | Arabellek havuzu 1.410 MB, veritabanı 50 GB sınırı (2025 belgesine göre); bu projede lisans avantajı gerekmiyor | Seçilmedi |
| Makinedeki yerel SQL Server servisi | Ek bellek gerektirmez | Paylaşılan kaynak; tek kullanımlık değil; sahipliği doğrulanamaz | Reddedildi |
| İki sağlayıcıyı birlikte desteklemek | – | İki kilit/upsert stratejisi, iki migration kümesi, iki test matrisi | Reddedildi |

## Sonuçlar / maliyet

- Her SQL Server örneği boşta ~0,9 GB tutar; komşu PostgreSQL container'larının ölçülen boşta değerinin yaklaşık 14–20 katı. Doğrulama sıralı ve yavaştır; bellek düşükse kapılar bekler veya kontrol noktasıyla durur.
- Tam metin arama resmî imajda yoktur; bilgi bankası araması uygulama içinde yapılır (ADR-0007).
- İmaj 2,34 GB disk kullanır; ilk çekme uzun sürer.
- SQL Server'a özgü ipuçları (`UPDLOCK`, `HOLDLOCK`, `READPAST`, `ROWLOCK`) ve hata numaraları koda girer; sağlayıcı değişimi bu katmanın yeniden yazılmasını gerektirir.

## Değişme koşulu

- Bellek, tek bir SQL Server örneğini ve test çalıştırıcısını birlikte kaldıramazsa (entegrasyon kapısı tekrar tekrar bellek nedeniyle durursa) PostgreSQL'e geçiş bu ADR'nin yerine yeni bir ADR ile yapılır; iki sağlayıcı birlikte tutulmaz.
- Üretim dağıtımı hedeflenirse lisanslı bir sürüm (Standard/Enterprise veya Express) seçilmelidir.

## Kaynaklar

- https://learn.microsoft.com/en-us/sql/linux/install-upgrade/quickstart-install-docker — container önkoşulları (2 GB RAM)
- https://learn.microsoft.com/en-us/sql/linux/configure/environment-variables — `MSSQL_PID`, `MSSQL_MEMORY_LIMIT_MB`
- https://learn.microsoft.com/en-us/sql/sql-server/editions-and-components-of-sql-server-2025 — sürüm tanımları, Developer lisans kapsamı, Express sınırları
- https://mcr.microsoft.com/v2/mssql/server/tags/list — imaj etiketleri

SQL Server 2022'ye özel sürüm karşılaştırma sayfası ve EF Core SQL Server sağlayıcı belgeleri bu oturumda doğrulanmadı.
