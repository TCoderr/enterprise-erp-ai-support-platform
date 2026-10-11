# 05 — API Sözleşmeleri

Taban yol `/api`. Yalnız `application/json; charset=utf-8`. OpenAPI belgesi geliştirme ortamında `/openapi/v1.json` altında sunulur.

## 1. Ortak kurallar

### 1.1 İstek

| Konu | Kural |
| --- | --- |
| Gövde | En fazla 64 KB (`413`); JSON değilse `415`; bozuk JSON `400 invalid_json` |
| Bilinmeyen alan | `400 unknown_field` (alan adı `errors` içinde) |
| Yinelenen anahtar | `400 duplicate_field` |
| Sayılar | Miktarlar JSON tam sayısıdır (`1.0`, `"1"` reddedilir). Para JSON sayısıdır, en fazla 2 ondalık |
| Metin | Baştaki/sondaki boşluk kırpılır, NFC'ye normalleştirilir; denetim karakterleri (satır sonu ve sekme dışında) reddedilir |
| `null` | Yalnız "isteğe bağlı" işaretli alanlarda; alanı göndermemekle eşdeğerdir |
| CSRF | `GET/HEAD/OPTIONS` dışındaki isteklerde `X-CSRF-Token` zorunlu |
| Sıralama/filtre | Yalnız belgede yazılı parametreler ve değerler; bilinmeyen sorgu parametresi `400 unknown_parameter` |
| Sayfalama | `?limit=1..100` (varsayılan 25), `?cursor=<opak>`; yanıt `{ items, nextCursor }` |

### 1.2 Idempotency

`Idempotency-Key` başlığı (16–128 karakter, `[A-Za-z0-9_-]`) aşağıda **[I]** işaretli komutlarda zorunludur (`400 idempotency_key_required`).

- Kapsam: (kullanıcı, işlem adı, anahtar). İşlem adı rota şablonundan türetilir.
- İstek özeti: `SHA-256("v1" ␟ işlem ␟ rota kimlikleri ␟ If-Match ␟ kanonik gövde)`. Kanonik gövde, doğrulanmış ve normalleştirilmiş komut nesnesinin sunucuda sabit alan sırasıyla yeniden serileştirilmesidir; satır dizileri istemcinin gönderdiği sırada kalır. Bu, RFC 8785'in tam uygulaması **değildir**; yalnız bu API'nin kabul ettiği alan kümesi için tanımlıdır.
- Aynı anahtar + aynı özet → kayıtlı durum kodu, `Content-Type`, `Location`, `ETag` ve gövde **baytları** aynen döner; `Idempotent-Replay: true` eklenir. Tekrar oynatmadan önce güncel yetki yeniden denetlenir; yetki kalkmışsa `403`/`404` döner.
- Aynı anahtar + farklı özet → `409 idempotency_key_reused`.
- Yalnız başarılı (`2xx`) sonuçlar kaydedilir. Reddedilen (`4xx`) veya `503` ile biten bir komut iz bırakmaz; aynı anahtarla yeniden denenebilir ve güncel duruma göre yeniden değerlendirilir.
- Saklama süresi 24 saat. Süresi dolan anahtar yeni istek sayılır; çift etkiyi bu durumda iş referansı benzersizliği ve belge durumu engeller.

### 1.3 Önkoşullar

Belge başlığını değiştiren komutlar `If-Match: "<etag>"` ister. Eksikse `428 precondition_required`, eskiyse `412 precondition_failed` (gövdede güncel `etag` yoktur; istemci belgeyi yeniden okur). `GET` yanıtları `ETag` taşır.

İstisna — sevkiyat ve mal kabul: bu iki komut başlığın kullanıcının gördüğü sürümüne değil, **kalan miktara** dayanır. Belge kilidi altında durum ve kalan miktar yeniden denetlenir, iş referansı da benzersizdir; bu yüzden `If-Match` zorunlu değildir. Gönderilirse uygulanır (`412`). Böylece aynı siparişe iki ayrı teslimatı işleyen iki depo görevlisi birbirini `412` ile engellemez; fazlası `409 exceeds_remaining`, kapanmış belge `409 invalid_state` alır.

Anonim CSRF belirteci: `GET /api/auth/csrf` oturum yoksa `__Host-kantar-pre` adlı rastgele, `HttpOnly; SameSite=Strict` bir ön çerez yazar ve belirteci `HMAC-SHA256(sunucu anahtarı, "pre:" + çerez değeri)` olarak üretir. Bu belirteç yalnız `login` isteğinde kabul edilir. Girişten sonra ön çerez silinir; oturumlu belirteç oturum satırındaki `CsrfSecret` değeridir ve anonim belirteç oturumlu isteklerde geçmez.

### 1.4 Hata biçimi

RFC 9457 `application/problem+json`:

```json
{
  "type": "https://kantar.invalid/problems/insufficient_available",
  "title": "Yeterli kullanılabilir stok yok",
  "status": 409,
  "code": "insufficient_available",
  "correlationId": "01JB…",
  "errors": [ { "field": "lines[1]", "code": "insufficient_available", "detail": "İstenen 40, kullanılabilir 12" } ]
}
```

| Durum | `code` örnekleri |
| --- | --- |
| 400 | `invalid_json`, `unknown_field`, `duplicate_field`, `validation_failed`, `idempotency_key_required`, `unknown_parameter` |
| 401 | `unauthenticated` |
| 403 | `forbidden`, `csrf_failed`, `approval_limit_exceeded`, `password_change_required` |
| 404 | `not_found` (kapsam dışı kaynak dahil) |
| 409 | `invalid_state`, `insufficient_available`, `exceeds_remaining`, `duplicate_business_reference`, `idempotency_key_reused`, `master_data_inactive`, `source_no_longer_available` |
| 412 / 428 | `precondition_failed` / `precondition_required` |
| 413 / 415 | `payload_too_large` / `unsupported_media_type` |
| 422 | `line_mismatch` (satır belgeye ait değil, yanlış depo/ürün) |
| 429 | `rate_limited` (`Retry-After`) |
| 503 | `retry_later`, `database_unavailable` |

`detail` alanları kullanıcının zaten görmeye yetkili olduğu verilerden oluşur; başka kullanıcıya ait belge numarası, SQL hata metni veya yığın izi içermez.

## 2. Kimlik ve oturum

| Yöntem ve yol | Açıklama |
| --- | --- |
| `GET /api/auth/csrf` | Anonim veya oturumlu. `{ token }` döner; anonim belirteç yalnız `login` için geçerlidir |
| `POST /api/auth/login` | `{ userName, password }` → `200 { user }` + oturum çerezi; hatalı → `401` (kullanıcı var/yok ayrımı yapılmaz); 5 hatadan sonra 15 dk kilit |
| `POST /api/auth/logout` | Oturumu iptal eder → `204` |
| `GET /api/auth/me` | `{ id, userName, displayName, roles[], warehouses[{ id, code, name, level }], mustChangePassword, poApprovalLimit }` |
| `POST /api/auth/change-password` | `{ currentPassword, newPassword }` → diğer tüm oturumlar iptal, bu oturum yenilenir |

Oturum çerezi: `__Host-kantar-session`, `Secure; HttpOnly; SameSite=Lax; Path=/`. Boşta kalma 30 dk, mutlak süre 8 saat. Açık kayıt (signup) uç noktası yoktur.

## 3. Yönetim (SystemAdmin)

| Yöntem ve yol | Açıklama |
| --- | --- |
| `GET /api/admin/users`, `GET /api/admin/users/{id}` | |
| `POST /api/admin/users` | `{ userName, displayName, roles[], poApprovalLimit }` → geçici parola bir kez döner, `mustChangePassword = true`. Bilerek **[I] değildir**: yanıt gövdesi parola taşıdığı için saklanmaz. Yanıt kaybolursa tekrar deneme `409 user_name_taken` alır ve yönetici parolayı sıfırlar |
| `PUT /api/admin/users/{id}/roles` | `{ roles[] }`, `If-Match` → kullanıcının `SecurityStamp` değeri değişir, oturumları düşer |
| `PUT /api/admin/users/{id}/warehouse-grants` | `{ grants[{ warehouseId, level }] }`, `If-Match` |
| `POST /api/admin/users/{id}/reset-password` | Geçici parola döner; tüm oturumlar iptal |
| `POST /api/admin/users/{id}/deactivate` / `activate` | |
| `GET /api/admin/audit` | `?action=&entityType=` + imleç (tarih filtresi yoktur) |

## 4. Ana veri

`GET /api/products` (`?search=&active=`), `GET /api/products/{id}`, `POST /api/products` **[I]**, `PUT /api/products/{id}` (`If-Match`), `POST /api/products/{id}/deactivate|activate`. Aynı desen: `/api/customers`, `/api/suppliers`, `/api/warehouses`. Okuma yetkisi `02` belgesindeki matristedir; fiyat alanı yetkisi olmayan role yanıtta **hiç yer almaz**.

## 5. Stok

| Yöntem ve yol | Açıklama |
| --- | --- |
| `GET /api/inventory/balances` | `?warehouseId=` (zorunlu) `&search=&lowStockOnly=` → `{ productId, sku, name, unit, onHand, reserved, available, reorderLevel }` |
| `GET /api/inventory/movements` | `?warehouseId=` (zorunlu) `&productId=&type=&from=&to=` + imleç |
| `GET /api/inventory/reservations` | `?warehouseId=` (zorunlu) `&productId=` + imleç |
| `POST /api/inventory/transfers` **[I]** | `{ reference, fromWarehouseId, toWarehouseId, note?, lines[{ productId, quantity }] }` → `201` |
| `GET /api/inventory/transfers`, `GET /api/inventory/transfers/{id}` | Kaynak veya hedef depoda `View` yeterli |
| `POST /api/inventory/adjustments` **[I]** | `{ reference, warehouseId, reasonCode, note, lines[{ productId, quantityDelta }] }` → `201` |
| `GET /api/inventory/adjustments`, `GET /api/inventory/adjustments/{id}` | |

`warehouseId` zorunludur; "tüm depolar" sorgusu yoktur. Yetkisiz depo kimliği `404` döner.

## 6. Satış

| Yöntem ve yol | Açıklama |
| --- | --- |
| `GET /api/sales/orders` | `?warehouseId=&status=&mine=` + imleç; yalnız yetkili depolar |
| `POST /api/sales/orders` **[I]** | `{ customerId, warehouseId, note?, lines[{ productId, quantity }] }` → `201` Draft |
| `GET /api/sales/orders/{id}` | Başlık + satırlar + sevkiyat özetleri; `ETag` |
| `PUT /api/sales/orders/{id}` | Draft'ı bütünüyle değiştirir (`If-Match`) |
| `POST /api/sales/orders/{id}/confirm` **[I]** | `If-Match` → `200` |
| `POST /api/sales/orders/{id}/cancel` **[I]** | `If-Match`, `{ reason }` → `200` |
| `POST /api/sales/orders/{id}/shipments` **[I]** | `{ dispatchReference, lines[{ orderLineId, quantity }] }` → `201`. `If-Match` isteğe bağlıdır (bkz. 1.3) |
| `GET /api/sales/orders/{id}/shipments/{shipmentId}` | |
| `GET /api/sales/orders/{id}/summary` | Yazdırmaya uygun özet verisi; yanıtta `legalNotice: "Bu belge bilgi amaçlı sipariş özetidir; fatura, e-fatura veya irsaliye değildir."` |

İstemci hiçbir yerde fiyat, snapshot, durum, sahip veya numara gönderemez; bu alanlar gövdede yer alırsa `unknown_field` döner.

## 7. Satın alma

| Yöntem ve yol | Açıklama |
| --- | --- |
| `GET /api/purchasing/orders`, `GET /api/purchasing/orders/{id}` | |
| `POST /api/purchasing/orders` **[I]** | `{ supplierId, warehouseId, note?, lines[{ productId, quantity, unitCost }] }` |
| `PUT /api/purchasing/orders/{id}` | Draft (`If-Match`) |
| `POST /api/purchasing/orders/{id}/approve` **[I]** | `If-Match` |
| `POST /api/purchasing/orders/{id}/cancel` **[I]** | `If-Match`, `{ reason }` |
| `POST /api/purchasing/orders/{id}/receipts` **[I]** | `{ externalReference, lines[{ orderLineId, quantity }] }` → `201`. `If-Match` isteğe bağlıdır (bkz. 1.3) |
| `GET /api/purchasing/orders/{id}/receipts/{receiptId}` | |

`WarehouseOperator` görünümünde `unitCost` ve toplamlar yer almaz.

## 8. Destek ve bilgi bankası

| Yöntem ve yol | Açıklama |
| --- | --- |
| `GET /api/support/tickets` | `?status=&assignee=me|unassigned&mine=` + imleç |
| `POST /api/support/tickets` **[I]** | `{ title, description, priority, module, screen?, errorCode?, relatedDocument?{ type, number } }` |
| `GET /api/support/tickets/{id}` | Yorumlarla; iç notlar yalnız destek personeline |
| `POST /api/support/tickets/{id}/comments` **[I]** | `{ body, isInternal? }` |
| `POST /api/support/tickets/{id}/comments/from-assist` **[I]** | `{ answerId }` — bkz. 9 |
| `POST /api/support/tickets/{id}/assign` | `If-Match`, `{ assigneeUserId }` |
| `POST /api/support/tickets/{id}/status` | `If-Match`, `{ status }` |
| `GET /api/kb/articles` | Okuyucu görünümü: yalnız okunabilir yayınlı belgeler; `?search=&module=&errorCode=` |
| `GET /api/kb/articles/{slug}` | Yayınlı sürüm; okunamıyorsa `404` |
| `GET /api/kb/manage/documents`, `GET /api/kb/manage/documents/{id}` | Destek personeli: tüm durumlar ve sürümler |
| `POST /api/kb/manage/documents` **[I]** | `{ slug, visibility, module, warehouseId?, errorCode?, title, bodyMarkdown }` → Draft + sürüm 1 |
| `POST /api/kb/manage/documents/{id}/versions` **[I]** | `If-Match`, `{ title, bodyMarkdown }` → yeni taslak sürüm |
| `POST /api/kb/manage/documents/{id}/publish` **[I]** | `If-Match`, `{ versionNo }` (SupportManager) |
| `POST /api/kb/manage/documents/{id}/retire` **[I]** | `If-Match` (SupportManager) |

`relatedDocument` yalnız tür + numara metnidir; sunucu belgenin varlığını **denetlemez** ve yanıtta belge içeriği döndürmez (aksi halde talep açmak belge varlığını sınama aracı olurdu).

## 9. Kaynaklı yardım

| Yöntem ve yol | Açıklama |
| --- | --- |
| `GET /api/assist/status` | `{ kind: "demo"|"local-model"|"disabled", model?, isDemo }` |
| `POST /api/assist/search` | `{ query }` → okunabilir parçalar (yanıt üretmeden); sağlayıcıdan bağımsız çalışır |
| `POST /api/assist/ask` | `{ question, ticketId? }` → aşağıdaki gövde. Hız sınırı: kullanıcı başına 6/dk |
| `GET /api/assist/answers/{id}` | Yalnız sahibi; süresi dolmuşsa `404` |

```json
{
  "id": "…",
  "status": "Answered | NoSource | ProviderUnavailable | RejectedUnsupported | AccessChanged",
  "answer": "düz metin / kısıtlı Markdown",
  "citations": [ { "documentSlug": "…", "title": "…", "versionNo": 3, "sectionNo": 2, "heading": "…" } ],
  "limitations": "…",
  "sources": [ { "documentSlug": "…", "title": "…", "versionNo": 3, "sectionNo": 2, "heading": "…", "snippet": "…" } ],
  "provider": { "kind": "demo | local-model | disabled", "model": "…", "isDemo": true },
  "expiresAt": "…"
}
```

- Soru 3–1000 karakter. `ticketId` verilirse çağıran o talebi görebilmelidir; talepten yalnız `module`, `errorCode` ve `title` arama ipucu olarak kullanılır.
- `citations` yalnız sunucunun getirdiği parça kümesinden gelir. Güven puanı alanı yoktur.
- `sources`, kullanıcının yanıtı kaynakla karşılaştırabilmesi için atıf yapılan parçaların alıntılarını taşır; `ProviderUnavailable` durumunda getirilen parçaları taşır (yanıt üretilemese de okunabilsinler).
- `id` ve `expiresAt` yalnız `Answered` durumunda döner; diğer durumlarda saklanan bir yanıt yoktur.
- `from-assist`: yanıt çağırana ait, süresi dolmamış, durumu `Answered` olmalı; atıf yapılan her parça **o anda** hâlâ okunabilir ve aynı yayınlı sürüme ait olmalıdır, değilse `409 source_no_longer_available`.

## 10. Operasyon (SystemAdmin)

`GET /api/ops/outbox` (özet sayılar), `GET /api/ops/reconciliation/latest`, `POST /api/ops/reconciliation/run` **[I]** (salt okunur çalıştırma), `GET /api/ops/metrics`, `GET /api/notifications` (her kullanıcı kendi bildirimleri), `POST /api/notifications/{id}/read`.

Sağlık: `GET /health/live`, `GET /health/ready` (anonim, ayrıntısız).
