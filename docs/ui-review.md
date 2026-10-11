# Arayüz İncelemesi

Tasarım kaynakları, uygulanan kararlar, gerçek tarayıcıda yapılan inceleme ve bulgular. Koşu sayıları: `final-verification.md`.

## 1. Tasarım kaynakları ve nasıl kullanıldıkları

| Kaynak | Sürüm | Kullanım | Kurulum durumu |
| --- | --- | --- | --- |
| **Impeccable** (<https://github.com/pbakaus/impeccable>) | `skill-v4.5.2`, commit `d631a8827f99414d2b6daba4ef08b7f8701751d7`, Apache-2.0 | `init` → `PRODUCT.md`; yön ve plan (`new-work`, `shape`, `mode-operate`, `operate`); uygulama öncesi `craft-floor`; sonda `critique`/`audit`/`polish` ölçütleriyle inceleme | Beceri dosyaları **proje kapsamında** `.claude/skills/impeccable/` altına kopyalandı (`LICENSE`, `NOTICE.md` dahil). Genel (kullanıcı profili) kurulum yapılmadı |
| **Vercel Web Interface Guidelines** (`command.md`) | 2026-10-11'de okunan ana dal | Erişilebilirlik, odak, form, tipografi, içerik taşması ve karşı-örüntü denetim listesi olarak son inceleme | Metin depoya alınmadı (depo lisans bildirmiyor); yalnız uygulandı |

**Becerinin tam olarak kullanılamayan kısımları** (uydurulmadı, açıkça yazılıyor):

- Impeccable'ın `scripts/impeccable` başlatıcısı **çalıştırılmadı**: ilk çalıştırmada kullanıcı profiline (`~/.impeccable/bin`) ikili dosya indiriyor; bu, "genel kurulum yok" kuralına aykırıydı. Becerinin kendi "başlatıcı kullanılamıyor" yolu izlendi: bağlam dosyaları doğrudan okundu.
- Bu yüzden başlatıcıya bağlı adımlar **yapılmadı**: `concept-seed` (yön zarı), otomatik dedektör taraması (59 kural), kanca (hook) tabanlı düzenleme sonrası denetim, `live`/`generate` tarayıcı varyant modu.
- `DESIGN.md` beceri kuralına göre inşa sonunda üretilir; burada önce "tohum" olarak yazıldı, uygulama bittikten sonra elle güncel tutuldu.
- Beceri komutları (`/impeccable critique` vb.) oturum içinde komut olarak çağrılmadı; ilgili başvuru dosyalarındaki ölçütler elle uygulandı.
- UI UX Pro Max kullanılmadı (tek tasarım yöneticisi: Impeccable).

## 2. Kimlik ve belirteçler

- **Ad ve dünya**: Kantar. Palet rafı adres sistemi: koyu mavi dikme rengi kabuk, kiriş turuncusu tek eylem rengi, soğuk beyaz/gri çalışma yüzeyi. Yalnız açık tema.
- **İmza öğesi**: "terazi şeridi" — `Sipariş = Sevk/Kabul + İptal + Açık` (stokta `Eldeki = Rezerve + Kullanılabilir`) orantılı tek çubuk ve altında rakamlarla eşitlik.
- **Yazı**: Sofia Sans (arayüz) ve Sofia Sans Condensed (belge numarası plakası, SKU/kod hücreleri); ikisi de OFL 1.1, uygulamayla birlikte yerelden sunulur.
- **Ölçek**: 0,8125 / 0,875 / 0,9375 / 1 / 1,125 / 1,25 / 1,5 rem; boşluk 4-8-12-16-24-32-48 px; yarıçap 2 px (denetim, etiket, plaka) ve 4 px (panel).

| Belirteç | Değer | Karşıtlık |
| --- | --- | --- |
| `ink` / `ink-muted` | `#16202B` / `#4A5866` | panelde 16,46:1 / 7,29:1 |
| `ground` / `panel` | `#EEF2F5` / `#FFFFFF` | – |
| `line-strong` (denetim kenarı) | `#7C8A97` | 3,54:1 |
| `rail` | `#14304A` (metin `#E8EFF6`) | 11,67:1 |
| `accent` / hover | `#B4410A` / `#933407` | beyaz üstünde 5,67:1 / 7,68:1 |
| `link` | `#1A568A` | 7,66:1 |
| `focus` | `#0B5FCC` (rayda `#FFD25A`) | 5,96:1 / 9,42:1 |
| durum metinleri (ok / uyarı / tehlike / bilgi / nötr) | `#17603B` / `#6F4700` / `#9C2219` / `#1A568A` / `#3F4C59` | kendi zemininde 6,45–7,39:1 |

Karşıtlık oranları belirteçlerden hesaplandı; işlenmiş ekranda ayrıca ölçülmedi (axe taraması renk karşıtlığı kuralını içerir ve ihlal bulmadı).

Durum her yerde **glif + sözcük** ile verilir (boş, yarım, dolu, onaylı daire; üçgen; sekizgen); yalnız renkle verilen durum yoktur.

## 3. Bilerek kaçınılanlar

Mor gradyan, parıltı, her ekranda aynı KPI kart duvarı, krem + serif "lüks" görünüm, broşür tarzı kahraman alanı, tam ekran sohbet. Kaynaklı yardım bir yan paneldir (dar ekranda alttan açılan katman); ana ürün sohbet ekranına dönüşmez. Uydurma KPI, sahte başarı iletisi veya işlevsiz düğme yoktur: her eylem gerçek API çağrısıdır.

## 4. Gerçek tarayıcıda inceleme

**Nasıl yapıldı**: Playwright (Chrome) gerçek yığına karşı altı kritik ekranı 360×800, 768×1024 ve 1440×900 genişliklerinde açtı; her biri için otomatik düzen denetimleri çalıştı ve tam sayfa ekran görüntüsü kaydedildi (`artifacts/verification/screenshots/<ekran>-<genişlik>.png`). Görüntüler **bir insan tarafından değil**, bu çalışmayı yapan yazılım aracı tarafından açılıp DESIGN.md, Impeccable craft-floor ve Operate ölçütlerine göre değerlendirildi. 18 birleşimin 13'ü ilk geçişte, dördü düzeltmeden sonra yeniden, ayrıca üçü (sipariş 1440, stok 360, talep + yardım 1440) ikinci bir gözle bir kez daha incelendi. `giris-360`, `giris-768`, `yardim-kaynak-yok-360` ve `yardim-kaynak-yok-1440` görüntüleri **açılıp bakılmadı**; onlar yalnız otomatik denetimlerden geçti.

Kritik ekranlar: giriş; satış siparişi (kısmen sevk edildi); mal kabul formu; stok bakiyeleri; destek talebi + kaynaklı yardım paneli (yanıtlı); kaynaklı yardım "kaynak yok" durumu.

**Her ekran ve genişlikte otomatik denetlenenler** (hepsi geçti): yatay sayfa taşması yok; her tablo hücresi görünür alanda ya da odaklanabilir kaydırma bölgesinde; ana eylem yana kaydırmadan erişilebilir; 360 px'te her düğme, giriş ve seçim en az 40 px yüksekliğinde.

### Bulgular — düzeltilenler

| Ekran / genişlik | Sorun | Düzeltme |
| --- | --- | --- |
| Mal kabul, 360 | Yapışkan eylem çubuğu not + üç düğmeyle ~170 px tutuyor, hata özetini örtüyordu | Not akışa, "tümünü doldur" referans alanının altına alındı; çubukta yalnız Vazgeç + ana eylem. Sevkiyat, transfer ve düzeltme formlarında da aynı |
| Mal kabul, 768 | Ana düğme tek başına ikinci satıra, sola hizalı düşüyordu | Çubuk sağa hizalı |
| Mal kabul, 768 | Tedarikçi kodu "TED-/ANADOLU" diye bölünüyordu | Kodlar tek satır; yalnız uzun kodlar bölünebilir |
| Stok bakiyeleri, 768 | Durum etiketleri sözcük ortasından bölünüyordu ("Yet/erli") | Durum etiketleri hiç bölünmez |
| Stok bakiyeleri, 768 | Uzun sütun başlığı ürün adını 3–4 satıra sıkıştırıyordu | 1024 px altında başlıklar bölünebilir (yalnız otomatik denetimle doğrulandı) |
| Satış siparişi, 360 | Akış adımları ikinci satıra başta sallanan çizgiyle taşıyordu | Bağlantı çizgisi adımın sonunda |
| Tümü, 360 | Sıkı düğmeler 32 px'ti | 720 px altında 40 px |
| Talep + panel, 360/768 | Katman açıkken arkadaki sayfa kayabiliyordu | Modal katman açıkken kaydırma kilitli |
| Talep + panel, 1440 | Uzun yanıtta panel başlığı ve kapat düğmesi panelin kendi kaydırmasıyla görünümden çıkıyordu | Panel başlığı yapışkan. `e2e` kapısının güncel imajla ürettiği `artifacts/verification/screenshots/talep-yardim-1440.png` görüntüsünde doğrulandı: panel içeriği kaydırılmışken başlık ve kapat düğmesi yerinde |
| Klavye | Atlama bağlantısı yardım katmanının ve gezinme çekmecesinin arkasında odaklanabiliyordu | Modal katman açıkken arka plan etkisiz |
| Klavye | Dar ekranda katman kapanınca odak tetikleyiciye dönmüyordu | Düzeltildi |
| Klavye | Yeni sayfa yüklemesinde kabuk odağı içeriğe taşıyor, ilk Tab atlama bağlantısını atlıyordu | Düzeltildi |
| Klavye | Yan sütun halindeki yardım panelini Escape kapatmıyordu | Düzeltildi |
| Tablolar, 720–1200 | Tablo sarmalayıcısı sütunları kırpabiliyordu | Kaydırılabilir bölge |
| Hareket | Renk geçişleri vardı | Yalnız `transform`/`opacity` canlandırılır; azaltılmış hareket tercihinde kapalı |

### Bulgular — görülüp bırakılanlar

- **Satış siparişi, 360**: terazi şeridi ilk görünümün altında kalıyor; meta liste ve iki satırlı eylem çubuğu ilk ekranı dolduruyor.
- **Stok bakiyeleri, 768**: sekiz sütunlu tabloda ürün adları üç satıra bölünebiliyor.
- **Stok bakiyeleri, 1440**: "Süz" düğmesi onay kutusundan bir ızgara sütunu uzakta.
- **Stok bakiyeleri, 360**: 15 ürün ~4400 px yüksekliğinde tek liste; sanallaştırma veya sayfalama düğmesi yerine "daha fazla" ile ilerliyor.
- **Tarihler**: gün tek haneli yazılabiliyor ("9.10.2026"); tarih sütunları hane hane hizalanmıyor.
- **Talep + panel, 1440**: sınırlamalar metni ve "talebe kaydet" düğmesi panelin kendi kaydırmasının altında.
- **₺ işareti** Sofia Sans alt kümelerinde yok; sistem yazı tipinden geliyor (okunur, ama aynı yazı tipi değil).
- **Kaydedilmemiş değişiklik uyarısı** yalnız sekme kapatma/yenilemede; uygulama içi gezinmede uyarmıyor.
- **412 uyarısı** göründüğünde sayfada iki vurgulu düğme aynı anda bulunabiliyor.
- **Sipariş satırlarındaki kullanılabilirlik ipucu** satır başına bir bakiye isteği yapıyor.
- **Gezinme**: belge ayrıntı sayfasında "Siparişler" öğesi "geçerli" olarak işaretlenmiyor.
- **Odak dönüşü**: satır içi onaydan sonra odak dönüşü `document.activeElement`'e dayanıyor; Safari'de düğme tıklamasında bu ayarlanmaz (Safari'de sınanmadı).

## 5. Erişilebilirlik

- **axe (otomatik)**: giriş (düz ve hata özetli), sipariş listesi, sipariş ayrıntısı (düz ve iptal adımı açık), bilgi bankası makalesi, talep + yardım paneli (yanıtlı), stok bakiyeleri, hareketler, mal kabul formu (düz ve alan hatalı), kullanıcı yönetimi (liste ve oluşturma) — ciddi/kritik ihlal **0**, devre dışı bırakılan kural yok.
- **Klavye**: giriş → yeni talep → yardım panelini aç → sor → Escape ile kapat (odak tetikleyiciye döner) → yeniden aç → kapat düğmesiyle kapat akışı yalnız klavyeyle geçti. 360 px'te 24 Tab basışı yardım katmanının ve çekmecenin içinde kalıyor. Başarısız form gönderimi odağı hata özetine taşıyor; özetteki bağlantılar alanlara odaklanıyor.
- **Yazı tipleri**: `document.fonts.check` Sofia Sans (400, 650) ve Sofia Sans Condensed (700) için ve Türkçe harflerle doğru. Tablo rakamları gerçekten uygulanıyor: on "1" ve on "0" `tabular-nums` ile aynı genişlikte (201,61 px) ölçüldü; orantılı halde 145,28 ve 185,61 px.
- **Yapılmayanlar**: ekran okuyucuyla (NVDA, JAWS, VoiceOver) test **yapılmadı**; axe ve klavye denetimi bunun yerini tutmaz. Gerçek dokunmatik cihaz, Firefox, Safari ve yazdırma çıktısı sınanmadı. Yüksek karşıtlık/zorunlu renk modu sınanmadı.

## 6. İstemci durumu ve gizlilik

- Oturum belirteci yalnız `HttpOnly` çerezdedir; CSRF belirteci yalnız bellekte tutulur. `localStorage`/`sessionStorage`/IndexedDB kullanılmaz; sorular ve yanıtlar yalnız bileşen durumundadır ve panel kapanınca ya da kullanıcı değişince atılır.
- Sorgu önbelleği kullanıcı kimliğiyle anahtarlanır; çıkışta, 401'de ve kullanıcı değişiminde temizlenir, uçuştaki istekler iptal edilir. Tarayıcı testi: aynı sayfada `satis1` çıkıp `depo1` girince önceki kullanıcının sipariş numaraları, fiyatları ve adı DOM'da bulunmuyor.
- Bilgi bankası ve yardım yanıtı metni kendi güvenli işleyicisiyle React öğelerine dönüştürülür (`dangerouslySetInnerHTML` yok); ham HTML düz metin olarak görünür; bağlantılar tıklanabilir yapılmaz. Tarayıcı testi: `<script>` ve `javascript:` içeren soru ve yanıtlarda hiçbir iletişim kutusu açılmıyor.
- Ekran görüntülerinde parola yok: görüntüler girişten sonra alınır; giriş ekranında parola alanı maskelenir; geçici parola bildirimi görüntü alınmadan önce kapatılır.

## 7. Prototip

`design/preview.html` uygulamadan önce yazılan, örnek verili statik ön izlemedir ("Tasarım ön izlemesi — örnek veri, gerçek API değil" bandı taşır). Gerçek arayüz bunun görsel dilini bileşenlere taşıdı. Prototipin kendisi tarayıcıda ayrıca incelenmedi; birkaç ayrıntısı (ör. "KUTU" birimi, "Kaydeden" sütununun yeri) gerçek sözleşmeyle uyuşmadığı için uygulamada farklıdır.
