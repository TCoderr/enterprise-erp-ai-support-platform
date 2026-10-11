# ADR-0006 — Veritabanı outbox'ı, kiralamalı worker ve çit belirteci

Durum: Kabul edildi · Tarih: 2026-10-11

## Bağlam

Bazı işler komutun kendisinden sonra yapılmalıdır: düşük stok bildirimi, talep atandı bildirimi, süresi dolan kayıtların temizliği, zamanlı mutabakat. Gereksinimler:

- iş komutu commit edildiyse ardıl iş **kaybolmamalı**; commit edilmediyse hiç çalışmamalı;
- worker çökerse iş başka bir worker tarafından alınabilmeli;
- aynı iş iki kez çalışırsa etkisi tek olmalı;
- kirası düşmüş (yavaşlamış, askıda kalmış) bir worker'ın geç gelen sonucu, işi devralan worker'ın sonucunu ezmemeli.

Ürün yerelde çalışır; dış tüketici ve yüksek hacim yoktur.

## Karar

- **Outbox:** iş olayları `ops.OutboxMessages` tablosuna, iş etkisiyle aynı transaction'da yazılır (ADR-0002). Yük yalnız kimlik ve tür taşır; kişisel veri taşımaz.
- **Kiralama (lease):** worker tek bir `UPDATE … OUTPUT` ile bir grup mesajı alır; `LeaseOwner`, `LeaseUntil` ayarlanır, `LeaseVersion` ve `Attempts` artar. Seçim `WITH (READPAST, UPDLOCK, ROWLOCK)` ile yapılır; başka worker'ın kilitlediği satırlar atlanır.
- **Çit belirteci (fencing):** tamamlama `WHERE Id = @id AND LeaseOwner = @me AND LeaseVersion = @v` koşulludur. Kirası düşüp başka worker'a geçmiş bir mesajda eski worker'ın tamamlaması 0 satır etkiler ve yok sayılır.
- **Tüketici tekrar etkisizliği:** her işleyici, etkisiyle aynı transaction'da `ops.OutboxConsumptions (MessageId, Handler)` satırı ekler; benzersizlik ikinci çalışmayı etkisiz kılar. Garanti "en az bir kez teslim, tek etki"dir.
- **Yeniden deneme:** başarısız mesaj artan gecikmeyle (`AvailableAt`) yeniden kullanılabilir olur; `LastErrorCode` kapalı bir kod kümesidir, hata metni saklanmaz.
- **Zamanlı işler** (temizlik, mutabakat) aynı worker sürecinde çalışır; hepsi tekrar çalıştırılabilir yazılır. Mutabakat salt okunurdur.
- **Kapanış:** durdurma sinyalinde worker yeni kiralama almaz, eldeki mesajı bitirmeye çalışır; süre dolarsa kira kendiliğinden düşer.
- Worker ayrı süreçtir, yalnız `kantar_app` girişiyle bağlanır. Broker (RabbitMQ, Kafka) ve Redis yoktur.

## Değerlendirilen alternatifler

| Alternatif | Neden seçilmedi |
| --- | --- |
| Süreç içi kuyruk / `Channel` | Çökmede kaybolur; iş etkisiyle atomik değildir |
| Komut içinde doğrudan çalıştırma | Komut süresini uzatır; ardıl işin hatası iş komutunu geri aldırır |
| Mesaj broker'ı | Ek container ve bellek; dış tüketici yokken gerekçesi yok. Outbox yine gerekirdi |
| Kira yerine satırı transaction boyunca kilitli tutmak | Uzun transaction; işleyici süresi boyunca kilit; çökme tespiti bağlantı kopmasına bağlı |
| Çit belirteci olmadan yalnız `LeaseOwner` denetimi | Aynı worker kirayı kaybedip yeniden alırsa eski ve yeni girişim ayırt edilemez |
| Hazır iş zamanlayıcı kütüphanesi | Bu kapsam için ek bağımlılık ve şema; gereken davranış küçük |

## Sonuçlar / maliyet

- Yoklama gecikmesi (varsayılan 2 sn): bildirimler anlık değildir.
- Ayrı bir worker süreci ve onun belleği.
- Kira süresi işleyicinin olağan süresinden uzun seçilmelidir; çok uzun seçilirse çökme sonrası devralma gecikir.
- Mesaj sırası garanti edilmez; işleyiciler sıraya bağımlı yazılmaz.
- İşlenmiş mesajların saklama/temizlik süresi ayrıca yönetilir.

## Değişme koşulu

Dış sistemlere olay yayınlamak, yüksek hacim veya düşük gecikme gerekirse outbox bir broker'a aktarıcı (relay) olarak kalır ve tüketim broker'a taşınır.

## Kaynaklar

`READPAST` ipucunun `READ_COMMITTED_SNAPSHOT ON` altındaki davranışı ve `UPDATE … OUTPUT` ile birlikte kullanımı bu oturumda resmî kaynaktan doğrulanmadı; Aşama 9'da gerçek SQL Server üzerinde iki worker'lı entegrasyon testiyle doğrulanacaktır (çökme, kira düşmesi, geç tamamlama).
