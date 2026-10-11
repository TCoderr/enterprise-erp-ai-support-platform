# 06 — Güvenlik ve Hata Davranışı

Bu belge tehditleri, karşı önlemleri ve bir şey ters gittiğinde beklenen davranışı tanımlar. Her önlem `09-test-and-verification-plan.md` içinde bir teste bağlanır; testi olmayan önlem "var" sayılmaz. Buradaki önlemler yerel bir demo/portföy kurulumu içindir; üretim güvenlik değerlendirmesi yapılmamıştır.

## 1. Kimlik doğrulama ve oturum

| Tehdit | Önlem |
| --- | --- |
| Çerez hırsızlığı (XSS, ağ) | `__Host-kantar-session`: `Secure; HttpOnly; SameSite=Lax; Path=/`, alan adı yok. Çerezde yalnız rastgele belirteç vardır; sunucu SHA-256 özetini saklar (`iam.Sessions.TokenHash`). Yalnız HTTPS |
| Ele geçirilmiş çerezin uzun süre kullanılması | Boşta kalma 30 dk, mutlak süre 8 saat. Çıkış, parola değiştirme, parola sıfırlama, rol değişikliği ve pasifleştirme oturumu düşürür |
| Yetki kalktıktan sonra eski yetkiyle işlem | Roller ve depo yetkileri **her istekte** veritabanından okunur; oturumda saklanan rol karar için kullanılmaz |
| Parola tahmini | 5 hatalı denemeden sonra 15 dk hesap kilidi; giriş ucunda kaynak IP başına hız sınırı; kullanıcı var/yok ayrımı yapmayan tek tip `401` |
| Parola sızıntısı | PBKDF2 tabanlı özet (`PasswordHasher`); parola günlüğe, hata iletisine, denetim kaydına yazılmaz |
| Herkese açık yönetici oluşturma | Kayıt (signup) ucu yok. Kullanıcıyı yalnız `SystemAdmin` oluşturur. İlk yönetici yalnız `--seed` modunda, yerel gizli dosyadaki parolayla oluşur; web süreci kullanıcı tohumlamaz |

Oturum iptali kuralları:

| Olay | Etki |
| --- | --- |
| `POST /api/auth/logout` | O oturum `RevokedAt` alır; yakalanmış eski çerez `401` döner |
| Parola değiştirme | `SecurityStamp` yenilenir → diğer tüm oturumlar geçersiz; çağıran oturum yeni belirteçle yenilenir |
| Yönetici parola sıfırlama | Kullanıcının tüm oturumları iptal; `MustChangePassword = true` (parola değişene kadar diğer uçlar `403 password_change_required`) |
| Rol değişikliği | `SecurityStamp` yenilenir → kullanıcının oturumları düşer |
| Depo yetkisi değişikliği | Oturum düşmez; bir sonraki istekte güncel yetki okunur |
| Kullanıcı pasifleştirme | Oturum çözümleme başarısız → `401` |

Bilinen sınır: hesap kilidi, kullanıcı adını bilen birinin hesabı geçici olarak kilitlemesine izin verir. Yerel demo için kabul edildi; `known-limitations.md` içinde yer alır.

## 2. CSRF

Akış (aynı origin, üçüncü taraf çerez yok):

1. **Anonim:** tarayıcı `GET /api/auth/csrf` çağırır, anonim belirteç alır. Bu belirteç yalnız `POST /api/auth/login` için geçerlidir.
2. **Giriş:** `POST /api/auth/login` `X-CSRF-Token` ile gelir. Başarılı girişte yeni oturum ve oturuma özgü `CsrfSecret` oluşur.
3. **Oturumlu:** tarayıcı `GET /api/auth/csrf` ile oturuma bağlı belirteci alır ve `GET/HEAD/OPTIONS` dışındaki her `/api` isteğinde `X-CSRF-Token` olarak gönderir. Anonim belirteç oturumlu isteklerde kabul edilmez.
4. **Çıkış / oturum düşmesi:** belirteç oturumla birlikte geçersiz olur.

Eksik veya hatalı belirteç `403 csrf_failed` döner ve işleyici çalışmaz. Belirteç `localStorage`'a yazılmaz; bellekte tutulur. `SameSite=Lax` ikinci savunma hattıdır, tek başına yeterli sayılmaz. Anonim belirteç rastgele bir ön çereze (`__Host-kantar-pre`) HMAC ile bağlıdır ve yalnız `login` isteğinde geçer; girişten sonra oturuma bağlı ayrı bir belirteç kullanılır (ayrıntı: `05-api-contracts.md` §1.3, `adr/0005`).

## 3. Yetkilendirme

Karar üç girdiyle verilir: **rol**, **belgenin deposundaki yetki seviyesi**, **sahiplik**. Matris `02` §2.4'tedir.

| Durum | Yanıt | Neden |
| --- | --- | --- |
| Oturum yok / geçersiz | `401 unauthenticated` (JSON, yönlendirme yok) | |
| Kullanıcının rolü bu işlem türünü hiç yapamaz | `403 forbidden` | İşlem türünün varlığı gizli değildir |
| Rol uygun ama kaynak kapsam dışı (yetkisiz depo, başkasının belgesi, okunamayan bilgi belgesi) | `404 not_found` | Var olan ile olmayan kaynak ayırt edilemez |
| Var olmayan kaynak | `404 not_found` | Yukarıdakiyle aynı gövde biçimi |

Ek kurallar:

- **EF sorgu filtresi yetki sınırı değildir.** Depo kapsamı her sorguda açık `WHERE` koşuludur ve her komut işleyicisinin `AuthorizeAsync` adımında yeniden denetlenir. Ham SQL kullanan her yerde kapsam parametresi zorunludur.
- **Talep bağlantısı yetki vermez.** Destek talebindeki ilişkili belge yalnız tür + numara metnidir; sunucu belgenin varlığını denetlemez, içeriğini döndürmez. Belgeyi görmek isteyen normal belge ucunu normal yetkiyle çağırır.
- **Tekrar oynatma yetki atlatmaz.** Idempotent tekrar, kayıtlı yanıt dönmeden önce güncel yetkiyi yeniden denetler.
- **`SystemAdmin` örtük yetki taşımaz.** Ticari belge ve kısıtlı bilgi belgesi için rol + depo yetkisi ayrıca verilmelidir; bu atama denetim kaydına düşer.
- **Alan düzeyi:** fiyat/maliyet alanları yetkisiz rolün yanıtında hiç yer almaz (boş değer olarak da dönmez).
- **Çakışma iletileri:** `duplicate_business_reference` yanıtı mevcut belgenin numarasını veya kimliğini **içermez**; yalnız referansın daha önce kullanıldığını ve ikinci bir etki oluşmadığını söyler. Böylece yanıt, çağıranın göremediği bir belge hakkında bilgi taşımaz.

## 4. Girdi işleme

| Tehdit | Önlem |
| --- | --- |
| Toplu atama (mass assignment) | İstek modelleri yalnız izinli alanları taşır; bilinmeyen alan `400 unknown_field`. Fiyat, snapshot, durum, sahip, numara istemciden alınmaz |
| Belirsiz JSON | Yinelenen anahtar `400 duplicate_field`; miktar için `1.0` veya `"1"` reddedilir; gövde en fazla 64 KB |
| SQL enjeksiyonu | Tüm SQL parametriktir; sıralama ve filtre değerleri kapalı listeden seçilir, metin olarak sorguya eklenmez |
| Aşırı sonuç kümesi | Sayfa boyutu 1–100; `OFFSET` yok; stok sorgularında `warehouseId` zorunlu |
| Saklı XSS | Metin alanları düz metindir; arayüz HTML olarak yorumlamaz. Bilgi belgesi Markdown'ı kısıtlı bir alt kümeyle işlenir; ham HTML ve betik çalışmaz. CSP `default-src 'self'` |
| Denetim karakteri / Unicode hileleri | Satır sonu ve sekme dışındaki denetim karakterleri reddedilir; metin NFC'ye normalleştirilir |
| HTTP dışı geçersiz veri | `CHECK`, benzersizlik ve yabancı anahtar kısıtları uygulamayı atlayan yazımı da reddeder |

## 5. Hız sınırı ve vekil başlıkları

| Uç grubu | Anahtar | Sınır |
| --- | --- | --- |
| `POST /api/auth/login`, `GET /api/auth/csrf` (anonim) | Kaynak IP | Sıkı (Aşama 1'de sayısal değer yapılandırmadan) |
| Oturumlu `/api` istekleri | Kullanıcı kimliği | Genel sınır |
| `POST /api/assist/ask` | Kullanıcı kimliği | 6 / dakika |

Aşım `429 rate_limited` + `Retry-After` döner. `X-Forwarded-For` ve benzeri başlıklar **varsayılan olarak güvenilmez**; yalnız yapılandırmada açıkça yazılı vekil adreslerinden kabul edilir. Yerel yığında vekil yoktur, kaynak IP doğrudan bağlantıdan alınır. Sayaçlar süreç içidir; çok örnekli dağıtımda paylaşılmaz (bilinen sınır).

## 6. Veritabanında en az yetki

- Web ve worker yalnız `kantar_app` girişiyle bağlanır. Bu giriş `db_datareader`/`db_datawriter` üyesi değildir; izinleri `db/grants.sql` içinde tek tek verilir.
- Günlük ve Posted belge tablolarında (`inv.StockMovements`, `inv.ReservationEntries`, `ops.AuditEntries`, `sup.KbDocumentVersions`, `sup.TicketComments`, sevkiyat/kabul/transfer/düzeltme) `UPDATE` ve `DELETE` için açık `DENY` vardır. `ALTER` verilmediği için `TRUNCATE` reddedilir.
- `kantar_migrator` yalnız `--migrate` / `--seed` modunda kullanılır; web ve worker sürecinin yapılandırmasında bu bağlantı dizesi **yoktur**.
- Bu kurallar gerçek `kantar_app` girişiyle sınanır; testler migrator girişiyle çalıştırılıp "geçti" sayılmaz.

## 7. Gizli değerler

| Değer | Nerede | Kural |
| --- | --- | --- |
| `sa`, `kantar_migrator`, `kantar_app` parolaları, bağlantı dizeleri | `.local/secrets/` (git ve Docker bağlamı dışı) | `scripts/local-stack.ps1 init` rastgele üretir; var olanı ezmez; ekrana yazmaz |
| Demo kullanıcı parolaları | `.local/seed/` | Aynı |
| HTTPS sertifika parolası, yerel model anahtarı (varsa) | `.local/secrets/`, Docker secret dosyası olarak bağlanır | Ortam değişkenine düz metin yazılmaz |
| Test fixture parolaları | Yalnız bellek | Günlüğe yazılmaz; doğrulama günlüklerinden süzülür |

Depoda yalnız yer tutuculu `.env.example` bulunur. Gizli değer taraması olumlu ve yanlış-olumlu kontrol örnekleriyle sınanır.

Karar (Aşama 1): `POST /api/admin/users` ve `reset-password` yanıtları geçici parola taşıdığı için bu uçlar idempotency kapsamının **dışındadır**; yanıt gövdeleri hiçbir tabloda saklanmaz. Yanıt kaybolursa kullanıcı oluşturma tekrarında `409` alınır (kullanıcı adı benzersizdir) ve yönetici parolayı sıfırlar.

## 8. Günlük ve ölçüm

- Günlüğe **yazılmayanlar**: istek/yanıt gövdesi, parola, çerez, CSRF belirteci, bağlantı dizesi, yardım sorusu ve yanıt metni, istem, müşteri/tedarikçi adı ve adresi.
- Kullanıcı adı yerine kullanıcı kimliği yazılır. Hata günlüğü SQL hata numarasını içerir, parametre değerlerini içermez.
- Ölçüm etiketleri kapalı kümedir (`operation`, `outcome`, `provider`, `status`). Belge, kullanıcı, ürün, depo kimliği etiket olmaz.
- Denetim kaydı `DetailJson` alanı kapalı bir alan kümesidir; kişisel veri taşımaz.
- Tanılama snapshot'ı sunucuda beyaz listeden üretilir (`02` §9.2); IP ve user-agent toplanmaz.

## 9. Kaynaklı yardıma özgü

Özet; ayrıntı `10-ai-grounding-and-access-design.md`:

- Yetki filtresi getirme sorgusunun içindedir; yetkisiz, taslak, emekli veya eski sürüm metin sağlayıcıya **gönderilmez**. Çıktı maskeleme tek güvence değildir.
- Model talimata uymayabilir; sınır sunucu denetimleridir (gönderilen metin, kabul edilen atıf, teslim öncesi yeniden yetkilendirme). Araç çağrısı yoktur.
- Sağlayıcı adresi yalnız sunucu yapılandırmasından gelir ve loopback/izinli özel adresle sınırlıdır; yönlendirme izlenmez, yanıt boyutu sınırlıdır.
- Soru ve istem saklanmaz. Yanıt yalnız sahibine, `no-store` ile teslim edilir; önbellek yoktur.
- Atıf kimliğinin geçerli olması içeriğin kaynakça desteklendiğini kanıtlamaz; bu sınır arayüzde ve `ai-verification.md` içinde açıkça yazılır.

## 10. Hata matrisi

| Olay | Kullanıcının gördüğü | Veri durumu | Kurtarma |
| --- | --- | --- | --- |
| Veritabanı erişilemez | `503 database_unavailable` / `retry_later`; `/health/ready` 503, `/health/live` 200 | Değişmez | Veritabanı gelince kendiliğinden; aynı `Idempotency-Key` ile tekrar güvenli |
| Deadlock (1205), kilit zaman aşımı (1222) | Çoğunlukla hiçbir şey (iç yeniden deneme); 3 denemeden sonra `503 retry_later` | Hiçbir kısmi etki yok | İstemci aynı anahtarla tekrar eder |
| Commit'ten **önce** süreç çöker | Bağlantı kopar | Transaction geri alınır; idempotency satırı da yoktur | Tekrar deneme işi baştan yapar |
| Commit'ten **sonra**, yanıt yazılmadan çöker / yanıt ağda kaybolur | Bağlantı kopar veya zaman aşımı | İş etkisi + idempotency kaydı + yanıt baytları kalıcı | Aynı anahtarla tekrar → kayıtlı yanıt bayt bayt döner; yeni belge oluşmaz |
| İstemci yeni anahtarla tekrar eder | `409 duplicate_business_reference` (iş referanslı komutlar) veya `409 invalid_state` | İkinci stok etkisi yok | Kullanıcı mevcut belgeyi açar |
| Idempotency kaydının süresi dolmuş (24 saat) | Yeni istek gibi değerlendirilir | Çift etkiyi iş referansı benzersizliği ve belge durumu engeller | – |
| Worker kira ortasında çöker | – | Mesaj işlenmemiş kalır; `LeaseUntil` dolunca başka worker alır | Tüketici `(MessageId, Handler)` benzersizliğiyle tekrar etkisizdir; eski worker'ın geç tamamlaması `LeaseVersion` koşulunda 0 satır etkiler |
| Yardım sağlayıcısı kapalı / zaman aşımı | Yardım panelinde `ProviderUnavailable` + kaynak listesi | ERP verisi etkilenmez | Kaynak araması ve tüm ERP komutları çalışır |
| Üretim sırasında erişim kalkar | `AccessChanged`; metin gösterilmez | Yanıt metni atılır | Kullanıcı yeniden sorar |
| Mutabakat fark bulur | Operasyon ekranında `DriftFound` uyarısı | Hiçbir bakiye veya günlük değişmez | Uygulama içinden onarım yoktur: fark, uygulama dışı bir yazma ya da kusur demektir ve incelenir. Fiziksel sayım farkı ayrı bir konudur; onu depo sorumlusu gerekçeli düzeltmeyle (normal işlem) giderir |
| Doğrulama sırasında bellek düşük | `verify` kapıyı başlatmaz, çıkış kodu 3 | Kaynak ve veritabanı etkilenmez; kontrol noktası yazılır | Bellek açılınca aynı kapı adıyla sürdürülür; sonuç "geçti" diye sunulmaz |

## 11. Sıralı akışlar

### 11.1 Mal kabul tekrar denemesi

1. `depo1`, `POST /api/purchasing/orders/{id}/receipts` gönderir: `Idempotency-Key: K1`, `If-Match: "v5"`, `externalReference: IRS-1001`.
2. Sunucu yetkiyi denetler (`WarehouseOperator`, PO deposunda `Operate`), `K1` kaydı olmadığını görür, transaction açar, idempotency satırını ekler.
3. PO başlığı `UPDLOCK` ile kilitlenir; durum ve `rowversion` denetlenir; her satır için `q ≤ OpenQty` denetlenir.
4. `GoodsReceipt` + satırları, `ReceivedQty` artışları, bakiye `UPDATE`'leri, `Receipt` hareketleri, denetim ve outbox satırları yazılır; yanıt baytları idempotency satırına yazılır; commit.
5. Yanıt (`201`) ağda kaybolur.
6. İstemci aynı gövdeyi `K1` ile yeniden gönderir (`If-Match` hâlâ `"v5"`). Yetki yeniden denetlenir; kayıt bulunur, özet eşleşir; kayıtlı `201` gövdesi ve başlıkları aynen döner, `Idempotent-Replay: true`. PO'nun güncel sürümü artık `v6` olsa da komut yeniden çalışmaz.
7. İstemci bunun yerine `K2` ile gönderseydi: özet yeni, komut çalışır; `If-Match: "v5"` eski olduğu için `412`. `If-Match` güncellenip gönderilse bile (`SupplierId`, `IRS-1001`) benzersizliği `409 duplicate_business_reference` döndürür. Stok ikinci kez artmaz.
8. Arada `depo1`'in `IST-01` yetkisi kaldırılmışsa 6. adım `404` döner; kayıtlı yanıt teslim edilmez.

### 11.2 Eşzamanlı onayda fazla satışın engellenmesi

1. Ürün X için `IST-01`'de `Available = 10`. Yirmi farklı taslak siparişin her biri X'ten 1 adet ister; 20 confirm aynı anda gelir.
2. Her istek kendi sipariş başlığını kilitler (farklı satırlar, birbirini beklemez).
3. Her biri X bakiyesinde koşullu `UPDATE … SET Reserved = Reserved + 1 WHERE … OnHand >= Reserved + 1` çalıştırır. Bakiye satırı kilidi istekleri sıraya sokar; koşul her seferinde güncel değerle değerlendirilir.
4. İlk 10 istek 1 satır etkiler → `Reserve` kaydı + durum `Confirmed` + commit.
5. Sonraki 10 istek 0 satır etkiler → `409 insufficient_available`; transaction geri alınır; idempotency kaydı kalmaz.
6. Sonuç: `Reserved = 10`, `Available = 0`, 10 `Reserve` kaydı, 10 `Confirmed` + 10 `Draft` sipariş. Çok satırlı bir siparişte tek satır yetersizse o siparişin hiçbir satırı rezerve edilmez.

### 11.3 Kısmi sevkiyat ile iptal yarışı

Sipariş `Confirmed`, tek satır: `OrderedQty = 10`. `depo1` 4 adet sevk eder, aynı anda `satis1` iptal eder. İkisi de başlığı `UPDLOCK` ile kilitlemeye çalışır.

**Önce sevkiyat kazanırsa:**

1. Sevkiyat: `ShippedQty = 4`, `OnHand −4`, `Reserved −4`, `Shipment` hareketi + `ReleaseOnShipment`; durum `PartiallyShipped`; commit.
2. İptal kilidi alır, güncel durumu okur. `If-Match` eski sürümü taşıdığı için `412` döner; kullanıcı belgeyi yeniden okuyup "kalanı iptal et" der.
3. İptal: `CancelledQty = 6`, `Reserved −6` (`ReleaseOnCancel`), `OnHand` değişmez; durum `Cancelled`.
4. Sonuç: `4 + 6 + 0 = 10`; sevk edilen 4 adet stoka geri dönmez.

**Önce iptal kazanırsa:**

1. İptal: `CancelledQty = 10`, `Reserved −10`; durum `Cancelled`; commit.
2. Sevkiyat kilidi alır; `If-Match` eski → `412`. Güncel sürümle yeniden denense de durum `Cancelled` olduğu için `409 invalid_state`. Stok hareketi oluşmaz.

Her iki sırada `OrderedQty = ShippedQty + CancelledQty + OpenQty` ve `Reserved = Σ OpenQty` korunur.

### 11.4 Transfer hatası

1. `depo.sorumlu`, `IST-01 → ANK-01` iki satırlı transfer gönderir (ürün A: 5, ürün B: 3).
2. Yetki: kaynakta ve hedefte `Operate`. Biri eksikse `404`, hiçbir şey yazılmaz.
3. Transaction açılır; `StockTransfer` + satırları yazılır.
4. Bakiye satırları `(WarehouseId, ProductId)` sırasıyla işlenir; kaynak düşüşleri ve hedef artışları aynı sıralamadadır. Hedefte bakiye satırı yoksa `UPDLOCK, HOLDLOCK` ile oluşturulur.
5. A için kaynak düşüşü ve hedef artışı başarılı. B için kaynakta `Available = 2 < 3` → koşullu `UPDATE` 0 satır etkiler.
6. Transaction geri alınır: A'nın düşüşü ve artışı, transfer belgesi, hareketler, idempotency satırı dahil. Yanıt `409 insufficient_available`, `errors` içinde B satırı.
7. Hedef yazımında beklenmeyen bir hata (test hata noktasıyla) oluşursa sonuç aynıdır: hiçbir depoda kısmi etki kalmaz.

### 11.5 Yardım üretimi sırasında erişimin kalkması

1. `satis.mudur` soru sorar. Getirme sorgusu, okuyabildiği bir `ManagerOnly` belgenin parçasını (S1) ve bir `General` parçayı (S2) getirir.
2. Sağlayıcı çağrısı başlar (transaction açık değil).
3. Üretim sürerken `admin`, kullanıcıdan `SalesManager` rolünü kaldırır — veya `destek.mudur` belgeyi emekli eder.
4. Sağlayıcı S1 ve S2'ye atıf yapan bir yanıt döndürür; atıflar getirilen kümede olduğu için doğrulamadan geçer.
5. Teslimden önce yeniden yetkilendirme: oturum hâlâ geçerli mi, S1 ve S2 şu anda okunabilir mi? Rol kaldırıldıysa oturum zaten düşmüştür; belge emekli edildiyse S1 sorgudan dönmez.
6. Sonuç `AccessChanged` (oturum düştüyse `401`); üretilen metin atılır, kullanıcıya gösterilmez.
7. Yanıt daha önce teslim edilmiş ve kullanıcı sonradan "talebe kaydet" derse: `from-assist` kaynakları yeniden denetler; S1 artık okunamıyorsa `409 source_no_longer_available`.
