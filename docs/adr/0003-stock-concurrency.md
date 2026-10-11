# ADR-0003 — Stok eşzamanlılığı: koşullu UPDATE, belge kilidi, sıralı kilitleme

Durum: Kabul edildi · Tarih: 2026-10-11

## Bağlam

Aynı (depo, ürün) bakiyesine aynı anda birçok komut dokunabilir: rezervasyon, sevkiyat, mal kabul, transfer, düzeltme. Korunacak değişmezler `Reserved ≥ 0` ve `OnHand ≥ Reserved`'dir. Aynı belgeye de birbiriyle yarışan komutlar gelebilir (sevkiyat ↔ iptal, mal kabul ↔ mal kabul, mal kabul ↔ iptal).

Oku–kontrol et–yaz deseni, okuma ile yazma arasında başka bir transaction'ın araya girmesine açıktır. Veritabanı `READ_COMMITTED_SNAPSHOT ON` ile çalıştığı için düz okuma kilit almaz; bu yüzden yazma yolunun kilitleri açıkça alınmalıdır.

## Karar

Üç mekanizma birlikte kullanılır:

1. **Tek ifadeli koşullu değişiklik.** Bakiye, koşulu `WHERE` içinde taşıyan tek bir `UPDATE … OUTPUT` ile değişir. Koşul satır kilidi altında güncel değerle değerlendirilir. 0 satır etkilenmesi iş kuralı ihlalidir; transaction geri alınır. `OUTPUT` ile dönen değerler günlük satırının `OnHandAfter` / `ReservedAfter` alanına yazılır.
2. **Belge başlığı kilidi.** Durum değiştiren her belge komutu ilk iş olarak başlık satırını `WITH (UPDLOCK, ROWLOCK)` ile okur. Aynı belgeye gelen komutlar sıraya girer; ikinci gelen birincinin commit edilmiş durumunu görür.
3. **Belirlenimci kilit sırası.** Her komutta sıra aynıdır: belge başlığı → bakiye satırları `(WarehouseId, ProductId)` artan sırada. Transfer, kaynak ve hedef satırlarını aynı sıralamaya koyar.

Ek olarak:

- Bakiye satırı yoksa yalnız girişlerde `INSERT … WHERE NOT EXISTS (… WITH (UPDLOCK, HOLDLOCK))` ile oluşturulur; `MERGE` kullanılmaz.
- `CHECK` kısıtları ikinci savunma hattıdır: uygulama hatası olsa da ihlal eden satır yazılamaz.
- Deadlock (1205), kilit zaman aşımı (1222) ve anlık görüntü çakışması (3960) için tüm transaction en fazla 3 kez yeniden çalıştırılır; her denemede durum sıfırdan okunur. İş kuralı hataları yeniden denenmez.
- `rowversion` belge başlıklarında `ETag`/`If-Match` için kullanılır; bakiyede yalnız bilgi amaçlıdır.

Stok SQL'i tek bir altyapı sınıfındadır, parametriktir; başka hiçbir kod bakiye tablosuna yazmaz.

## Değerlendirilen alternatifler

| Alternatif | Neden seçilmedi |
| --- | --- |
| EF Core ile oku–değiştir–kaydet + `rowversion` çakışmasında yeniden deneme | Yüksek çekişmede (aynı ürüne 20+ eşzamanlı confirm) çok sayıda tekrar; çok satırlı işlemde her tekrar bütün satırları yeniden okur |
| `SERIALIZABLE` yalıtım | Aralık kilitleri ve deadlock olasılığı artar; koşullu `UPDATE` aynı güvenceyi daha dar kilitle verir |
| Uygulama düzeyi kilit (`sp_getapplock` veya süreç içi kilit) | Süreç içi kilit web + worker arasında geçersiz; uygulama kilidi veritabanı kısıtının yerini tutmaz |
| Yalnız `CHECK` kısıtına güvenmek | Değişmezi korur ama kullanıcıya anlamlı hata (hangi satır, ne kadar eksik) vermek zorlaşır |
| Rezervasyonu ayrı satırlar toplamı olarak hesaplamak (sayaç tutmamak) | Her uygunluk sorgusu toplama yapar; eşzamanlı ekleme için yine aralık kilidi gerekir |

## Sonuçlar / maliyet

- Stok ilkel işlemleri ham SQL'dir; EF değişiklik izleyicisi bakiye satırlarını bilmez. Bakiye değerleri işleyicide varlık olarak tutulmaz.
- Belge kilidi, aynı belgeye gelen komutları seri hâle getirir; bu istenen davranıştır, ancak uzun süren bir komut aynı belgedeki diğerlerini bekletir (`LOCK_TIMEOUT` 5 sn).
- Farklı belgelerin aynı ürüne dokunması bakiye satırı kilidinde sıraya girer; sıcak ürünlerde verim bu kilitle sınırlıdır.
- Davranış SQL Server'a özgüdür; sağlayıcı değişirse bu katman yeniden yazılır.

## Değişme koşulu

Tek bir bakiye satırındaki çekişme ölçülmüş bir darboğaz olursa (yük testi yapılmadı) rezervasyonun parçalanması veya kuyruğa alınması değerlendirilir.

## Kaynaklar

`UPDLOCK`, `HOLDLOCK`, `ROWLOCK` ipuçlarının ve `READ_COMMITTED_SNAPSHOT` altındaki davranışlarının resmî belgeleri bu oturumda doğrulanmadı. Davranış, Aşama 3 ve 5'teki gerçek SQL Server entegrasyon testleriyle doğrulanacaktır (`09` §5.1: S3, S9, S19–S23, D10).
