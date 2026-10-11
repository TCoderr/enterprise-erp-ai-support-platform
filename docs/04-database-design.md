# 04 — Veritabanı Tasarımı

Sağlayıcı: **SQL Server 2022 (16.x) Developer**, imaj `mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04`. Erişim: EF Core 10 (`Microsoft.EntityFrameworkCore.SqlServer`) + `Microsoft.Data.SqlClient`. Seçim gerekçesi ve ölçümler: `adr/0001-database-provider.md`.

Developer sürümü yalnız geliştirme ve test için lisanslıdır; bu depo üretim dağıtımı tanımlamaz.

## 1. Veritabanı düzeyi ayarlar

| Ayar | Değer | Neden |
| --- | --- | --- |
| `READ_COMMITTED_SNAPSHOT` | `ON` | Okuma sorguları yazarları beklemez; yazma yolu kilitlerini açık ipuçlarıyla alır |
| `ALLOW_SNAPSHOT_ISOLATION` | `OFF` | Kullanılmıyor |
| Harmanlama | `Turkish_100_CI_AS_SC_UTF8` (veritabanı) | Türkçe sıralama ve `i/İ`, `ı/I` eşleşmesi; kod/SKU sütunları ayrıca `Latin1_General_100_BIN2` |
| `MSSQL_MEMORY_LIMIT_MB` | 1280 | SQL Server belleğini sınırlar; container bellek sınırı 2 GB'ın altına **çekilmez** (resmî asgari gereksinim) |

Kod sütunları (`Sku`, `Code`, belge numaraları, iş referansları) ikili harmanlamadadır: benzersizlik kültürden bağımsız ve büyük/küçük harfe duyarlıdır. Uygulama bu değerleri yazmadan önce `ToUpperInvariant` ile normalleştirir.

## 2. Şemalar ve tablolar

Zaman sütunları `datetime2(3)` UTC'dir. Birincil anahtarlar aksi belirtilmedikçe `uniqueidentifier` (uygulama `Guid.CreateVersion7()` üretir. Not: SQL Server `uniqueidentifier` değerlerini son baytlardan başlayarak sıralar, v7'nin zaman öneki ise baştadır; bu yüzden bu anahtarlar SQL Server dizininde **sıralı değildir** ve parçalanma avantajı sağlamaz. Bu ölçekte kabul edildi; sıralama gereken her yerde `CreatedAt` + benzersiz ikincil anahtar kullanılır). Günlük tabloları `bigint identity` kullanır. Değişebilir belge başlıklarında `RowVersion rowversion` bulunur.

### `iam`

| Tablo | Önemli sütunlar | Kısıtlar |
| --- | --- | --- |
| `Users` | `UserName`, `DisplayName`, `PasswordHash`, `SecurityStamp`, `IsActive`, `MustChangePassword`, `PoApprovalLimit decimal(18,2)`, `FailedLoginCount`, `LockedUntil` | `UQ(UserName)`; `CK PoApprovalLimit >= 0` |
| `UserRoles` | `UserId`, `Role varchar(32)` | `PK(UserId, Role)`; `CK Role IN (...)` |
| `WarehouseGrants` | `UserId`, `WarehouseId`, `Level tinyint` | `PK(UserId, WarehouseId)`; `CK Level IN (1,2,3)` |
| `Sessions` | `Id`, `UserId`, `TokenHash binary(32)`, `CsrfSecret binary(32)`, `SecurityStamp`, `CreatedAt`, `ExpiresAt`, `LastSeenAt`, `RevokedAt` | `UQ(TokenHash)` |

### `md`

`Products` (`Sku`, `Name`, `Unit`, `ListPrice`, `ReorderLevel`, `IsActive`, `RowVersion`), `Customers` (`Code`, `Name`, `TaxNumber`, `Address`, `IsActive`, `RowVersion`), `Suppliers` (`Code`, `Name`, `IsActive`, `RowVersion`), `Warehouses` (`Code`, `Name`, `IsActive`, `RowVersion`). Hepsinde kod benzersizdir; `ListPrice >= 0`, `ReorderLevel >= 0`.

### `inv`

| Tablo | Önemli sütunlar | Kısıtlar |
| --- | --- | --- |
| `StockBalances` | `WarehouseId`, `ProductId`, `OnHand int`, `Reserved int`, `RowVersion` | `PK(WarehouseId, ProductId)`; `CK Reserved >= 0`; `CK OnHand >= Reserved` |
| `StockMovements` | `Id bigint identity`, `WarehouseId`, `ProductId`, `Type tinyint`, `QuantityDelta int`, `OnHandAfter int`, `ReferenceType tinyint`, `ReferenceId`, `ReferenceLineId`, `ActorUserId`, `OccurredAt` | `CK QuantityDelta <> 0`; `CK OnHandAfter >= 0`; `CK` tür–işaret uyumu; `UQ(ReferenceType, ReferenceLineId, Type)` |
| `ReservationEntries` | `Id bigint identity`, `WarehouseId`, `ProductId`, `QuantityDelta int`, `ReservedAfter int`, `Reason tinyint`, `SalesOrderId`, `SalesOrderLineId`, `ShipmentLineId?`, `ActorUserId`, `OccurredAt` | `CK QuantityDelta <> 0`; `CK ReservedAfter >= 0`; `CK` neden–işaret uyumu |
| `StockTransfers` / `StockTransferLines` | `Number`, `Reference`, `FromWarehouseId`, `ToWarehouseId`, `Note`, `CreatedBy`, `CreatedAt` / `ProductId`, `Quantity` | `UQ(Number)`; `UQ(Reference)`; `CK From <> To`; `CK Quantity > 0`; `UQ(TransferId, ProductId)` |
| `StockAdjustments` / `StockAdjustmentLines` | `Number`, `Reference`, `WarehouseId`, `ReasonCode`, `Note`, … / `ProductId`, `QuantityDelta` | `UQ(Number)`; `UQ(Reference)`; `CK QuantityDelta <> 0`; `UQ(AdjustmentId, ProductId)` |

`UQ(ReferenceType, ReferenceLineId, Type)` aynı belge satırının aynı türde iki hareket üretmesini veritabanı düzeyinde engeller (transferde bir satır `TransferOut` ve `TransferIn` olmak üzere iki tür üretir; bu yüzden `Type` anahtara dahildir).

### `sales`

| Tablo | Önemli sütunlar | Kısıtlar |
| --- | --- | --- |
| `SalesOrders` | `Number`, `Status tinyint`, `CustomerId`, `WarehouseId`, `OwnerUserId`, snapshot sütunları, `ConfirmedAt`, `ClosedAt`, `CreatedAt`, `RowVersion` | `UQ(Number)`; `CK Status IN (...)` |
| `SalesOrderLines` | `OrderId`, `LineNo`, `ProductId`, `Sku`, `ProductName`, `Unit`, `UnitPrice`, `OrderedQty`, `ShippedQty`, `CancelledQty` | `UQ(OrderId, ProductId)`; `CK OrderedQty BETWEEN 1 AND 100000`; `CK ShippedQty >= 0 AND CancelledQty >= 0 AND ShippedQty + CancelledQty <= OrderedQty` |
| `Shipments` / `ShipmentLines` | `Number`, `SalesOrderId`, `WarehouseId`, `DispatchReference`, `CreatedBy`, `CreatedAt` / `SalesOrderLineId`, `ProductId`, `Quantity` | `UQ(Number)`; `UQ(SalesOrderId, DispatchReference)`; `CK Quantity > 0`; `UQ(ShipmentId, SalesOrderLineId)` |

### `purch`

| Tablo | Önemli sütunlar | Kısıtlar |
| --- | --- | --- |
| `PurchaseOrders` | `Number`, `Status`, `SupplierId`, `WarehouseId`, `OwnerUserId`, snapshot sütunları, `ApprovedBy`, `ApprovedAt`, `RowVersion` | `UQ(Number)` |
| `PurchaseOrderLines` | `OrderId`, `LineNo`, `ProductId`, snapshot, `UnitCost`, `OrderedQty`, `ReceivedQty`, `CancelledQty` | `UQ(OrderId, ProductId)`; `CK ReceivedQty + CancelledQty <= OrderedQty`; `CK UnitCost >= 0` |
| `GoodsReceipts` / `GoodsReceiptLines` | `Number`, `PurchaseOrderId`, `SupplierId`, `WarehouseId`, `ExternalReference`, … / `PurchaseOrderLineId`, `ProductId`, `Quantity` | `UQ(Number)`; `UQ(SupplierId, ExternalReference)`; `CK Quantity > 0`; `UQ(ReceiptId, PurchaseOrderLineId)` |

### `sup`

`Tickets` (`Number`, `RequesterUserId`, `AssigneeUserId`, `Status`, `Priority`, `Module`, `Screen`, `ErrorCode`, `RelatedDocumentType`, `RelatedDocumentNumber`, `DiagnosticJson`, `RowVersion`), `TicketComments` (`TicketId`, `AuthorUserId`, `Body`, `IsInternal`, `Source`, `AiProvider`, `AiModel`, `CitationsJson`, `CreatedAt`), `KbDocuments` (`Slug` benzersiz, `Visibility`, `Module`, `WarehouseId`, `ErrorCode`, `Status`, `PublishedVersionId`, `RowVersion`), `KbDocumentVersions` (`DocumentId`, `VersionNo`, `Title`, `BodyMarkdown`, `AuthorUserId`, `CreatedAt`; `UQ(DocumentId, VersionNo)`), `KbChunks` (`VersionId`, `DocumentId`, `SectionNo`, `Heading`, `Text`; `UQ(VersionId, SectionNo)`).

### `ai`

`Answers` (`Id`, `UserId`, `SessionId`, `Status`, `Provider`, `Model`, `AnswerText`, `LimitationsText`, `CitationsJson`, `CreatedAt`, `ExpiresAt`). Soru metni saklanmaz. Satır yalnız sahibine okunur; 30 dakikada süresi dolar. `Usage` (`Id bigint`, `OccurredAt`, `Provider`, `Model`, `Status`, `DurationMs`, `SourceCount`) — kullanıcı ve içerik taşımaz.

### `ops`

| Tablo | Amaç |
| --- | --- |
| `IdempotencyRecords` | `UserId`, `Operation varchar(64)`, `IdempotencyKey varchar(128)`, `RequestHash binary(32)`, `HashVersion tinyint`, `ResponseStatus smallint`, `ResponseContentType`, `ResponseBody varbinary(max)`, `ResponseLocation`, `ResponseETag`, `CreatedAt`, `ExpiresAt`; `PK(UserId, Operation, IdempotencyKey)` |
| `AuditEntries` | `Id bigint identity`, `OccurredAt`, `ActorUserId`, `Action varchar(64)`, `EntityType`, `EntityId`, `WarehouseId?`, `CorrelationId`, `DetailJson` (kapalı alan kümesi, kişisel veri yok) |
| `OutboxMessages` | `Id`, `Type`, `PayloadJson`, `CreatedAt`, `AvailableAt`, `Attempts`, `LeaseOwner`, `LeaseUntil`, `LeaseVersion`, `ProcessedAt`, `LastErrorCode` |
| `OutboxConsumptions` | `PK(MessageId, Handler)` — tüketici tekrar etkisizliği |
| `Notifications` | Kullanıcıya süreç içi bildirim |
| `ReconciliationRuns` / `ReconciliationFindings` | Salt okunur mutabakat çalışmasının sonucu |
| `TestRunMarker` | Yalnız test veritabanlarında; sahiplik korumasının okuduğu çalışma kimliği |

Belge numaraları için şema başına `SEQUENCE` nesneleri (`sales.SalesOrderNumbers` vb.).

## 3. Sağlayıcıya özgü stratejiler

### 3.1 Stok ilkel işlemleri

Bakiye satırının varlığını garanti etme (yalnız girişlerde: mal kabul, transfer hedefi, pozitif düzeltme):

```sql
INSERT INTO inv.StockBalances (WarehouseId, ProductId, OnHand, Reserved)
SELECT @w, @p, 0, 0
WHERE NOT EXISTS (SELECT 1 FROM inv.StockBalances WITH (UPDLOCK, HOLDLOCK)
                  WHERE WarehouseId = @w AND ProductId = @p);
```

`UPDLOCK, HOLDLOCK` anahtar aralığını tutar; iki eşzamanlı ekleme birbirini bekler, ikincisi satırı görür ve eklemez. `MERGE` kullanılmaz.

Koşullu değişiklik (tek ifade, okuma ve yazma arasında boşluk yok):

```sql
UPDATE inv.StockBalances
SET OnHand = OnHand + @dOnHand, Reserved = Reserved + @dReserved
OUTPUT inserted.OnHand, inserted.Reserved
WHERE WarehouseId = @w AND ProductId = @p
  AND Reserved + @dReserved >= 0
  AND OnHand + @dOnHand >= Reserved + @dReserved;
```

0 satır dönerse iş kuralı ihlalidir (`insufficient_available` / `insufficient_reserved`); transaction geri alınır. `CHECK` kısıtları ikinci savunma hattıdır. Dönen `OnHand`/`Reserved` değerleri günlük satırının `…After` alanına yazılır.

Çok satırlı işlemlerde satırlar `(WarehouseId, ProductId)` artan sırada işlenir; transfer kaynak ve hedef satırlarını aynı sıralamaya koyar. Bu, iki işlemin aynı satır çiftini ters sırada kilitlemesini önler.

### 3.2 Belge kilidi

Durum değiştiren her belge komutu ilk iş olarak başlık satırını kilitler:

```sql
SELECT … FROM sales.SalesOrders WITH (UPDLOCK, ROWLOCK) WHERE Id = @id;
```

Aynı belgeye gelen ship/cancel, receive/cancel, receive/receive komutları sıraya girer; ikinci gelen birincinin commit edilmiş durumunu okur. Kilit sırası her komutta aynıdır: **belge başlığı → stok bakiyeleri (sıralı)**.

### 3.3 İyimser eşzamanlılık

`rowversion` sütunu EF Core eşzamanlılık belirtecidir ve HTTP `ETag` olarak (Base64) dışarı verilir. Draft düzenleme ve durum komutları `If-Match` ister. Stok bakiyesi için `rowversion` yalnız bilgi amaçlıdır; doğruluk koşullu `UPDATE` ile sağlanır.

### 3.4 Benzersizlik ihlalleri

SQL Server'da 2601/2627 hatası transaction'ı kendiliğinden sonlandırmaz, ancak kod bunu varsaymaz: benzersizlik hatasından sonra aynı transaction'da başka sorgu çalıştırılmaz; transaction geri alınır, sonra yeni bağlamda mevcut kayıt okunur (idempotency ikizi → kayıtlı yanıt; iş referansı → `409`). `SET XACT_ABORT ON` kullanılmaz; savepoint kullanılmaz.

### 3.5 Yeniden deneme

`1205` (deadlock kurbanı), `1222` (kilit zaman aşımı; `SET LOCK_TIMEOUT 5000`), `3960` (anlık görüntü güncelleme çakışması) ve geçici bağlantı hataları için tüm transaction en fazla 3 kez, 50–200 ms rastgele beklemeyle yeniden çalıştırılır. EF Core'un `EnableRetryOnFailure` stratejisi kullanıcı transaction'larıyla birlikte kullanılmaz; yeniden deneme `IUnitOfWork.ExecuteAsync` içindedir ve her denemede `ChangeTracker` temizlenir.

### 3.6 Outbox kiralama

```sql
WITH next AS (
  SELECT TOP (@n) * FROM ops.OutboxMessages WITH (READPAST, UPDLOCK, ROWLOCK)
  WHERE ProcessedAt IS NULL AND AvailableAt <= @now AND (LeaseUntil IS NULL OR LeaseUntil < @now)
  ORDER BY CreatedAt, Id)
UPDATE next SET LeaseOwner = @me, LeaseUntil = @until, LeaseVersion = LeaseVersion + 1, Attempts = Attempts + 1
OUTPUT inserted.Id, inserted.Type, inserted.PayloadJson, inserted.LeaseVersion, inserted.Attempts;
```

`READPAST` kilitli satırları atlar (PostgreSQL `SKIP LOCKED` karşılığı değil, SQL Server'ın kendi ipucu; `READ COMMITTED` altında geçerlidir).

### 3.7 Sayfalama

Listeler anahtar kümesi (keyset) sayfalaması kullanır: `ORDER BY CreatedAt DESC, Id DESC`, imleç `(CreatedAt, Id)` çiftinin opak kodlamasıdır. Günlüklerde `ORDER BY Id DESC`. `OFFSET` kullanılmaz. Sayfa boyutu 1–100, varsayılan 25.

## 4. Roller ve yetkiler

| Giriş / kullanıcı | Kullanım | Yetki |
| --- | --- | --- |
| `sa` | Yalnız container ilk kurulumu ve test fixture'ı | – |
| `kantar_migrator` | Yalnız `--migrate` | Veritabanında `db_owner` |
| `kantar_app` | Web ve worker | Aşağıdaki açık izinler; `db_datareader`/`db_datawriter` üyeliği **yok** |

`kantar_app` izinleri (`db/grants.sql`, her migration sonrasında yeniden uygulanır, tekrar çalıştırılabilir):

- Tüm uygulama şemalarında `SELECT`.
- Değişebilir tablolarda `INSERT, UPDATE`; `DELETE` yalnız süresi dolan satırların temizlendiği tablolarda (`iam.Sessions`, `ops.IdempotencyRecords`, `ai.Answers`, `ops.Notifications`) ve Draft satır tablolarında (`SalesOrderLines`, `PurchaseOrderLines`).
- **Yalnız ekleme** tabloları — `inv.StockMovements`, `inv.ReservationEntries`, `ops.AuditEntries`, `sup.KbDocumentVersions`, `sup.TicketComments`, `sup.KbChunks`, oluşturulduğu anda kesinleşen belgeler ve satırları (`Shipments`/`ShipmentLines`, `GoodsReceipts`/`GoodsReceiptLines`, `StockTransfers`/`StockTransferLines`, `StockAdjustments`/`StockAdjustmentLines`): `INSERT` verilir, `UPDATE` ve `DELETE` için açık `DENY` yazılır. `SalesOrderLines` ve `PurchaseOrderLines` bu gruba girmez: miktar alanları güncellenir ve Draft'ta satır silinir.
- `ALTER` hiçbir nesnede yok → `TRUNCATE TABLE` reddedilir. `CONTROL`, `REFERENCES`, DDL yok.
- Sequence'lerde `UPDATE` (yani `NEXT VALUE FOR`).

Bu izinler entegrasyon testlerinde **gerçek `kantar_app` girişiyle** sınanır: günlük tablolarında `UPDATE`, `DELETE`, `TRUNCATE` denemelerinin hata verdiği ve satır sayısının değişmediği doğrulanır. Uygulama testleri migrator girişiyle çalıştırılmaz.

## 5. Migration ve seed

- Migration'lar EF Core ile üretilir, `Kantar.Infrastructure/Persistence/Migrations` altındadır. Uygulama web modunda migration çalıştırmaz.
- `--migrate`: veritabanı ayarları → `Database.Migrate()` → `db/grants.sql`. Tekrar çalıştırılabilir.
- `--seed` **`kantar_app` girişiyle** çalışır (migrator girişi kullanılmaz; böylece seed de kısıtlı rolün izinleriyle sınanmış olur). Açılış belgelerinin sahibi, oturum açamayan (`IsActive = 0`) teknik kullanıcı `sistem.acilis`'tir; bu kullanıcı yalnız seed süreci içinde, bellekte kurulan bir çağıran kimliği olarak kullanılır.
- `--seed`: ana veriyi kodlarına göre ekler (varsa dokunmaz); demo kullanıcılarını gizli dosyalardaki parolalarla oluşturur; açılış stokunu **uygulama komutlarıyla** (onaylı PO + mal kabul, sabit iş referanslarıyla) girer. İkinci çalıştırma iş referansı çakışmasını "zaten yapılmış" olarak görür ve stok eklemez. Seed sonunda mutabakat çalışır ve fark varsa süreç hata koduyla biter.
- Doğrulama: boş veritabanına uygulama, bir önceki aşamanın migration'ına kadar uygulanıp sentetik veri eklendikten sonra yükseltme, `has-pending-model-changes` denetimi.

## 6. Mutabakat sorguları

Her (depo, ürün) için dört eşitlik denetlenir; hiçbiri veri değiştirmez:

1. `OnHand = Σ StockMovements.QuantityDelta`
2. `Reserved = Σ ReservationEntries.QuantityDelta`
3. `Reserved = Σ (Confirmed/PartiallyShipped siparişlerin OrderedQty − ShippedQty − CancelledQty)`
4. En son hareketin `OnHandAfter` değeri `= OnHand`; en son rezervasyon kaydının `ReservedAfter` değeri `= Reserved`

Fark, `ReconciliationFindings` satırı ve `ops` ekranında `DriftFound` uyarısı olarak görünür. Sistem kendi kendine düzeltme yapmaz ve farkı "onarıldı" diye raporlamaz.

İki durum birbirinden ayrıdır:

- **Günlük ile bakiye arasındaki fark** (bu sorguların bulduğu): uygulama komutları bakiye ve günlüğü aynı transaction'da yazdığı için normal işleyişte oluşmaz; uygulama dışı bir yazma ya da bir kusur demektir. Uygulama içinden onarım yolu **yoktur**; stok düzeltmesi bakiyeyi ve günlüğü birlikte değiştirdiği için bu farkı kapatmaz. İnceleme ve düzeltme veritabanı yöneticisinin kayıt altındaki işidir; rapor, fark giderilene kadar `DriftFound` kalır.
- **Fiziksel sayım ile sistem arasındaki fark**: mutabakatın konusu değildir. Depo sorumlusu gerekçeli bir stok düzeltmesi girer; bu normal, yetkili ve günlüğe yazılan bir işlemdir.
