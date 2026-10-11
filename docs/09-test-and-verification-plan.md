# 09 — Test ve Doğrulama Planı

## 1. Test katmanları

| Katman | Proje / konum | Neyi kanıtlar | Neyi kanıtlamaz |
| --- | --- | --- | --- |
| Birim | `tests/Kantar.UnitTests` | Durum makineleri, miktar kuralları, istek özeti/kanonikleştirme, zaman kuralları, yetki politikası, puanlama, sahiplik koruması mantığı, mimari yön | Kilit, kısıt, transaction davranışı |
| Entegrasyon | `tests/Kantar.IntegrationTests` | HTTP → uygulama → **gerçek SQL Server 2022**; uygulama **gerçek `kantar_app` girişiyle** bağlanır. Kısıtlar, transaction'lar, eşzamanlılık, roller, işler | Tarayıcı davranışı |
| Bileşen | `src/web` (Vitest + Testing Library) | Arayüz durumları, biçimleme, güvenli metin işleme | Gerçek API sözleşmesi |
| E2E | `src/web/e2e` (Playwright, kurulu Chrome, tek worker) + axe | Gerçek tarayıcı + gerçek API + gerçek veritabanı uçtan uca senaryolar | Ekran okuyucu deneyimi |
| Duman | `scripts/stack-smoke.ps1` (Compose) | HTTPS, kalıcılık, yeniden başlatma, veritabanı indir/kaldır, sağlık, yardım kapalıyken ERP | Yük, çok örnek |

SQLite veya InMemory sağlayıcı hiçbir katmanda kullanılmaz; kilit ve kısıt kanıtı sayılmaz. Entegrasyon testleri migrator girişiyle çalıştırılıp uygulama etkisi "geçti" sayılmaz: fixture şemayı migrator ile kurar, test edilen uygulama yalnız `kantar_app` bağlantı dizesini alır.

## 2. Test veritabanı sahiplik koruması

Amaç: geliştirme, paylaşılan veya komşu bir veritabanına migration, seed, silme veya test verisi yazılmasını imkânsız kılmak. Ad öneki tek başına yeterli sayılmaz.

Fixture bir **manifest** üretir: `{ runId, containerId, host, port, database, markerSchema }`. İlk yazımdan önce `TestDatabaseGuard` şu koşulların **tümünü** salt okunur sorgularla doğrular:

| # | Denetim | Kaynak |
| --- | --- | --- |
| 1 | Açık onay sabiti (`KANTAR_TEST_DB=owned`) verilmiş | Fixture |
| 2 | Bağlanılan host ve port, manifestteki container uç noktasına eşit | Bağlantı dizesi ↔ manifest |
| 3 | Veritabanı adı `kantar_test_<runId>` biçiminde | Manifest |
| 4 | Sunucunun bildirdiği `DB_NAME()` manifestteki ada eşit | Sunucu |
| 5 | Sunucu kimliği (`@@SERVERNAME` / container ana makine adı) manifestteki container'a ait | Sunucu |
| 6 | `ops.TestRunMarker` içindeki `RunId` manifestteki `runId` değerine eşit | Veritabanı |

Herhangi biri tutmazsa koruma istisna fırlatır ve **hiçbir yazım yapılmaz**. Korumanın kendisi birim testlidir: `ITestDatabaseProbe` için casus (spy) uygulama kullanılır; yanlış host, yanlış ad, eksik işaret, eksik onay durumlarında yazma çağrısı sayısının sıfır olduğu doğrulanır.

Temizlik yalnız sahiplik doğrulanmış kaynaklara uygulanır: container `kantar.test-run=<runId>` etiketini, volume'lar `dev.kantar.stack=true` etiketini taşımalıdır. Etiketi olmayan kaynak silinmez; betik hata verip durur.

## 3. Belirlenimci eşzamanlılık

- Yarışlar **engel** (barrier) ile kurulur: N görev aynı `Barrier`/`TaskCompletionSource` üzerinde bekler, birlikte bırakılır. Sıralı senaryolarda (önce sevkiyat / önce iptal) birinci komut, test DI'ı ile eklenen bir **duraklama noktasında** transaction'ı açıkken bekletilir; ikinci komutun kilitte beklediği `sys.dm_tran_locks`/`sys.dm_exec_requests` ile gözlendikten sonra birinci serbest bırakılır.
- Rastgele `Thread.Sleep`/`Task.Delay` bir değişmezin kanıtı olarak kullanılmaz. Bekleme yalnız gözlenebilir bir koşulun yoklanmasında, üst süre sınırıyla yer alır.
- Zaman `TimeProvider` üzerinden okunur; testler `FakeTimeProvider` ile süre dolumu, kira düşmesi ve kilit süresini ilerletir.
- Her eşzamanlılık testi sonunda bakiye değişmezleri ve mutabakat sorguları (`04` §6) çalıştırılır.
- Entegrasyon testleri tek container, tek derleme içinde **sıralı** koşar (`parallelizeTestCollections: false`); testlerin kendi içindeki paralel istekler bilinçlidir.

## 4. Hata noktaları

Hata enjeksiyonu yalnız test derlemesinde vardır: `IFailpoints` arayüzünün üretim kaydı boş uygulamadır; testler `WebApplicationFactory` içinde kendi uygulamasını kaydeder (ör. "hedef bakiye yazımından sonra istisna", "commit'ten sonra yanıttan önce bağlantıyı kes", "sağlayıcı çağrısında bekle"). Üretim yapılandırmasında hata ayıklama rotası veya ortam değişkeniyle açılan hata noktası yoktur; bunun yokluğu bir testle doğrulanır.

## 5. Değişmez → test → beklenen sonuç

Sınıf adları öneridir; `U` = `tests/Kantar.UnitTests`, `I` = `tests/Kantar.IntegrationTests`.

### 5.1 Stok ve transaction

| # | Değişmez | Katman / sınıf | Beklenen sonuç |
| --- | --- | --- | --- |
| S1 | (depo, ürün) bakiyesi tektir | I `Inventory.BalanceConstraintTests` | İkinci satır ekleme birincil anahtar hatası verir |
| S2 | `OnHand ≥ Reserved ≥ 0` | I `Inventory.BalanceConstraintTests` | Migrator girişiyle doğrudan ihlal eden `UPDATE` `CHECK` hatası verir |
| S3 | Eski okuma–kontrol–yazma yarışı yok | I `Inventory.ConditionalUpdateRaceTests` | Engelle bırakılan N azaltmadan yalnız kapasite kadarı 1 satır etkiler; bakiye negatif olmaz |
| S4 | Rezervasyon: bakiye + sipariş durumu + satır + rezervasyon kaydı + idempotency + denetim + outbox tek transaction | I `Sales.ConfirmConcurrencyTests` (çok satırlı ya hep ya hiç), `Inventory.TransferTests` ve `Purchasing.ReceiptTests` (hata noktası) | Uygulanan hata noktası stok yazımından sonradır (`stock.after-posting`): transaction geri alınır, hiçbir tabloda satır kalmaz. Outbox yazımına özel ayrı bir hata noktası **yoktur**; outbox satırı aynı transaction'da yazıldığı için aynı geri almayla kapsanır, ayrıca sınanmadı |
| S5 | Sevkiyat aynı şekilde atomik | I `Sales.ShipmentAtomicityTests` | Hareket yazımından sonra hata → `ShippedQty`, bakiye, günlük değişmez |
| S6 | İptal aynı şekilde atomik | I `Sales.CancelAtomicityTests` | Hata → rezervasyon ve durum değişmez |
| S7 | Mal kabul aynı şekilde atomik | I `Purchasing.ReceiptAtomicityTests` | İkinci satırda hata → ilk satırın stoku ve hareketi de yok |
| S8 | Transfer ve düzeltme aynı şekilde atomik | I `Inventory.TransferAtomicityTests`, `Inventory.AdjustmentAtomicityTests` | Hata → iki depoda da bakiye ve günlük değişmez |
| S9 | Çok satır/çok depo kilit sırası belirlenimci | I `Inventory.LockOrderingTests` | Ters sırada satır gönderen iki eşzamanlı transfer deadlock olmadan tamamlanır |
| S10 | Deadlock/kilit zaman aşımında sınırlı yeniden deneme | I `Database.TransactionRetryTests` | Gerçek deadlock (1205, sunucu sayacıyla doğrulanır) → geri alma → yeniden deneme → tek etki; kilit zaman aşımında (1222) en fazla 3 deneme, sonra `503 retry_later`; etki yok |
| S11 | Yeniden deneme iş etkisini tekrarlamaz | I `Platform.RetryPolicyTests` | İlk denemede hata, ikincide başarı → tek hareket, tek denetim satırı |
| S12 | Dış çağrı transaction dışında | U `Architecture.TransactionBoundaryTests` + I `Ai.AssistIsolationTests` | Yardım çağrısı sırasında açık transaction sayısı 0; sağlayıcı beklerken stok komutu tamamlanır |
| S13 | Çift sevkiyat günlük tekrarı üretmez | I `Sales.DuplicateDispatchTests` | Aynı `DispatchReference`, farklı anahtar → `409 duplicate_business_reference`; tek hareket |
| S14 | Çift mal kabul günlük tekrarı üretmez | I `Purchasing.DuplicateReceiptTests` | Aynı (`SupplierId`, `ExternalReference`), farklı anahtar → `409`; stok bir kez artar |
| S15 | Çift transfer/düzeltme tekrarı üretmez | I `Inventory.DuplicateReferenceTests` | Aynı `Reference`, farklı anahtar → `409`; tek çift hareket |
| S16 | Aynı belge satırı aynı türde iki hareket üretemez | I `Inventory.MovementUniquenessTests` | `UQ(ReferenceType, ReferenceLineId, Type)` ihlali hata verir |
| S17 | Transfer kaynak düşüşü + hedef artışı atomik | I `Inventory.TransferAtomicityTests` | Başarıda iki hareket ve toplam stok sabit; hedefte hata → kaynak da değişmez |
| S18 | Transfer yetkisi: kaynak **ve** hedefte `Operate` | I `Inventory.TransferAuthorizationTests` | Yalnız birinde yetki → `404`; yazım yok |
| S19 | Eşzamanlı confirm fazla satmaz | I `Sales.ConfirmOversellTests` | Kapasite 10, 25 eşzamanlı confirm → tam 10 başarı, 15 `409 insufficient_available`; `Reserved = 10` |
| S20 | Çok satırlı confirm hepsi-ya-da-hiçbiri | I `Sales.ConfirmAllOrNothingTests` | Bir satır yetersiz → hiçbir satır rezerve değil, sipariş `Draft` |
| S21 | İptal–kısmi sevkiyat yarışı (önce sevkiyat) | I `Sales.DispatchCancelRaceTests` | Sevk edilen korunur, iptal yalnız kalanı bırakır; `Ordered = Shipped + Cancelled` |
| S22 | İptal–kısmi sevkiyat yarışı (önce iptal) | I `Sales.DispatchCancelRaceTests` | Sevkiyat reddedilir; hareket yok; `Reserved` tam bırakılmış |
| S23 | Negatif düzeltme ile confirm/sevkiyat yarışı rezervasyonu yok saymaz | I `Inventory.AdjustmentVersusReservationTests` | Sonuç her sırada `OnHand ≥ Reserved`; kaybeden `409 insufficient_available` |
| S24 | Fazla kabul reddi | I `Purchasing.ReceiptValidationTests` | `q > OpenQty` → `409 exceeds_remaining`; yazım yok |
| S25 | Yanlış ürün / başka PO satırı / başka depo reddi | I `Purchasing.ReceiptValidationTests` | `422 line_mismatch`; hiçbir satır yazılmaz |
| S26 | Yetkisiz depoda mal kabul | I `Purchasing.ReceiptAuthorizationTests` | `404`; yazım yok |
| S27 | Paralel mal kabulde yalnız kalan miktar | I `Purchasing.ParallelReceiptTests` | Toplam `ReceivedQty ≤ OrderedQty`; fazlası reddedilir |
| S28 | PO kalan kabul / iptal aynı kilit ve sürümle | I `Purchasing.ReceiveCancelRaceTests` | İki sırada da `Ordered = Received + Cancelled`; iptalden sonra kabul reddedilir |
| S29 | Yanıt kaybında tekrar aynı sonucu verir | I `Platform.LostResponseReplayTests` | Commit sonrası bağlantı kesilir; aynı anahtar → aynı baytlar; tek belge |
| S30 | Bilinmeyen durumda yeni belge başlamaz | I `Platform.LostResponseReplayTests` | Yeni anahtarla tekrar → iş referansı `409`; belge sayısı 1 |
| S31 | Günlük değiştirilemez | I `Security.RuntimeDbPermissionTests` | `kantar_app` ile `UPDATE`/`DELETE`/`TRUNCATE` hata verir; satır sayısı aynı |
| S32 | Açılış stoku hareketlidir | I `Operations.SeedReconciliationTests` | Seed sonrası her bakiye için `Σ hareket = OnHand`; hareketsiz bakiye yok |
| S33 | Seed tekrarı güvenli | I `Operations.SeedReconciliationTests` | İkinci seed sonrası bakiye ve hareket sayısı değişmez |
| S34 | Mutabakat salt okunur | I `Operations.ReconciliationTests` | Migrator ile yapay sapma → çalışma bulgu yazar; bakiye ve günlük değişmez |
| S35 | Sapma "onarıldı" diye sunulmaz | I `Operations.ReconciliationTests` | Yanıt durumu `DriftFound`; otomatik düzeltme belgesi oluşmaz |
| S36 | Sapma, yetkili düzeltmeyle normal transaction olarak çözülür | I `Operations.ReconciliationTests` | `Supervise` kullanıcısının düzeltmesinden sonraki çalışma bulgusuz |

### 5.2 Idempotency ve eşzamanlılık

| # | Değişmez | Katman / sınıf | Beklenen sonuç |
| --- | --- | --- | --- |
| D1 | Kalıcı kayıt: kullanıcı + işlem + anahtar + özet sürümü + süre | I `Platform.IdempotencyRecordTests` | Başarılı komut sonrası satır var; `HashVersion`, `ExpiresAt` dolu |
| D2 | Kapsam kullanıcı ve işleme göredir | I `Platform.IdempotencyScopeTests` | Aynı anahtar, farklı kullanıcı veya farklı işlem → bağımsız yürütme |
| D3 | Aynı anahtar + aynı istek: durum, başlık ve gövde **baytları** aynı | I `Platform.IdempotencyReplayTests` | Bayt dizileri eşit; `Location`, `ETag` eşit; `Idempotent-Replay: true` |
| D4 | Aynı anahtar + farklı gövde | I `Platform.IdempotencyReplayTests` | `409 idempotency_key_reused`; etki yok |
| D5 | Tekrar oynatmadan önce yetki denetlenir | I `Platform.IdempotencyAuthorizationTests` | Rol/depo yetkisi kaldırıldıktan sonra tekrar → `403`/`404`; kayıtlı gövde dönmez |
| D6 | Kayıt, iş tamamlanmadan görünür olmaz | I `Platform.IdempotencyCrashTests` | Commit öncesi hata → kayıt yok; tekrar deneme işi yapar |
| D7 | Eşzamanlı aynı anahtar tek etki | I `Platform.IdempotencyConcurrencyTests` | İki eşzamanlı istek → tek belge; ikisi de aynı baytları alır |
| D8 | Süresi dolan anahtar yeniden kullanılabilir, çift etki yine olmaz | I `Platform.IdempotencyExpiryTests` | Saat 24 saat ilerletilir → istek yeniden değerlendirilir; iş referansı/durum çift etkiyi engeller |
| D9 | Benzersizlik hatasından sonra aynı transaction'da sorgu yok | I `Platform.UniqueViolationHandlingTests` | Çakışmada transaction geri alınır, mevcut kayıt yeni bağlamda okunur; yarım yazım yok |
| D10 | Bakiye satırı güvenli oluşturma | I `Inventory.BalanceUpsertRaceTests` | Aynı (depo, ürün) için eşzamanlı ilk girişler → tek satır, doğru toplam |
| D11 | Kanonikleştirme: alan sırası, boşluk, NFC, `null` ≡ yok | U `Idempotency.RequestCanonicalizerTests` | Eşdeğer istekler aynı özeti; anlamca farklı istek farklı özeti verir |
| D12 | Miktar tam sayı, para en fazla 2 ondalık | U `Validation.NumberPolicyTests` + I `Api.StrictJsonTests` | `1.0`, `"1"`, `1e0`, 3 ondalık → `400` |
| D13 | Bilinmeyen alan ve yinelenen anahtar reddi | I `Api.StrictJsonTests` | `400 unknown_field` / `duplicate_field` |
| D14 | Özet şeması "tam RFC 8785" diye adlandırılmaz | U `Idempotency.RequestCanonicalizerTests` + belge | Sürüm etiketi `v1`; belgede sınır yazılı |
| D15 | Eksik `If-Match` | I `Api.PreconditionTests` | `428 precondition_required` |
| D16 | Eski `If-Match`; sessiz kayıp güncelleme yok | I `Api.PreconditionTests` | `412`; ilk yazarın değişikliği korunur |
| D17 | HTTP dışı geçersiz veriyi veritabanı reddeder | I `Persistence.ConstraintTests` | Negatif miktar, `Shipped + Cancelled > Ordered`, geçersiz durum, tür–işaret uyumsuzluğu → kısıt hatası |
| D18 | Durum geçişi korumaları | U `Sales.SalesOrderStateTests`, `Purchasing.PurchaseOrderStateTests`, `Support.TicketStateTests` | İzinli olmayan her geçiş `invalid_state`; son durumlar değişmez |
| D19 | Sayfalama kararlı: zaman + benzersiz bağlayıcı | I `Api.KeysetPaginationTests` | Aynı `CreatedAt` değerli satırlarda sayfalar arası tekrar/atlama yok; araya ekleme sonuçları kaydırmaz |

### 5.3 Güvenlik

| # | Değişmez | Katman / sınıf | Beklenen sonuç |
| --- | --- | --- | --- |
| G1 | Rol + depo + sahiplik matrisi | U `Access.AuthorizationPolicyTests` + I `Security.AuthorizationMatrixTests` | `02` §2.4'ün her hücresi için izin/ret beklenen kodla |
| G2 | Kapsam dışı belge tutarlı `404` | I `Security.ScopeDenialTests` | Var olan kapsam dışı ile var olmayan kaynak aynı durum ve gövde biçimi |
| G3 | Başka modül bağlantısı yetki vermez | I `Support.TicketDocumentBoundaryTests` | Talebi gören kullanıcı ilişkili siparişi/PO'yu `404` alır; talep yanıtında belge içeriği yok |
| G4 | Parola değiştirme/sıfırlama eski çerezi düşürür | I `Security.SessionRevocationTests` | Yakalanmış eski çerez `401` |
| G5 | Rol kaldırma ve çıkış eski çerezi düşürür | I `Security.SessionRevocationTests` | Aynı |
| G6 | Depo yetkisi değişikliği bir sonraki istekte geçerli | I `Security.CurrentPermissionTests` | Yetki kaldırılır → kullanıcının oturumları düşer (eski oturum `401`); yeni oturumla aynı belge `404` |
| G7 | Açık kayıt yok; giriş yönetici oluşturmaz | I `Security.BootstrapTests` | Kayıt rotası `404`; bilinmeyen kullanıcıyla giriş kullanıcı satırı oluşturmaz |
| G8 | Demo tohumlama yalnız `--seed` modunda | I `Security.BootstrapTests` | Web süreci başlatıldığında kullanıcı tablosu değişmez |
| G9 | CSRF akışı anonim → giriş → oturumlu | I `Security.CsrfFlowTests` | Belirteçsiz/yanlış belirteçli yazma `403 csrf_failed`; anonim belirteç oturumlu istekte reddedilir |
| G10 | Çerez öznitelikleri, HTTPS | I `Security.CookieAttributeTests` | `__Host-` öneki, `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/` |
| G11 | Özel API yanıtları `no-store`; kimlik hatası JSON | I `Security.ResponseHeaderTests` | `Cache-Control: no-store`; `401`/`403` `application/problem+json`, yönlendirme yok |
| G12 | Hız sınırı politikası | I `Security.RateLimitTests` | Aşımda `429` + `Retry-After`; kullanıcılar birbirinin sayacını tüketmez |
| G13 | Vekil başlığına varsayılan güven yok | I `Security.ForwardedHeaderTests` | `X-Forwarded-For` ile kaynak IP değiştirilemez |
| G14 | Çalışma zamanı girişi günlük/denetimde `UPDATE`/`DELETE`/`TRUNCATE` yapamaz | I `Security.RuntimeDbPermissionTests` | Gerçek `kantar_app` ile her deneme izin hatası |
| G15 | Çalışma zamanı girişi DDL yapamaz | I `Security.RuntimeDbPermissionTests` | `CREATE`/`ALTER`/`DROP` izin hatası |
| G16 | Düz metin kodlaması | Bileşen `SafeText.test.tsx` + E2E | `<script>` içeren metin etkisiz düz metin olarak görünür |
| G17 | Filtre/sıralama kapalı liste; sayfa boyutu sınırı | I `Api.QueryParameterTests` | Bilinmeyen parametre `400`; `limit=101` `400` |
| G18 | Toplu atama koruması | I `Api.MassAssignmentTests` | Gövdede fiyat/durum/sahip/numara → `400 unknown_field` |
| G19 | Alan düzeyi gizleme | I `Security.FieldVisibilityTests` | Depo görevlisi yanıtında fiyat/maliyet alanı JSON'da yok |
| G20 | Günlükte kişisel veri/gizli değer yok | I `Observability.LogRedactionTests` | Yakalanan günlükte kanarya parola, çerez, soru metni, müşteri adı yok |
| G21 | Ölçüm etiketleri düşük kardinaliteli | U `Observability.MetricLabelTests` | Etiket adları kapalı kümede; kimlik biçimli değer yok |
| G22 | Tanılama snapshot'ı beyaz listeden | U `Support.DiagnosticSnapshotTests` | Yalnız izinli alanlar; istemci gönderirse `400 unknown_field` |
| G23 | Yardım bağlamı kişisel veri taşımaz | I `Ai.ProviderInputTests` | Sağlayıcı girdisinde talep açıklaması, yorum, müşteri verisi yok |
| G24 | Ret yanıtı varlık çıkarımını sınırlar | I `Security.ScopeDenialTests` | Çakışma yanıtı yetkisiz belge numarası içermez |

### 5.4 Kaynaklı yardım

| # | Değişmez | Katman / sınıf | Beklenen sonuç |
| --- | --- | --- | --- |
| A1 | Yetkisiz belgenin kanarya metni sağlayıcıya gitmez | I `Ai.AclRetrievalTests` | Yakalanan sağlayıcı girdisinde ve yanıtta kanarya yok |
| A2 | Taslak ve emekli belge getirmeye girmez | I `Ai.AclRetrievalTests` | Aday kümesinde yok |
| A3 | Eski sürüm parçası servis edilmez | I `Ai.VersionServingTests` | Yeni sürüm yayınından sonra yalnız yeni sürüm parçaları |
| A4 | Aynı soru, farklı kullanıcı → farklı izinli kaynaklar | I `Ai.CrossUserIsolationTests` | Kaynak kümeleri kullanıcı yetkisine göre ayrışır |
| A5 | Yetkiler arası önbellek yok | I `Ai.CrossUserIsolationTests` | B, A'nın yanıt kimliğini `404` alır; B'nin yanıtı A'nın kaynağını içermez |
| A6 | Enjeksiyon veri kümesi | I `Ai.InjectionDatasetTests` | "Talimatları yok say", "sırları yazdır", gizli HTML, sahte sistem iletisi → kaynak metni `user` rolünde ve sınırlayıcılar etkisiz; yetki değişmez |
| A7 | Kaynak yok | I `Ai.NoSourceTests` | `NoSource`; sağlayıcı çağrı sayısı 0 |
| A8 | Sağlayıcı hatası / zaman aşımı / 429 | I `Ai.ProviderFailureTests` | `ProviderUnavailable`; kaynak listesi döner; ERP komutu etkilenmez |
| A9 | Uydurma atıf | I `Ai.CitationValidationTests` | Küme dışı kimlik → `RejectedUnsupported`; metin teslim edilmez |
| A10 | Atıfsız "yanıtlandı" veya ayrıştırılamayan çıktı | I `Ai.CitationValidationTests` | `RejectedUnsupported` |
| A11 | Üretim ortasında rol kaldırma | I `Ai.MidGenerationRevocationTests` | Engelle bekletilen sağlayıcı; sonuç `AccessChanged` veya `401`; metin teslim edilmez |
| A12 | Üretim ortasında belge emekli etme | I `Ai.MidGenerationRevocationTests` | `AccessChanged` |
| A13 | Çıktı kodlama ve bağlantı saldırıları | Bileşen `AssistAnswer.test.tsx` + E2E | `<script>`, `javascript:`, Markdown bağlantısı etkisiz düz metin |
| A14 | Kaynak alıntısı sınırları | U `Ai.SnippetBoundsTests` | Parça ≤ 1500, toplam ≤ 6000 karakter; denetim/sıfır genişlikli karakter yok |
| A15 | Talebe kaydetme ayrı ve yeniden yetkili | I `Support.SaveSuggestionTests` | Başkasının yanıtı / süresi dolmuş → `404`; kaynak emekli → `409 source_no_longer_available` |
| A16 | Sağlayıcı adresi kısıtı | U `Ai.EndpointAllowlistTests` | Loopback dışı ve listede olmayan adres → açılış hatası; yönlendirme izlenmez |
| A17 | Sahte sağlayıcı testleri ile gerçek model sonuçları ayrı | Doğrulama raporu | `ai-live` kapısı ayrı satır; model yoksa `not-run` |
| A18 | Gerçek model varsa küçük altın soru seti | `ai-live` kapısı | Beklenen kaynak, çekimser kalma, enjeksiyon denemeleri; çıktı elle değerlendirilir, olduğu gibi raporlanır |

Demo/sahte sağlayıcıyla model kalitesi, halüsinasyon oranı veya yararlılık ölçümü **üretilmez**. Bu testler genel bir model kıyaslaması veya güvenlik sertifikası değildir. Atıf kimliğinin geçerli olması yanıtın kaynakça desteklendiğini kanıtlamaz; bu, otomatik testin kapsamı dışındadır.

## 6. Doğrulama kapıları

`scripts/verify.ps1 -Gate <ad>[,<ad>…]` kapıları adıyla ve verilen sırayla çalıştırır.

| Kapı | İçerik | Asgari boş bellek |
| --- | --- | --- |
| `restore` | Kilitli (`--locked-mode`) geri yükleme | yalnız ölçüm |
| `build` | Release build | 700 MB |
| `unit` | Birim testleri | 700 MB |
| `integration` | Gerçek SQL Server üzerinde entegrasyon testleri | 1200 MB |
| `web` | `npm ci`, tip denetimi, lint, bileşen testleri, üretim derlemesi | 700 MB |
| `migrations` | Boş veritabanına uygulama; önceki aşama + sentetik veri → yükseltme; bekleyen model değişikliği denetimi | 1200 MB |
| `e2e` | Kendi Compose yığını (`kantar-e2e`, 8963) + Playwright (tek worker) + axe | 1500 MB |
| `smoke` | `kantar-smoke` (8953): HTTPS, kalıcılık, yeniden başlatma, veritabanı indir/kaldır, yardım kapalıyken ERP | 1200 MB |
| `scans` | Gizli değer taraması (olumlu + yanlış-olumlu kontrol), yazar/atıf denetimi, bağımlılık ve lisans dökümü | yalnız ölçüm |
| `ai-live` | Yalnız yapılandırılmış ve erişilebilir yerel model varsa; veritabanı yığını indirildikten sonra, en son | modele göre yapılandırılır (varsayılan: `e2e` eşiği) |

Eşikler ortam değişkeniyle değiştirilebilir (`KANTAR_VERIFY_MIN_FREE_MB_<KAPI>`).

Davranış:

1. Her kapıdan önce boş fiziksel bellek ölçülür ve günlüğe yazılır.
2. Eşiğin altındaysa betik sınırlı süre bekleyip yeniden ölçer; hâlâ düşükse kapıyı **başlatmaz**, kontrol noktasına `blocked-memory` yazar ve **3** koduyla çıkar. Bellek açmak için komşu süreç veya container durdurulmaz; yalnız bu görevin sahip olduğu boştaki build sunucusu ve test yığını kapatılır.
3. Ağır kapılar arasında betiğin başlattığı yığın, sahiplik etiketi doğrulanarak indirilir.
4. Çıkış kodları: **0** istenen kapıların tümü geçti; **1** en az bir kapı başarısız; **3** bellek nedeniyle kontrollü durma (başarı da başarısızlık da değildir).

Kontrol noktası `artifacts/verification/checkpoints.json`, **kaynak parmak izine** göre anahtarlanır:

```json
{ "<parmak izi>": { "gates": { "integration": {
    "result": "passed | failed | blocked-memory | not-run",
    "exit": 0, "total": 0, "passed": 0, "failed": 0, "skipped": 0,
    "startedAtUtc": "…", "finishedAtUtc": "…", "freeMbBefore": 0,
    "invocationId": "…", "log": "artifacts/verification/raw/…" } } } }
```

Kurallar:

- Parmak izi kaynak dosyaların içerik özetidir; `docs/`, `artifacts/`, `design/` ve `README.md` kapsam dışıdır ve bu kapsam raporda yazılır.
- Kaynak değişmediyse geçmiş bir kapı yeniden koşulmaz; değiştiyse eski sonuçlar "eski parmak izi" olarak ayrı tutulur ve güncel sonuç sayılmaz.
- Aynı parmak izinde **ayrı çağrılarla** sırayla geçen kapılar kabul edilir, ancak rapor her kapının `invocationId` ve zamanını gösterir; sonuç hiçbir yerde "tek `verify` çağrısında geçti" diye sunulmaz.
- Test sayıları (bulunan, geçen, kalan, atlanan) günlükten ayrıştırılır. Atlanan test varsa kritik kapı geçmiş sayılmaz. Başarısızlık nedeni uygulama / fixture / ortam olarak ayrı etiketlenir.
- Betik adları, kapı adları ve günlük dosya adları kültürden bağımsızdır; betik testleri `tr-TR` ve `en-US` altında koşar.

## 7. Yapılmayanlar

Yük/performans testi, çok örnekli dağıtım testi, elle ekran okuyucu testi ve uzak CI çalıştırması bu planın kapsamında değildir; yapılmadıkça sonuç raporunda yapılmış gibi yer almaz. Bağımlılık taramasının "bulgu yok" sonucu, yazılımın güvenli olduğu anlamına gelmez; kullanılan araç, kaynak ve tarama zamanı raporda belirtilir.
