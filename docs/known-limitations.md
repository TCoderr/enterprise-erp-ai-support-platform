# Bilinen Sınırlar

Bu belge ürünün ve doğrulamasının **yapmadığı** şeyleri sayar. Buradaki hiçbir madde "yakında" vaadi değildir.

## 1. Ürün kapsamı

- **Finans yok**: muhasebe, çift taraflı defter, tahsilat, vergi, iskonto, kur, fatura/e-fatura/e-arşiv/e-irsaliye yoktur. Sipariş özeti sayfası yasal belge değildir ve üzerinde bunu yazar.
- **İade yok**: satış iadesi/RMA ve tedarikçiye iade yoktur. Kapanan belge değiştirilemez; düzeltme yeni belgeyle yapılır.
- **Stok modeli dardır**: tam sayı miktar ve tek birim. Ondalıklı ölçü, paket dönüşümü, lot/seri, son kullanma tarihi, raf adresi, sayım oturumu, değerleme ve maliyet (COGS) yoktur. Transfer tek adımdır; yolda stok durumu yoktur.
- **Fazla mal kabul yoktur**: sipariş edilenden fazlası kabul edilemez; yeni PO gerekir.
- **PO onayında dört göz ilkesi yoktur**: limiti yeten kullanıcı kendi PO'sunu onaylayabilir. Limiti aşan bir PO'yu demo kullanıcılarından hiçbiri onaylayamaz; limit artırımı yöneticinin işidir.
- **Tek işletme**: çok kiracılılık, şube/şirket ayrımı yoktur.
- **Bildirimler süreç içidir**: e-posta, SMS veya anlık bildirim gönderilmez.
- **Dosya eki yoktur**: destek talebine ve bilgi belgesine dosya yüklenemez; PDF ayrıştırma yoktur.
- **Arayüz dili yalnız Türkçedir**; yalnız açık tema vardır.

## 2. Doğruluk ve veri

- **Belge numaraları boşluklu olabilir**: sequence transaction dışıdır. Numaralar görüntü amaçlıdır.
- **Idempotency kaydı 24 saat tutulur**: süresi dolan anahtar yeni istek sayılır. Çift etkiyi bu durumda iş referansı benzersizliği ve belge durumu engeller; iş referansı olmayan komutlarda (ör. taslak oluşturma) süresi dolmuş anahtarın tekrarı ikinci bir taslak oluşturur.
- **Yalnız başarılı sonuçlar saklanır**: reddedilen (4xx) bir komutun aynı anahtarla tekrarı o anki duruma göre yeniden değerlendirilir; ilk yanıtın aynısı dönmeyebilir.
- **İstek özeti genel bir kanonik JSON uygulaması değildir**: RFC 8785'in tamamı uygulanmadı; özet, bu API'nin kabul ettiği tipli komutların sunucudaki serileştirmesi üzerinden hesaplanır.
- **Mutabakat onarmaz**: günlük ile bakiye arasında fark bulunursa yalnız raporlanır. Uygulama içinden onarım yolu yoktur; stok düzeltmesi bu farkı kapatmaz (bkz. `04-database-design.md` §6).
- **Yeniden deneme sınırlıdır**: deadlock/kilit zaman aşımında komut en fazla 3 kez denenir, sonra `503` döner. Yüksek çekişmede istemcinin yeniden denemesi gerekir. Gerçek deadlock sonrası tek etki ve kilit zaman aşımıyla 3 denemede vazgeçme test edildi; art arda üç deadlock, anlık görüntü çakışması (3960) ve bağlantı hatasıyla yeniden deneme test edilmedi (`inventory-correctness-verification.md` §9).
- **Zaman damgaları milisaniye hassasiyetindedir**; sıralama eşitliği kimlikle bozulur.

## 3. Güvenlik

- **Güvenlik denetimi yapılmadı**: otomatik taramalar (gizli bilgi desenleri, bilinen açıklı paketler) çalıştırıldıkları andaki veritabanı ve desenlerle sınırlıdır. "Bulgu yok", "açık yok" demek değildir. Sızma testi yoktur. Gizli bilgi taraması bağlantı dizgisi biçimindeki parolaları, özel anahtar bloklarını ve bilinen belirteç biçimlerini arar; kod içinde tırnaklı parola sabitlerini (ör. `const string Password = "…"`) hedeflemez ve `kantar:allow-test-secret` işaretli test satırlarını atlar.
- **Çok faktörlü kimlik doğrulama, tek oturum açma (OIDC/SAML), parola sıfırlama e-postası yoktur.** Parolayı yönetici sıfırlar.
- **Hız sınırı süreç içi ve tek örneklidir**: birden çok API örneğinde sınırlar örnek başınadır. Kaynak adresi varsayılan olarak soket adresidir; vekil arkasında güvenilir vekil listesi yapılandırılmalıdır.
- **Oturum kontrolü her istekte veritabanına gider**: iptal anında etkilidir; maliyeti istek başına ek sorgudur. Yük altında ölçülmedi.
- **Yerel yığında SQL Server `sa` parolası container ortam değişkenindedir** (imaj `_FILE` biçimini belgelemiyor); `docker inspect` yetkisi olan yerel kullanıcı görebilir. Uygulama container'larında parola ortam değişkeni yoktur; Docker secret dosyaları kullanılır.
- **SQL Server bağlantısı şifrelidir ama sunucu sertifikası doğrulanmaz** (`TrustServerCertificate=True`): yerel Compose ağında container'ın kendinden imzalı sertifikası kullanılır. Üretim için uygun değildir.
- **HTTPS, .NET geliştirme sertifikasıdır**; tarayıcı güvenmez.
- **Veritabanı düzeyinde satır güvenliği (RLS) yoktur**: depo kapsamı uygulama sorgularında uygulanır. Kısıtlı veritabanı rolü günlüklerin değiştirilmesini ve şema değişikliğini engeller, depo ayrımını değil.
- **Denetim kaydı uygulama tablosudur**: kısıtlı rol değiştiremez; veritabanı sahibi değiştirebilir. Kurcalamaya dayanıklı (imzalı/zincirli) değildir.

## 4. Kaynaklı yardım (AI)

- **Bu makinede gerçek bir yerel model yoktu**: canlı model kapısı (`ai-live`) koşulamadı ve `not-run` kaydetti; **canlı AI kabulü yoktur**. Doğrulananlar: getirme, yetki, atıf doğrulama, yeniden yetkilendirme, hata davranışı ve yerel model adaptörünün istek/yanıt işlemesi (sahte HTTP sunucusuyla). Gerçek bir modelin yanıt kalitesi, uydurma eğilimi, enjeksiyona direnci ve Türkçe başarımı **doğrulanmadı**.
- **Demo sağlayıcı bir dil modeli değildir**: kaynak cümlelerini seçer. Onunla geçen testler model kalitesi hakkında bir şey söylemez ve böyle sunulmaz.
- **Atıf kimliğinin geçerli olması, cümlenin kaynakça desteklendiğini kanıtlamaz.** Sunucu yalnız atıfın gönderilen parçalardan birine işaret ettiğini doğrular; içerik–kaynak uyumunu denetlemez. Arayüz bu yüzden kaynak alıntısını yanıtın yanında gösterir.
- **Arama sözcükseldir**: eş anlamlılar, yazım hataları ve farklı ifadeyle sorulan sorular kaçabilir. Anlamsal (gömme) arama yoktur.
- **Bilgi bankası küçük varsayılmıştır**: her soruda en çok 2000 okunabilir parça belleğe alınır; daha büyük bir bilgi bankasında sonuçlar eksik kalır ve gecikme artar.
- **İstem düzeyi savunmalar güvenlik sınırı değildir**: sınır, sunucunun neyi gönderdiği, hangi atıfı kabul ettiği ve kime teslim ettiğidir.
- **Akışlı (streaming) yanıt yoktur.** Yanıt önbelleği yoktur; her soru yeniden üretilir.
- **Yerel model adaptörü yalnız `/v1/chat/completions` ortak alt kümesini kullanır**; `response_format` desteğine güvenmez. Ollama için yol ve yanıt biçimi resmî belgeden doğrulandı; LM Studio için yalnız yol doğrulandı, yanıt biçimi belgeden doğrulanmadı.
- **Sorular kişisel veri içerebilir**: soru metni saklanmaz ve günlüğe yazılmaz, ancak yerel model sunucusuna gönderilir; o sunucunun kendi kayıtları Kantar'ın denetiminde değildir.

## 5. Doğrulamanın sınırları

- **Uzak CI çalıştırılmadı**: depo hiç push edilmedi. `.github/workflows/ci.yml` yalnız yazıldı; yerel sonuçlar uzak sonuç değildir.
- **Yük, performans ve dayanıklılık testi yapılmadı.** Eşzamanlılık testleri doğruluk içindir (onlarca paralel istek), kapasite için değil.
- **Çok örnekli çalıştırma sınanmadı**: birden çok API veya worker örneği aynı anda çalıştırılmadı. Outbox çit mantığı tek süreç içinde iki dağıtıcıyla sınandı.
- **Ekran okuyucu testi yapılmadı**: otomatik axe taraması ve klavye denetimi, ekran okuyucuyla elle testin yerini tutmaz.
- **Tarayıcı kapsamı**: yalnız Chrome (Chromium). Firefox, Safari ve mobil tarayıcılar sınanmadı.
- **Yükseltme testi iki migration ile sınırlıdır**; uzun bir sürüm geçmişi yoktur.
- **Bellek kısıtı**: bu makinede kapılar ayrı çağrılarla, sırayla koşuldu. Hangi kapının hangi çağrıda ve hangi kaynak parmak izinde koştuğu `final-verification.md` içinde yazılıdır; tek çağrıda bütün kapıların koştuğu bir sonuç **yoktur**. Doğrulama sırasında betik ve test düzeltmeleri nedeniyle kaynak parmak izi dört kez değişti; her durumun sonuçları orada ayrı tutulur ve bütün kapılar son parmak izinde yeniden koşuldu.
- **Seed sırasında iki EF Core uyarısı** (hata değil) günlüğe yazılır: `First` işlecinin `OrderBy` olmadan kullanılması ve birden çok koleksiyonun tek sorguda yüklenmesi. Giderilmedi.
- **SQL Server Developer sürümü** yalnız geliştirme/test lisanslıdır; üretim davranışı (Standard/Enterprise, yüksek erişilebilirlik) sınanmadı.
