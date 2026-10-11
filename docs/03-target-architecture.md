# 03 — Hedef Mimari

## 1. Özet

Kantar tek kod tabanından, tek imaj olarak dağıtılan bir ASP.NET Core (.NET 10) **modüler monolittir** (web ve worker aynı imajın iki ayrı sürecidir). Arayüz aynı origin'den statik dosya olarak sunulur; kimlik doğrulama sunucu taraflı oturum + çerez + CSRF belirtecidir. Kalıcı durumun tamamı tek bir SQL Server 2022 veritabanındadır ve her iş komutu tek bir veritabanı transaction'ıdır.

```
Tarayıcı (React SPA, aynı origin)
        │  HTTPS, __Host- çerez, X-CSRF-Token, Idempotency-Key, If-Match
        ▼
┌───────────────────────────── Kantar.Api (tek imaj) ─────────────────────────────┐
│ Uç noktalar (Minimal API)  →  İstek hattı: oturum → CSRF → hız sınırı → yetki   │
│        │                                                                        │
│        ▼                                                                        │
│ Kantar.Application: komut/sorgu işleyicileri (modül klasörleri)                 │
│   Identity · MasterData · Inventory · Sales · Purchasing · Support · Ai · Ops   │
│        │                         │                                              │
│        ▼                         ▼                                              │
│ Kantar.Domain (saf kurallar)   Kantar.Infrastructure                            │
│                                  EF Core (SQL Server) · stok SQL ilkel işlemleri│
│                                  idempotency deposu · outbox · AI adaptörleri   │
└───────────────┬─────────────────────────────────────────────┬───────────────────┘
                │ kantar_app (kısıtlı)                         │ yalnız loopback / izinli özel uç
                ▼                                             ▼
        SQL Server 2022                               Yerel model sunucusu (isteğe bağlı)
                ▲
                │ kantar_migrator (yalnız `migrate`/`seed` modunda)
```

Aynı imaj dört modda çalışır: varsayılan (web), `--worker`, `--migrate`, `--seed`. Web ve worker ayrı süreçlerdir; ikisi de yalnız `kantar_app` girişiyle bağlanır.

## 2. Projeler ve bağımlılık yönleri

| Proje | İçerik | Bağımlı olduğu |
| --- | --- | --- |
| `Kantar.Domain` | Varlıklar, değer nesneleri, durum makineleri, miktar kuralları, yetki politikası (saf fonksiyonlar), hata kodları | yok (yalnız BCL) |
| `Kantar.Application` | Komut/sorgu işleyicileri, port arayüzleri (`IUnitOfWork`, `IStockLedger`, `IAnswerGenerator`, `IClock`…), istek kanonikleştirme | Domain |
| `Kantar.Infrastructure` | `KantarDbContext`, migration'lar, stok SQL'i, idempotency/outbox depoları, AI adaptörleri, worker döngüleri | Application, Domain |
| `Kantar.Api` | Host, uç noktalar, oturum/CSRF/hız sınırı, ProblemDetails, statik UI, mod seçimi | Application, Infrastructure |
| `tests/Kantar.Testing` | Test veritabanı sahiplik koruması, ortak yardımcılar | – |
| `tests/Kantar.UnitTests` | Domain + Application kuralları, mimari yön testi | Domain, Application, Testing |
| `tests/Kantar.IntegrationTests` | Gerçek SQL Server + gerçek kısıtlı rol üzerinde HTTP → DB testleri | Api, Testing |
| `src/web` | React + TypeScript + Vite arayüz; bileşen testleri; Playwright E2E | – |

Yön kuralı (`Domain ← Application ← Infrastructure ← Api`) bir birim testiyle derleme referanslarından denetlenir. Modüller proje değil klasör/ad alanıdır: sekiz ayrı derleme, bu ölçekte derleme süresi ve bellekten başka bir şey kazandırmaz. Modüller arası çağrı Application katmanındaki açık arayüzlerle yapılır (ör. Sales, stok için yalnız `IStockLedger` kullanır; `StockBalances` tablosuna doğrudan yazmaz).

## 3. Kararlar

Her karar: ihtiyaç → seçilen → daha basit alternatif → maliyet → ne olursa değişir. Ayrıntılı gerekçeler `docs/adr/` altındadır.

| # | İhtiyaç | Karar | Daha basit alternatif ve neden yetmedi | Maliyet | Değişme koşulu |
| --- | --- | --- | --- | --- | --- |
| 1 | Sipariş, stok, günlük, idempotency, denetim ve outbox'ın birlikte tutarlı olması | Tek veritabanı, tek `DbContext`, komut başına tek transaction (ADR-0002) | Modül başına DbContext: aynı bağlantı/transaction'ı paylaşmak için ek tesisat ister, kazancı yok | Modüller şema düzeyinde ayrılır, derleme düzeyinde değil | Bir modül ayrı ölçeklenmek/ayrı dağıtılmak zorunda kalırsa |
| 2 | Portföyde PostgreSQL dışında sağlayıcı; `rowversion`, kilit ipuçları | SQL Server 2022 Developer (ADR-0001) | PostgreSQL: bu makinede ~50 MB ile çalışır; ama hedeflenen işveren teknolojisi çeşitliliğini sağlamaz | Boşta ~0,9 GB bellek; aynı anda tek SQL Server örneği kuralı | Bellek kısıtı tek örneği bile kaldırmazsa (ölçüm ADR'de) |
| 3 | Stokta yarışsız güncelleme | Tek ifadeli koşullu `UPDATE … OUTPUT` + belge satırında `UPDLOCK` + (depo, ürün) sıralı kilitleme (ADR-0003) | Oku-kontrol et-yaz + `rowversion` tekrar denemesi: yüksek çekişmede çok tekrar, çok satırda kısmi durum riski | Stok ilkel işlemleri ham SQL'dir (parametrik, tek sınıfta) | – |
| 4 | Tekrarlanan isteklerin aynı sonucu vermesi | Kalıcı idempotency kaydı, iş etkisiyle **aynı transaction'da**; yanıt baytları saklanır (ADR-0004) | Yalnız iş referansı benzersizliği: yanıtı tekrar edemez, belge oluşturmayı kapsamaz | Her önemli komutta bir satır + yanıt gövdesi | – |
| 5 | Oturum iptali, rol kaldırmanın anında etkisi | Sunucu taraflı oturum tablosu + her istekte güncel rol/depo okuması (ADR-0005) | Çerezde imzalı claim: iptal ve rol değişimi süre dolana kadar etkisiz | İstek başına 1 küçük sorgu | Çok örnekli ve yüksek trafikli dağıtım (kısa ömürlü önbellek + iptal sürümü) |
| 6 | Okuma tarafı | EF Core LINQ projeksiyonları; raporlar dahil | Dapper: bu sorgular için LINQ yeterli; ikinci veri erişim yolu dekoratif olurdu | – | LINQ'in üretemediği bir rapor sorgusu ortaya çıkarsa |
| 7 | Komut/sorgu ayrımı | Elle yazılmış işleyici sınıfları, DI ile; MediatR yok | – | Biraz tekrar eden kayıt kodu | – |
| 8 | Arka plan işleri | Veritabanı outbox + kiralamalı (lease) worker, çit belirteci (fencing) (ADR-0006) | Süreç içi kuyruk: çökme anında kaybolur | Worker süreci, yoklama gecikmesi (varsayılan 2 sn) | Dış tüketici/yüksek hacim → broker |
| 9 | Bilgi bankası araması | Uygulama içi sözcüksel sıralama; yetki filtresi SQL sorgusunda (ADR-0007) | SQL Server Full-Text: resmi imajda kurulu değil (`IsFullTextInstalled = 0` ölçüldü), özel imaj gerekir | Parçalar bellekte puanlanır; KB büyürse yavaşlar | KB birkaç bin parçayı aşarsa FTS'li özel imaj veya ayrı arama |
| 10 | Yanıt üretimi | `IAnswerGenerator`: deterministik demo sağlayıcı + OpenAI-uyumlu yerel HTTP adaptörü (ADR-0008) | Yalnız demo sağlayıcı: gerçek model davranışı hiç sınanamaz | Yerel model yoksa canlı doğrulama eksik kalır ve böyle raporlanır | – |
| 11 | Arayüz | React SPA, aynı origin, `wwwroot` içine derlenir | Razor Pages: yoğun etkileşimli belge ekranlarında daha çok sunucu gidiş-gelişi | Node araç zinciri | – |
| 12 | Önbellek | Yok (Redis yok; AI yanıt önbelleği yok) | – | Her yardım sorusu yeniden üretilir | Ölçülmüş gecikme sorunu |

## 4. İstek hattı

Sıra önemlidir ve entegrasyon testleriyle sabitlenmiştir:

1. **Korelasyon kimliği** üretilir (istemcinin gönderdiği değer kabul edilmez).
2. **Güvenlik başlıkları**: CSP (`default-src 'self'`), `X-Content-Type-Options`, `Referrer-Policy`, `frame-ancestors 'none'`; `/api` yanıtlarında `Cache-Control: no-store`.
3. **Oturum çözümleme**: çerezdeki rastgele belirtecin SHA-256 özeti `iam.Sessions` içinde aranır; oturum iptal edilmemiş, süresi dolmamış ve kullanıcının `SecurityStamp` değeriyle eşleşiyor olmalıdır. Aynı sorgu güncel rolleri ve depo yetkilerini getirir.
4. **CSRF**: `GET/HEAD/OPTIONS` dışındaki her `/api` isteğinde `X-CSRF-Token` doğrulanır (oturuma bağlı). Hata → `403 csrf_failed`.
5. **Hız sınırı**: oturum açmış isteklerde kullanıcı başına, anonim uçlarda (giriş, CSRF) kaynak IP başına; `X-Forwarded-For` yalnız yapılandırılmış güvenilir vekilden kabul edilir (varsayılan: hiçbiri).
6. **Gövde bağlama**: `Content-Type: application/json`, boyut sınırı (varsayılan 64 KB), bilinmeyen alan ve yinelenen anahtar reddi.
7. **Yetkilendirme**: rol + depo seviyesi + sahiplik; uç nokta düzeyinde rol, işleyici içinde kaynak düzeyi.
8. **Idempotency** (işaretli komutlarda): kayıt bakılır → varsa istek özeti karşılaştırılır → eşleşirse **yetki yeniden denetlendikten sonra** kayıtlı yanıt bayt bayt döner.
9. **İşleyici** tek transaction içinde çalışır.

Kimlik doğrulama/yetki hataları HTML yönlendirmesi değil JSON `ProblemDetails` döner (`401`, `403`, `404`).

## 5. Komut yürütme modeli

```csharp
interface ICommandHandler<TCommand, TResult>
{
    // Yalnız okur. Güncel izinleri ve kaynak kapsamını denetler. Tekrar oynatmada da çalışır.
    Task AuthorizeAsync(TCommand command, CurrentUser user, CancellationToken ct);
    // Transaction içinde çalışır; tekrar denemede baştan çağrılabilir, dış çağrı yapmaz.
    Task<TResult> ExecuteAsync(TCommand command, CurrentUser user, CancellationToken ct);
}
```

`CommandPipeline` şunları sırayla yapar:

1. `AuthorizeAsync`.
2. Idempotency kaydı var mı? Varsa ve özet eşleşiyorsa kayıtlı yanıtı döndür; özet farklıysa `409 idempotency_key_reused`.
3. Transaction aç (`READ COMMITTED`, veritabanında `READ_COMMITTED_SNAPSHOT ON`).
4. Idempotency satırını ekle (benzersiz anahtar eşzamanlı ikiziyle çakışırsa transaction geri alınır, 2. adıma dönülür).
5. `ExecuteAsync`: belge satırı `UPDLOCK, ROWLOCK` ile kilitlenir → durum/`rowversion` denetlenir → stok ilkel işlemleri (depo, ürün) sırasıyla çalışır → günlük, denetim ve outbox satırları eklenir.
6. Yanıt gövdesi baytlara serileştirilir ve idempotency satırına yazılır.
7. Commit. Ancak bundan sonra yanıt istemciye yazılır.

Sonuç: "talep alındı ama iş bitmedi" diye görünür bir ara durum yoktur. Commit'ten önceki çökme hiçbir iz bırakmaz (tekrar deneme işi baştan yapar); commit'ten sonraki yanıt kaybında tekrar deneme kayıtlı baytları alır.

Deadlock (1205), kilit zaman aşımı (1222) ve anlık görüntü çakışması (3960) tüm transaction geri alınıp `ChangeTracker` temizlenerek en fazla 3 kez yeniden denenir. İş kuralı hataları (yetersiz stok, geçersiz durum) yeniden denenmez.

## 6. Arka plan işleri

| İş | Tetikleyici | Garanti |
| --- | --- | --- |
| Outbox dağıtımı | Yoklama (2 sn) | En az bir kez; tüketici `(MessageId, Handler)` benzersizliğiyle tekrar etkisiz |
| Bildirim üretimi (düşük stok, talep atandı) | Outbox olayı | Süreç içi bildirim satırı; dış gönderim yok |
| Süresi dolan idempotency kayıtları, oturumlar, AI yanıtları | Zamanlı (5 dk) | Silme koşulu `ExpiresAt < now`; tekrar çalışması zararsız |
| Stok mutabakatı | Zamanlı (15 dk) + elle | **Salt okunur**; fark bulursa rapor satırı yazar, hiçbir bakiyeyi düzeltmez |

Kiralama: worker `LeaseOwner`, `LeaseUntil`, `LeaseVersion` alanlarını tek `UPDATE … OUTPUT` ile alır (`READPAST, UPDLOCK, ROWLOCK`). Tamamlama `WHERE Id = @id AND LeaseOwner = @me AND LeaseVersion = @v` koşulludur; kirası düşmüş bir worker'ın geç gelen tamamlaması 0 satır etkiler ve yok sayılır. Kapanışta worker yeni kiralama almaz, eldeki mesajı bitirir, süre dolarsa kirası kendiliğinden düşer.

## 7. Hata davranışı ve bağımlılıklar

- Veritabanı erişilemezse `/health/ready` 503 döner, `/health/live` 200 kalır; iş uçları `503 retry_later` döner.
- AI sağlayıcısı erişilemezse yalnız yardım yanıtı `ProviderUnavailable` olur; kaynak araması ve bütün ERP komutları çalışmaya devam eder. AI çağrısı hiçbir zaman bir iş transaction'ı içinde yapılmaz.
- Üretim yapılandırmasında hata enjeksiyonu yoktur; test hata noktaları yalnız test derlemesinin DI kaydıyla eklenir.

## 8. Gözlemlenebilirlik

- Yapılandırılmış günlük (JSON, stdout). İstek gövdesi, soru metni, yanıt metni, çerez, parola, bağlantı dizesi günlüğe yazılmaz. Kullanıcı adı yerine kullanıcı kimliği yazılır.
- `System.Diagnostics.Metrics` sayaçları: komut sonucu (`operation`, `outcome`), yardım yanıtı (`provider`, `status`), outbox (`outcome`), süre histogramları. Etiket değerleri kapalı kümedir; belge, kullanıcı, ürün, depo kimliği etiket olmaz.
- Ölçümler `SystemAdmin` için `/api/ops/metrics` altında özet olarak okunur; dış ölçüm toplayıcı entegrasyonu yoktur.

## 9. Yerel dağıtım

`compose.yaml`: `sqlserver` (host portu yayınlamaz), `migrator` (tek seferlik), `api` (`127.0.0.1:8943`), `worker`, `seed` (profil). Compose proje adı `kantar`; duman testi `kantar-smoke` (8953), E2E `kantar-e2e` (8963). Bu makinede aynı anda yalnız bir Kantar SQL Server örneği çalıştırılır; `scripts/verify.ps1` ağır kapılardan önce belleği ölçer.
