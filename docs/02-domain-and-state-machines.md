# 02 — Alan Modeli ve Durum Makineleri

Bu belge Kantar'ın iş kurallarını tanımlar. Kod ve testler bu belgeyle çelişirse belge değil, önce çelişkinin kendisi düzeltilir ve `docs/implementation-progress.md` içine not düşülür.

## 1. Sözlük

| Terim | Anlam |
| --- | --- |
| Depo (Warehouse) | Stok tutulan fiziksel yer. Her iş belgesi tam olarak bir depoya bağlıdır (transfer: kaynak + hedef). |
| Stok bakiyesi (StockBalance) | Bir depo + ürün çifti için `OnHand` ve `Reserved`. `Available = OnHand − Reserved` türetilir, saklanmaz. |
| Stok hareketi (StockMovement) | `OnHand` değerini değiştiren, değiştirilemez günlük satırı. |
| Rezervasyon kaydı (ReservationEntry) | `Reserved` değerini değiştiren, değiştirilemez ayrı defter satırı. |
| Satış siparişi (SalesOrder) | Müşteriye taahhüt edilen ticari belge. Tahsilat/fatura değildir. |
| Sevkiyat (Shipment) | Bir satış siparişinin bir kısmının depodan çıkışı. |
| Satın alma siparişi (PurchaseOrder, PO) | Tedarikçiye verilen sipariş. |
| Mal kabul (GoodsReceipt) | Bir PO'nun bir kısmının depoya girişi. |
| Transfer (StockTransfer) | İki depo arasında tek transaction'lı iç aktarım. |
| Düzeltme (StockAdjustment) | Gerekçeli +/− stok düzeltmesi. |
| Destek talebi (Ticket) | Uygulama kullanımıyla ilgili destek kaydı. |
| Bilgi belgesi (KbDocument) | Sürümlü, görünürlük sınıflı bilgi bankası belgesi. |

Miktarlar tam sayıdır (`int`, tek birim). Para `decimal(18,2)` TRY'dir; kur, vergi ve iskonto ilk sürümde yoktur.

## 2. Roller, depo kapsamı ve kaynak sahipliği

### 2.1 Roller

`SalesAgent`, `SalesManager`, `PurchasingAgent`, `WarehouseOperator`, `SupportAgent`, `SupportManager`, `SystemAdmin`. Bir kullanıcının birden çok rolü olabilir. Roller her istekte veritabanından okunur; oturum içinde önbelleğe alınmış rol karar için kullanılmaz.

### 2.2 Depo yetkisi (WarehouseGrant)

Kullanıcı–depo çifti başına bir seviye:

| Seviye | Anlam |
| --- | --- |
| `View` | Depodaki stok ve o depoya bağlı (rolünün izin verdiği) belgeleri görür. |
| `Operate` | `View` + sevkiyat, mal kabul, transfer işlemleri. |
| `Supervise` | `Operate` + stok düzeltmesi. "Depo sorumlusu" yetkisidir. |

Depo yetkisi tek başına iş yetkisi vermez; rol + depo seviyesi birlikte aranır. `SystemAdmin` için örtük depo yetkisi yoktur.

### 2.3 Satın alma onay limiti

`PurchasingAgent` kullanıcısının `PoApprovalLimit` (TRY, varsayılan 0) alanı vardır. PO toplamı limitten büyükse onay 403 `approval_limit_exceeded` ile reddedilir. Limit 0 ise kullanıcı hiçbir PO'yu onaylayamaz. Onaylayan kişi PO sahibi olabilir; dört göz ilkesi ilk sürümde yoktur (bkz. `known-limitations.md`).

### 2.4 Yetki matrisi

D = depo yetkisi seviyesi (belgenin deposu için). "kendi" = `OwnerUserId` çağıran kullanıcı.

| İşlem | SalesAgent | SalesManager | PurchasingAgent | WarehouseOperator | SupportAgent | SupportManager | SystemAdmin |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Ürün/depo listesini oku (ad, SKU, birim) | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Ürün satış fiyatını oku | ✔ | ✔ | – | – | – | – | ✔ |
| Müşteri oku | ✔ | ✔ | – | – | – | – | ✔ |
| Tedarikçi oku | – | – | ✔ | – | – | – | ✔ |
| Ana veri yönet (ürün, müşteri, tedarikçi, depo, fiyat) | – | – | – | – | – | – | ✔ |
| Stok bakiyesi/hareket oku | D≥View | D≥View | D≥View | D≥View | – | – | – |
| Satış siparişi oluştur/düzenle (Draft) | kendi, D≥View | D≥View | – | – | – | – | – |
| Satış siparişi oku | kendi, D≥View | D≥View | – | D≥Operate (yalnız Confirmed ve sonrası, fiyatsız görünüm) | – | – | – |
| Satış siparişi onayla (confirm) | kendi, D≥View | D≥View | – | – | – | – | – |
| Satış siparişi iptal / kalan iptal | kendi, D≥View | D≥View | – | – | – | – | – |
| Sevkiyat oluştur | – | – | – | D≥Operate | – | – | – |
| PO oluştur/düzenle (Draft) | – | – | kendi, D≥View | – | – | – | – |
| PO oku | – | – | D≥View | D≥Operate (yalnız Approved ve sonrası, fiyatsız görünüm) | – | – | – |
| PO onayla | – | – | D≥View + limit | – | – | – | – |
| PO iptal / kalan iptal | – | – | kendi veya onay limiti olan, D≥View | – | – | – | – |
| Mal kabul | – | – | – | D≥Operate | – | – | – |
| Transfer | – | – | – | kaynak D≥Operate **ve** hedef D≥Operate | – | – | – |
| Stok düzeltmesi | – | – | – | D=Supervise | – | – | – |
| Destek talebi aç, kendi talebini oku/yorumla | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |
| Tüm talepleri oku, yorumla, iç not yaz, durum değiştir | – | – | – | – | ✔ | ✔ | – |
| Talep ata | – | – | – | – | kendine | ✔ | – |
| KB taslağı yaz | – | – | – | – | ✔ | ✔ | – |
| KB yayınla / emekli et | – | – | – | – | – | ✔ | – |
| KB oku, kaynaklı yardım sor | görünürlük kuralına göre (bkz. 9.3) |||||||
| Kullanıcı/rol/depo yetkisi yönet, parola sıfırla | – | – | – | – | – | – | ✔ |
| Denetim kaydı, outbox, mutabakat raporu oku | – | – | – | – | – | – | ✔ |

Sonuçlar:

- `SystemAdmin` satış siparişi, PO, stok bakiyesi ve `manager-only`/`support-only` bilgi belgelerini **okuyamaz**. Bunları görmesi gerekiyorsa ilgili rol ve depo yetkisi ayrıca verilir; bu atama denetim kaydına düşer.
- Kapsam dışı bir belgeye erişim, belge yokmuş gibi `404 not_found` döner (varlık sızdırılmaz). Rolü hiç olmayan bir işlem türü `403 forbidden` döner (işlem türünün varlığı gizli değildir).
- Destek talebindeki belge bağlantısı yetki vermez: bağlantı yalnızca tür + numara metnidir; belge ayrıntısı normal belge uç noktasından, normal yetkiyle okunur.

## 3. Ana veri

| Varlık | Alanlar | Kurallar |
| --- | --- | --- |
| Product | `Sku` (benzersiz, büyük harf, `[A-Z0-9-]{3,32}`), `Name`, `Unit` (ör. `ADET`, `KOLI`), `ListPrice` (TRY), `ReorderLevel` (int ≥ 0), `IsActive` | SKU ve birim, üründe hareket/belge satırı oluştuktan sonra değiştirilemez. |
| Customer | `Code` (benzersiz), `Name`, `TaxNumber` (sentetik), `Address`, `IsActive` | |
| Supplier | `Code` (benzersiz), `Name`, `IsActive` | |
| Warehouse | `Code` (benzersiz), `Name`, `IsActive` | |

Pasifleştirme: pasif ana veri yeni belgeye/satıra eklenemez ve Draft belgenin onayı reddedilir (`master_data_inactive`). Onaylanmış belgeler etkilenmez; sevkiyat ve mal kabul pasif ürün için de tamamlanır, çünkü taahhüt snapshot ile verilmiştir. Ana veri silinmez.

### Snapshot politikası

| Belge | Ne zaman | Kopyalanan |
| --- | --- | --- |
| Satış siparişi satırı | Satır yazıldığında (sunucu okur, istemci fiyat gönderemez) | `Sku`, `ProductName`, `Unit`, `UnitPrice` |
| Satış siparişi başlığı | Confirm anında (Draft'ta her yazışta güncellenir) | `CustomerCode`, `CustomerName`, `CustomerTaxNumber`, `ShippingAddress` |
| PO satırı | Satır yazıldığında | `Sku`, `ProductName`, `Unit`; `UnitCost` alıcının girdiği değerdir |
| PO başlığı | Approve anında | `SupplierCode`, `SupplierName` |
| Hareket/sevkiyat/kabul satırı | Oluştuğunda | `ProductId` + belge satırı referansı (ad/SKU belge satırındaki snapshot'tan okunur) |

Confirm/Approve sonrası snapshot alanları değişmez. Fiyat listesindeki değişiklik mevcut Draft satırlarını güncellemez; satır yeniden yazılırsa yeni fiyat alınır.

## 4. Stok

### 4.1 Miktar muhasebesi

Her (depo, ürün) için:

```
OnHand    = Σ StockMovement.QuantityDelta
Reserved  = Σ ReservationEntry.QuantityDelta
          = Σ (onaylı, açık satış satırlarının OpenQty'si)
Available = OnHand − Reserved
Değişmezler: Reserved ≥ 0, OnHand ≥ Reserved  (dolayısıyla Available ≥ 0, OnHand ≥ 0)
```

Değişmezler veritabanında `CHECK` kısıtıdır; uygulama hatası olsa bile ihlal eden satır yazılamaz.

### 4.2 Hareket türleri

| Tür | İşaret | Kaynak belge | OnHand | Reserved |
| --- | --- | --- | --- | --- |
| `Receipt` | + | GoodsReceipt satırı | +q | – |
| `Shipment` | − | Shipment satırı | −q | −q (`ReleaseOnShipment`) |
| `TransferOut` | − | StockTransfer satırı (kaynak depo) | −q | – |
| `TransferIn` | + | StockTransfer satırı (hedef depo) | +q | – |
| `Adjustment` | +/− | StockAdjustment satırı | ±q | – |

Rezervasyon defteri nedenleri: `Reserve` (+q, confirm), `ReleaseOnShipment` (−q), `ReleaseOnCancel` (−q).

Her hareket satırı `OnHandAfter`, her rezervasyon satırı `ReservedAfter` taşır; mutabakat hem toplamı hem son değeri karşılaştırır.

### 4.3 Kurallar

- Negatif düzeltme `OnHand − q ≥ Reserved` koşulunu sağlamazsa reddedilir (`insufficient_available`). Rezerve stok düzeltmeyle yok edilemez.
- Transfer yalnızca `Available` miktarı taşıyabilir; kaynak ve hedef aynı depo olamaz; kaynak düşüş ve hedef artış aynı transaction'dadır.
- Siparişten bağımsız stok girişi yoktur. Açılış/demo stoku da onaylı bir PO ve mal kabul belgesiyle girer; böylece her adet hareket günlüğünde izlenir.
- Düzeltme gerekçe kodu ister: `CountDifference`, `Damage`, `Loss`, `Found`, `DataCorrection` + serbest metin not (zorunlu, 10–500 karakter).
- Düşük stok listesi: `Available ≤ Product.ReorderLevel` olan aktif ürün bakiyeleri. Değerleme, sayım oturumu ve maliyet yoktur.

### 4.4 İş referansları (idempotency anahtarından bağımsız benzersizlik)

| Belge | Benzersiz iş referansı | Çakışma davranışı |
| --- | --- | --- |
| GoodsReceipt | (`SupplierId`, `ExternalReference`) — tedarikçi irsaliye no | `409 duplicate_business_reference`; yanıt mevcut belgenin numarasını taşımaz (varlık çıkarımını azaltmak için). Kullanıcı kaydı belgenin kendi geçmişinden bulur |
| Shipment | (`SalesOrderId`, `DispatchReference`) | aynı |
| StockTransfer | `Reference` (genel benzersiz) | aynı |
| StockAdjustment | `Reference` (genel benzersiz) | aynı |

Aynı `Idempotency-Key` + aynı istek → kayıtlı yanıtın bayt bayt tekrarı. Farklı anahtar + aynı iş referansı → 409; ikinci stok etkisi oluşmaz.

## 5. Satış siparişi

### 5.1 Satır miktarları

```
OrderedQty = ShippedQty + CancelledQty + OpenQty        (OpenQty türetilir)
Draft:      ShippedQty = CancelledQty = 0, rezervasyon yok
Confirmed+: bu satır için rezerve miktar = OpenQty
```

En fazla 50 satır; aynı ürün bir siparişte bir kez; `1 ≤ OrderedQty ≤ 100000`.

### 5.2 Durum makinesi

```
Draft ──confirm──▶ Confirmed ──ship(kısmi)──▶ PartiallyShipped ──ship(kalanın tamamı)──▶ Shipped
  │                    │   └──ship(tamamı)──▶ Shipped                │
  │cancel              │cancel                                       │cancel remaining
  ▼                    ▼                                             ▼
Cancelled          Cancelled                                     Cancelled (sevk edilen korunur)
```

| Geçiş | Koşul | Etki (tek transaction) |
| --- | --- | --- |
| `confirm` | Draft; ≥1 satır; müşteri, depo, tüm ürünler aktif; her satır için `Available ≥ OrderedQty` | Tüm satırlar rezerve edilir (hepsi ya da hiçbiri); başlık snapshot'ı dondurulur; `Reserve` kayıtları |
| `ship` | Confirmed veya PartiallyShipped; her sevk satırı `1 ≤ q ≤ OpenQty`; `DispatchReference` sipariş içinde benzersiz | `ShippedQty += q`; `OnHand −= q`, `Reserved −= q`; `Shipment` hareketi + `ReleaseOnShipment`; tüm `OpenQty = 0` ise Shipped, değilse PartiallyShipped |
| `cancel` (Draft) | Draft | Stok etkisi yok → Cancelled |
| `cancel` (Confirmed / PartiallyShipped) | Açık miktar var | Her satırda `CancelledQty += OpenQty`; `Reserved −= OpenQty` (`ReleaseOnCancel`); `OnHand` değişmez → Cancelled |

`Shipped` ve `Cancelled` sondur; belge değişmez. Düzeltme gerekiyorsa yeni belge açılır (iade/RMA kapsam dışı). `Cancelled` durumundaki bir siparişte `ShippedQty > 0` olabilir: bu "kısmen sevk edildi, kalanı iptal edildi" demektir ve arayüzde böyle gösterilir.

Draft düzenleme (başlık ve satırlar) `If-Match` ile iyimser eşzamanlılık ister. Confirm sonrası satır eklenemez/değiştirilemez.

## 6. Satın alma siparişi

### 6.1 Satır miktarları

```
OrderedQty = ReceivedQty + CancelledQty + OpenQty
```

En fazla 50 satır; aynı ürün bir PO'da bir kez; `UnitCost ≥ 0`, en fazla 2 ondalık.

### 6.2 Durum makinesi

```
Draft ──approve──▶ Approved ──receive(kısmi)──▶ PartiallyReceived ──receive(kalanın tamamı)──▶ Received
  │                   │   └──receive(tamamı)──▶ Received                 │
  │cancel             │cancel                                            │cancel remaining
  ▼                   ▼                                                  ▼
Cancelled         Cancelled                                          Cancelled (kabul edilen korunur)
```

| Geçiş | Koşul | Etki |
| --- | --- | --- |
| `approve` | Draft; ≥1 satır; tedarikçi, depo, ürünler aktif; onaylayanın limiti ≥ toplam | Tedarikçi snapshot'ı dondurulur; `ApprovedBy/At` |
| `receive` | Approved veya PartiallyReceived; her satır PO'ya ait, `1 ≤ q ≤ OpenQty`; kabul deposu = PO deposu; (`SupplierId`, `ExternalReference`) benzersiz | `ReceivedQty += q`; `OnHand += q`; `Receipt` hareketi; durum güncellenir |
| `cancel` | Draft / Approved / PartiallyReceived | `CancelledQty += OpenQty`; stok etkisi yok |

Fazla kabul (over-receipt) yoktur. PO'da olmayan ürün, başka PO'nun satırı veya başka depo `422` ile reddedilir ve hiçbir satır yazılmaz.

## 7. Transfer ve düzeltme

Her ikisi de oluşturulduğu anda tamamlanan, tek durumlu (`Posted`) değişmez belgelerdir. Taslak yoktur.

- Transfer: 1–50 satır, `q ≥ 1`, aynı ürün bir kez. Kilit sırası (depo, ürün) kimliğine göre artandır; kaynak ve hedef satırları aynı sıralamaya girer.
- Düzeltme: 1–50 satır, `q ≠ 0`.

## 8. Eşzamanlılık ve yarış kuralları

| Senaryo | Beklenen sonuç |
| --- | --- |
| Aynı ürüne N eşzamanlı confirm, toplam talep > Available | Kazananların toplamı ≤ Available; kaybedenler `409 insufficient_available`, kısmi rezervasyon yok |
| Çok satırlı confirm'de bir satır yetersiz | Hiçbir satır rezerve edilmez |
| Aynı siparişe eşzamanlı ship ve cancel | Belge satır kilidi sıraya sokar. Önce ship: cancel yalnız kalan miktarı bırakır. Önce cancel: ship `409 invalid_state` |
| Aynı PO'ya paralel receive | Toplam kabul ≤ OrderedQty; fazlası `409 exceeds_remaining` |
| PO receive ile cancel yarışı | Önce receive: cancel kalan miktarı kapatır. Önce cancel: receive `409 invalid_state` |
| Negatif düzeltme ile confirm/ship yarışı | Koşullu güncelleme `OnHand − q ≥ Reserved` koşulunu satır kilidi altında değerlendirir; değişmez korunur |
| Transferde hedef yazımı hata verir | Kaynak düşüşü dahil tümü geri alınır |
| Deadlock / kilit zaman aşımı | En fazla 3 deneme; denemeler arasında durum sıfırdan okunur; hâlâ başarısızsa `503 retry_later` ve hiçbir etki yok |
| Eski `If-Match` | `412 precondition_failed`; eksik `If-Match` → `428 precondition_required`. Draft düzenleme, confirm/approve ve cancel için zorunludur; ship ve receive için isteğe bağlıdır (kalan miktar ve belge kilidi korur). Bu tablodaki `409` sonuçları, güncel sürümle ya da `If-Match` olmadan gelen isteğin aldığı sonuçtur; eski sürümle gelen istek önce `412` alır |

## 9. Destek merkezi

### 9.1 Destek talebi

Alanlar: `Number`, `RequesterUserId`, `Title` (5–160), `Description` (düz metin, ≤ 4000), `Priority` (`Low`/`Normal`/`High`/`Urgent`), `Status`, `AssigneeUserId?`, `Module` (`Sales`/`Purchasing`/`Inventory`/`Support`/`General`), `Screen?` (izinli ekran kodu listesi), `ErrorCode?` (`[A-Z]{3}-\d{3}`), `RelatedDocumentType?` + `RelatedDocumentNumber?` (`SalesOrder`/`PurchaseOrder`/`Shipment`/`GoodsReceipt`/`StockTransfer`/`StockAdjustment`), `Diagnostic?`.

```
Open ──▶ InProgress ⇄ WaitingForRequester
  │           │                 │
  └───────────┴────────▶ Resolved ──▶ Closed
                              └──(talep sahibi yeniden açar)──▶ InProgress
```

Durum değişikliği destek personelinindir; talep sahibi yalnızca `Resolved` talebi yeniden açabilir. `Closed` sondur.

Yorumlar düz metindir (≤ 4000), değiştirilemez. `IsInternal = true` yorumlar yalnızca destek personeline görünür. `Source`: `User` veya `AiSuggestion`.

### 9.2 Tanılama snapshot'ı

İstemci gönderemez; sunucu talep açılırken açık bir beyaz listeden üretir: uygulama sürümü, modül, ekran kodu, hata kodu, istek korelasyon kimliği, talep sahibinin rol adları, sunucu zamanı. Bağlantı dizesi, token, çerez, müşteri/tedarikçi verisi, belge içeriği, IP ve user-agent toplanmaz.

### 9.3 Bilgi bankası

- `KbDocument`: `Slug`, `Visibility` (`General` / `SupportOnly` / `ManagerOnly`), `Module`, `WarehouseId?`, `ErrorCode?`, `Status` (`Draft` → `Published` → `Retired`), `PublishedVersionId?`.
- `KbDocumentVersion`: değiştirilemez; `VersionNo`, `Title`, `BodyMarkdown`, yazar, zaman.
- `KbChunk`: yayınlanan sürümün bölümleri (`SectionNo`, `Heading`, `Text`). Yayınlama transaction'ında üretilir.

Okuma kuralı (hepsi birlikte sağlanmalı):

1. Belge `Published` ve parça `PublishedVersionId` sürümüne ait.
2. Görünürlük: `General` → oturum açmış herkes; `SupportOnly` → `SupportAgent`, `SupportManager`; `ManagerOnly` → `SupportManager`, `SalesManager`.
3. Modül: `General` → herkes; `Sales` → `SalesAgent`, `SalesManager`; `Purchasing` → `PurchasingAgent`; `Inventory` → `WarehouseOperator`, `SalesAgent`, `SalesManager`, `PurchasingAgent`; `Support` → herkes. Destek personeli tüm modülleri okur.
4. `WarehouseId` doluysa: destek personeli dışındakiler için o depoda `View` veya üstü yetki.

Taslak ve emekli belgeler yalnızca destek personelinin yönetim ekranında görünür; arama ve yardım yanıtına hiçbir koşulda girmez. Yeni sürüm yayınlamak eski sürümün parçalarını aynı transaction'da servis dışı bırakır (parçalar silinmez; sorgu `PublishedVersionId` ile filtreler).

### 9.4 Kaynaklı yardım

Kullanıcı soru sorar → sunucu yalnızca kullanıcının okuyabildiği parçaları getirir → yanıt üretici yalnızca bu parçaları görür → sunucu atıfları doğrular ve teslimden önce yetkiyi yeniden denetler. Yanıt durumu: `Answered`, `NoSource`, `ProviderUnavailable`, `RejectedUnsupported`, `AccessChanged`. Ayrıntı: `10-ai-grounding-and-access-design.md`.

Yanıtı talebe kaydetmek ayrı bir komuttur: kullanıcı açıkça onaylar, sunucu yorum yazma yetkisini ve kaynakların hâlâ okunabilir olduğunu yeniden denetler, yorum `Source = AiSuggestion` ve sağlayıcı/sürüm etiketiyle yazılır.

## 10. Belge numaraları

`SO-`, `SH-`, `PO-`, `GR-`, `TR-`, `AD-`, `TK-` önekleri + veritabanı sequence değeri (8 hane). Sequence transaction dışıdır; geri alınan işlemler boşluk bırakabilir. Numaralar kimlik değil görüntü amaçlıdır; yasal belge numarası iddiası yoktur.
