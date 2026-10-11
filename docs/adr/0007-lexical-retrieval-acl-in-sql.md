# ADR-0007 — Sözcüksel getirme, yetki filtresi SQL sorgusunda

Durum: Kabul edildi · Tarih: 2026-10-11

## Bağlam

Kaynaklı yardım, soruya en ilgili bilgi bankası parçalarını bulmalıdır. İki gereksinim birbirinden bağımsızdır:

1. **Erişim:** kullanıcının okuyamadığı, taslak, emekli veya eski sürüm metin hiçbir aşamada aday olmamalı; yanıt üreticiye gönderilmemelidir. Çıktıyı sonradan maskelemek tek güvence olamaz.
2. **İlgi:** küçük, sentetik bir prosedür ve hata kodu kataloğunda doğru parçayı bulmak.

Kısıtlar: bellek dar; model/gömme indirme, GPU kurulumu ve ücretli hizmet yok. Resmî SQL Server container imajında tam metin arama kurulu değildir (`IsFullTextInstalled = 0` ölçüldü).

## Karar

- **Yetki filtresi sorgunun içindedir.** Aday parçalar tek bir SQL sorgusuyla getirilir; `WHERE` koşulu şunları birlikte ister: belge `Published`, parça `PublishedVersionId` sürümüne ait, görünürlük sınıfı kullanıcının rollerine uygun, modül kuralı sağlanıyor, depo kapsamı varsa kullanıcının o depoda yetkisi var. Roller ve depo yetkileri sorgu anında veritabanından okunur.
- **Ayrı dizin yok.** Parçalar yayınlama transaction'ında yazılır; sorgu her seferinde `PublishedVersionId` ile filtreler. Yayın, emekli etme ve görünürlük değişikliği commit edildiği anda bir sonraki sorguda geçerlidir; arka planda eşitlenen, geride kalabilen bir dizin yoktur.
- **Puanlama uygulama içindedir.** Yetkili aday kümesi belleğe alınır; Türkçe'ye uygun küçük harfe çevirme, aksan katlama, durak sözcük çıkarma ve basit önek kırpmadan sonra BM25 benzeri puan hesaplanır. Soruda hata kodu deseni varsa aynı kodlu belge öne alınır. Eşik altı sonuç "kaynak yok" sayılır.
- **Sınırlar:** en çok 5 parça, parça başına 1500, toplam 6000 karakter.
- **Önbellek yok.** Ne getirme sonucu ne yanıt önbelleğe alınır.
- **Teslimden önce yeniden denetim.** Atıf yapılan parçalar aynı yetki sorgusuyla kimlikleri üzerinden yeniden doğrulanır.
- Gömme (embedding), vektör veritabanı ve anlamsal arama yoktur.

## Değerlendirilen alternatifler

| Alternatif | Neden seçilmedi |
| --- | --- |
| SQL Server Full-Text Search | Resmî imajda kurulu değil; özel imaj oluşturmak ve yerel + CI'da sürdürmek gerekir. Türkçe sözcük ayırıcı davranışı bu oturumda doğrulanmadı |
| Gömme + vektör arama | Model indirme, ek bellek ve ek bileşen; gömme çağrısı içeriği başka bir sürece taşır (yeni güvenlik yüzeyi). Küçük, terimleri belirgin katalog için gerekçesi yok |
| İki getirme yolunu birlikte tutmak (sözcüksel + anlamsal) | Dekoratif olurdu; iki davranış, iki test matrisi |
| Önce geniş getir, sonra uygulamada yetki süz | Yetkisiz metin belleğe alınır; bir hata onu sağlayıcıya sızdırabilir. Filtre veri kaynağında olmalı |
| Arka plan dizinleyici | Yayın/emekli ile dizin arasında yarış penceresi açar |
| Yanıt/getirme önbelleği | Kullanıcı + rol + depo + KB sürümüyle anahtarlanmazsa yetkiler arası sızıntı riski; ölçülmüş gecikme sorunu yokken gereksiz |

## Sonuçlar / maliyet

- Her soruda yetkili aday kümesi belleğe alınıp puanlanır; maliyet parça sayısıyla doğrusal artar. Aday kümesi 2000 parçayı aşarsa sorgu modül ipucuyla daraltılır.
- Eş anlamlılar, yazım hataları ve farklı ifadeyle sorulan sorularda ilgili parça kaçırılabilir; bu durumda sonuç "yeterli yetkili kaynak yok" olur. Kaçırma oranı ölçülmemiştir.
- Türkçe ek yapısı yalnız kaba bir önek kırpmayla ele alınır; biçimbilimsel çözümleme yoktur.
- Eşik değeri elle seçilir; yanlış seçilirse ya ilgisiz kaynakla yanıt üretilir ya gereksiz "kaynak yok" döner.

## Değişme koşulu

- Bilgi bankası birkaç bin parçayı aşarsa veya puanlama süresi ölçülebilir bir gecikme yaratırsa: tam metin arama kurulu özel imaj ya da ayrı bir arama bileşeni.
- Sözcüksel kaçırma, gerçek kullanımda belgelenmiş bir sorun olursa: anlamsal getirme için maliyet ve güvenlik değerlendirmesi içeren yeni bir ADR. Yetki filtresinin veri kaynağında kalması koşulu değişmez.

## Kaynaklar

`IsFullTextInstalled = 0` bu oturumda `2022-CU27-ubuntu-22.04` imajında ölçüldü (ADR-0001). SQL Server Full-Text Search'ün Linux'ta kurulum belgeleri ve BM25 tanımı bu oturumda resmî kaynaktan doğrulanmadı.
