# ADR-0004 — Idempotency: iş transaction'ında kayıt, yanıt baytlarının saklanması

Durum: Kabul edildi · Tarih: 2026-10-11

## Bağlam

İstemci bir komutu gönderir, sunucu işi yapar, yanıt ağda kaybolur. İstemci tekrar gönderdiğinde:

- stok ikinci kez değişmemeli, ikinci belge oluşmamalı;
- istemci ilk isteğin sonucunu **aynen** almalı (durum kodu, başlıklar, gövde);
- arada yetkisi kalkmış bir kullanıcı eski sonucu alamamalı;
- aynı anahtar farklı bir istek için kullanılmışsa bu fark edilmeli.

Yaygın "önce talebi kaydet (pending), sonra işi yap, sonra tamamlandı işaretle" tasarımı üç ayrı yazım içerir; aralarındaki çökme "pending" kaydını geride bırakır ve bir kurtarma süreci gerektirir.

## Karar

- `Idempotency-Key` başlığı işaretli komutlarda zorunludur. Kapsam (kullanıcı, işlem adı, anahtar) üçlüsüdür.
- Idempotency satırı **iş etkisiyle aynı transaction'da** eklenir; yanıt gövdesi baytlara serileştirilip aynı satıra yazılır; sonra commit edilir. Yanıt istemciye ancak commit'ten sonra yazılır.
- Böylece "alındı ama bitmedi" diye kalıcı bir durum yoktur: commit'ten önceki çökme hiçbir iz bırakmaz; commit'ten sonraki yanıt kaybında tekrar deneme kayıtlı baytları alır.
- Saklananlar: istek özeti (SHA-256) ve özet sürümü, durum kodu, `Content-Type`, `Location`, `ETag`, gövde `varbinary(max)`. Gövde JSON sütunu olarak **değil** bayt olarak saklanır; yeniden serileştirme yapılmaz.
- İstek özeti: sürüm etiketi + işlem + rota kimlikleri + `If-Match` + kanonik gövde. Kanonik gövde, doğrulanmış komut nesnesinin sunucuda sabit alan sırasıyla serileştirilmesidir. Bu şema yalnız bu API'nin kabul ettiği alan kümesi için tanımlıdır; RFC 8785'in tam uygulaması olduğu iddia edilmez.
- Sıra: yetki → kayıt araması → (varsa) özet karşılaştırma → kayıtlı yanıt. Yetki her tekrar oynatmada yeniden denetlenir.
- Aynı anahtar + farklı özet → `409 idempotency_key_reused`.
- Eşzamanlı aynı anahtar: ikinci ekleme birincil anahtar kilidinde bekler, benzersizlik hatası alır; transaction geri alınır, kayıt yeni bağlamda okunur ve kayıtlı yanıt döner.
- Yalnız `2xx` sonuçlar kaydedilir; reddedilen komut iz bırakmaz ve güncel duruma göre yeniden değerlendirilir.
- Saklama 24 saat. Süresi dolan anahtar yeni istek sayılır; bu durumda çift etkiyi **iş referansı benzersizliği** (irsaliye no, sevk referansı, transfer/düzeltme referansı) ve belge durumu engeller.

Idempotency anahtarı iş benzersizliğinin yerini tutmaz: farklı anahtarla gönderilen aynı iş referansı `409 duplicate_business_reference` alır.

## Değerlendirilen alternatifler

| Alternatif | Neden seçilmedi |
| --- | --- |
| Ayrı transaction'da "pending" talep kaydı + tamamlanınca güncelleme | Çökme sonrası asılı kayıt ve kurtarma mantığı gerekir; talep var ama iş yok durumu görünür olur |
| Yalnız iş referansı benzersizliği | Çift etkiyi engeller ama ilk yanıtı tekrar edemez; referansı olmayan komutları (taslak oluşturma, onay) kapsamaz |
| Yanıtı JSON sütununda saklayıp tekrar serileştirmek | Alan sırası/boşluk/sayı biçimi değişebilir; "bayt düzeyinde aynı" garanti edilemez |
| Yanıtı saklamayıp belgeyi yeniden okumak | Tekrar anındaki belge durumu ilk yanıttan farklı olabilir |
| Süreç içi/Redis önbellek | Kalıcı değil; iş etkisiyle atomik yazılamaz |

## Sonuçlar / maliyet

- Her önemli komut bir satır ve yanıt gövdesi kadar ek yazım yapar; satırlar 24 saat sonra temizlenir.
- Yanıt, commit'ten önce üretilmek zorundadır; işleyici sonucu transaction içinde tam olarak oluşturur.
- Uzun süren bir komut, aynı anahtarlı eşzamanlı ikizini kilit zaman aşımına kadar bekletir.
- Kayıtlı gövdeler hassas alan içerebilir. Bu yüzden geçici parola döndüren `POST /api/admin/users` ve `reset-password` uçları idempotency kapsamının dışında bırakıldı (Aşama 1 kararı); yanıtları saklanmaz.
- Yanıtı sürümler arasında değiştiren bir dağıtımda eski kayıtlar eski biçimde döner; bu kabul edilen davranıştır.

## Değişme koşulu

Komutlar transaction dışında dış sisteme yan etki üretmeye başlarsa (ödeme, e-posta) tek transaction yeterli olmaz; o komutlar için "pending" durumlu iki aşamalı kayıt ve kurtarma işi eklenir.

## Kaynaklar

RFC 8785 (JSON Canonicalization Scheme) ve SQL Server'da 2601/2627 hatalarının transaction'a etkisi bu oturumda resmî kaynaktan doğrulanmadı. Kod, benzersizlik hatasından sonra transaction'ın kullanılabilir olduğunu varsaymaz (`04` §3.4).
