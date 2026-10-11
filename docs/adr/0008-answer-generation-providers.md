# ADR-0008 — Yanıt üretim sağlayıcıları: deterministik demo ve yerel OpenAI-uyumlu adaptör

Durum: Kabul edildi · Tarih: 2026-10-11

## Bağlam

Kaynaklı yardımın orkestrasyonu (yetkili getirme, atıf doğrulama, yeniden yetkilendirme, hata davranışı) her çalıştırmada, belirlenimci olarak sınanabilmelidir. Öte yandan belirlenimci bir sahte, gerçek bir dil modelinin davranışı hakkında hiçbir şey söylemez.

Kısıtlar:

- Ücretli API ve kullanıcı kredisi kullanılmaz; uzak uç nokta çağrılmaz.
- Otomatik model indirme, GPU kurulumu veya genel (global) model çalıştırıcı kurulumu yapılmaz.
- Bu oturumda makinede yerel model çalıştırıcı **bulunamadı**: `ollama`, `lms`, `llama-server` komutları yok; `127.0.0.1:11434` ve `127.0.0.1:1234` yanıt vermedi.
- Bellek dar: veritabanı, tarayıcı ve model aynı anda çalıştırılamaz.

## Karar

Tek bir port: `IAnswerGenerator`. İki uygulama:

| | A) Deterministik demo sağlayıcı | B) Yerel model adaptörü |
| --- | --- | --- |
| Nedir | Süreç içi, kural tabanlı: en ilgili parçalardan soruyla örtüşen cümleleri seçer, atıflarıyla birleştirir | OpenAI-uyumlu `POST {base}/v1/chat/completions` çağıran HTTP istemcisi |
| Dil modeli mi | **Hayır.** API'de `isDemo: true`; arayüzde "Demo sağlayıcı — dil modeli değil" | Evet, yapılandırılmış ve erişilebilirse |
| Varsayılan | Evet (`Ai:Provider = Demo`) | Hayır; açıkça yapılandırılır |
| Kullanım | Yerel demo ve tüm deterministik testler | Yalnız `ai-live` kapısı ve isteğe bağlı yerel kullanım |

Kurallar:

- Sağlayıcı adresi yalnız sunucu yapılandırmasından gelir; kullanıcı girdisinden URL alınmaz. İzinli adresler: loopback (`127.0.0.1`, `localhost`, `[::1]`) ve yapılandırmada açıkça listelenen özel ağ adresleri. Açılışta doğrulanır.
- HTTP istemcisi yönlendirme izlemez, vekil kullanmaz, yanıt gövdesini 256 KB ile sınırlar, zaman aşımı uygular; bağlanılan adres çözümlemeden sonra yeniden denetlenir.
- Anahtar gerekiyorsa git dışı bir dosyadan okunur.
- Yanıt ayrıştırma `response_format` desteğini **varsaymaz**: `choices[0].message.content` metninden tek bir JSON nesnesi çıkarılır; şemaya uymayan çıktı `RejectedUnsupported` olur.
- Akışlı (streaming) yanıt yoktur.
- Sağlayıcı araç çağıramaz; adaptör araç tanımı göndermez.
- `Ai:Provider = Disabled` ile yardım yanıtı kapatılabilir; kaynak araması çalışmaya devam eder.

Doğrulama iki ayrı satırda raporlanır:

1. **Deterministik paket** (demo + test içi sahte sağlayıcılar): orkestrasyon, yetki, atıf doğrulama, hata davranışı.
2. **Canlı yerel model** (`ai-live`): yalnız model gerçekten varsa çalışır; küçük sentetik altın soru seti; çıktılar elle değerlendirilir.

Birincinin geçmesi ikincinin yerine sayılmaz. Model yoksa `ai-live` **çalıştırılmadı** olarak raporlanır ve "canlı dil modeli doğrulanmadı" ifadesi son raporda yer alır.

## Değerlendirilen alternatifler

| Alternatif | Neden seçilmedi |
| --- | --- |
| Yalnız demo sağlayıcı | Adaptör yolu (istem yapısı, ayrıştırma, zaman aşımı, SSRF korumaları) hiç var olmaz; ürün "yapay zekâ" etiketini hak etmez |
| Yalnız yerel model | Testler belirlenimsiz ve modele bağımlı olur; model yokken hiçbir şey sınanamaz; CI'da kaynak gerektirir |
| Ücretli uzak API | Kısıt gereği yasak; içerik üçüncü tarafa gider |
| Uygulamanın modeli kendisinin indirip çalıştırması | Otomatik büyük indirme ve bellek tüketimi; kısıt gereği yasak |
| Ollama'nın kendi (`/api/chat`) protokolü | Tek çalıştırıcıya bağlar; OpenAI-uyumlu yol birden çok yerel sunucuda bulunduğu varsayımıyla seçildi (aşağıya bakın) |
| Demo sağlayıcıyı "yapay zekâ" olarak sunmak | Yanıltıcı; açıkça demo olarak etiketlenir |

## Sonuçlar / maliyet

- Yerel model yokken canlı kalite, halüsinasyon ve talimat izleme davranışı **bilinmez**; demo sağlayıcıyla bu konularda ölçüm üretilmez.
- Demo sağlayıcının yanıtları mekaniktir (kaynaktan cümle seçimi); doğal dil kalitesi beklenmez.
- İki uygulamanın bakımı; sözleşme testleri ikisine de uygulanır.
- Model metinden JSON çıkarma kırılgandır; uyumsuz çıktı yanıt yerine `RejectedUnsupported` üretir.

## Değişme koşulu

- Yerel model çalıştırıcı kurulup açıkça yapılandırılırsa `ai-live` kapısı çalıştırılır ve sonuçları `docs/ai-verification.md` içine yazılır.
- Kullanılan sunucunun yapılandırılmış çıktıyı (JSON şeması) güvenilir desteklediği doğrulanırsa adaptör bunu isteğe bağlı kullanabilir; metinden ayrıştırma yedek olarak kalır.

## Kaynaklar

Ollama, LM Studio ve llama.cpp sunucusunun `/v1/chat/completions` yolunu sunduğu ve yanıt biçimleri **bu oturumda resmî belgelerden doğrulanmadı**; yerel çalıştırıcı bulunmadığı için canlı olarak da denenmedi. Adaptör yazılmadan önce (Aşama 7) hedeflenen sunucunun resmî belgesi okunmalı ve ayrıştırma varsayımları ona göre sabitlenmelidir.
