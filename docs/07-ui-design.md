# 07 — Arayüz Tasarımı

Bu belge Kantar arayüzünün tasarım kaynaklarını, bilgi mimarisini, ekran envanterini, belirteçlerini (token) ve erişilebilirlik planını tanımlar. Kalıcı ürün bağlamı kökteki `PRODUCT.md`, görsel dünya ve belirteçler kökteki `DESIGN.md` dosyasındadır; statik ön izleme `design/preview.html` dosyasıdır.

## 1. Tasarım kaynakları ve nasıl uygulandıkları

| Kaynak | Sürüm / referans | Lisans | Bu projede ne yapıldı |
| --- | --- | --- | --- |
| Impeccable (pbakaus/impeccable) | `skill-v4.5.2`, commit `d631a8827f99414d2b6daba4ef08b7f8701751d7` | Apache-2.0 | Beceri dosyaları proje kapsamında `.claude/skills/impeccable/` altına kopyalandı (`plugin/skills/impeccable` içeriği + depo kökündeki `LICENSE` ve `NOTICE.md`). Ana tasarım yöneticisi budur. |
| Vercel Web Interface Guidelines | <https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md> (beceri tanımı: <https://github.com/vercel-labs/agent-skills/tree/main/skills/web-design-guidelines>) | `vercel-labs/agent-skills` deposu GitHub API'sinde lisans bildirmiyor (`license: null`) | Metni depoya **alınmadı**; kurallar okunup erişilebilirlik, odak, form, tipografi ve içerik taşması denetiminde kontrol listesi olarak uygulandı ve yalnız bağlantıyla anılıyor. |

Impeccable kurulumunda bilinçli olarak yapılmayanlar:

- **Başlatıcı (`scripts/impeccable`) çalıştırılmadı.** Başlatıcı ilk çalıştırmada kullanıcının ev dizinine (`~/.impeccable/bin/`) bir ikili indirir; bu, bu görevin izin vermediği genel (global) bir kurulumdur. Becerinin kendi "Launcher unavailable" yolu izlendi: bağlam (`PRODUCT.md`, `DESIGN.md`) doğrudan okundu/yazıldı.
- **Kanca (hook) kurulmadı**, `.claude/settings*.json` yazılmadı. Dolayısıyla otomatik dedektör taraması (59 kural) **çalışmadı**; `craft-floor.md` ve `audit.md` içindeki denetimler elle uygulandı.
- **`concept-seed` zar atışı yapılmadı** (başlatıcıya bağlıdır). Yön, `new-work.md` 1–3. adımlarındaki türetme ve `mode-operate.md` kuralları elle izlenerek seçildi; bu sapma burada açıkça kayıtlıdır.
- Görsel üretimi yoktur; "Operate" yüzeyleri becerinin kuralı gereği zaten kod öncelikli (code-led) kurulur.

Uygulanan Impeccable referansları: `init.md` (→ `PRODUCT.md`), `new-work.md` (dünya seçimi), `mode-operate.md` ve `operate.md` (çalışma ekranı kuralları), `typeset.md`, `craft-floor.md` (kalite tabanı ve reddedilecek kalıplar), `document.md` tohum (seed) biçimi (→ `DESIGN.md`). `critique.md` (Nielsen sezgiselleri, bilişsel yük, persona testleri) ve `audit.md` (erişilebilirlik, performans, tema, duyarlı tasarım, uygulama bütünlüğü) gerçek arayüz kurulduktan sonra Aşama 8'de uygulanacak; sonuçları `docs/ui-review.md` içine yazılacaktır.

## 2. Görsel kimlik

### 2.1 Yönün türetilmesi

Ürünün mekanizması: *her adet hesaplıdır — sipariş edilen, rezerve edilen, çıkan, giren ve iptal edilen miktarlar ekranda birbirini tutar.* Kullanıcı sahnesi: parlak depo/ofis ışığında, kol mesafesinden, bazen ayakta okunan tezgâh bilgisayarı.

Kullanıcının dünyasından yedi aday (kategori varsayılanı olan "gri ekran + tek vurgu rengi" ve onun tersi olan "koyu neon pano" listeye alınmadı):

1. **Palet rafı adres sistemi** — mavi dikmeler, turuncu kirişler, beyaz raf adres plakaları (A-03-2).
2. Karbon kopyalı sevk irsaliyesi — beyaz/pembe/sarı nüsha, mavi-siyah baskı, kaşe.
3. Koli etiketi — kraft zemin, kalın dar siyah yazı, barkod.
4. Hırdavat fiyat kataloğu — yoğun kod tabloları, mavi kapak.
5. Tartı fişi (adın düz okuması; yalnız bir aday) — termal kâğıt, ağırlık çentikleri.
6. Kablo renk kodları — kahverengi/mavi/sarı-yeşil yalıtım, teknik lejant.
7. Telli dosya ve kaşe mürekkebi — mavi/pembe karton, mor-mavi mühür.

Seçilen: **1 — palet rafı adres sistemi.** Gerekçe: depo ve satış kullanıcılarının her gün gördüğü, "her şeyin bir adresi var" fikrini taşıyan bir işaretleme sistemi; renkleri kabuk ve eylem için iki net rol verir. 5 numaralı adaydan yalnız bir disiplin alındı: *denge* — imza hamlesi olan terazi şeridi.

`mode-operate.md` sınırı gereği dünya yalnız dört şey verir: yazı, palet, yoğunluk ve bir imza hamlesi. Gezinme, tablo, form ve düğmeler standart web bileşenleridir; raf, plaka ya da defter "kostümü" yoktur.

### 2.2 Yazı tipleri

| Rol | Aile | npm paketi | Lisans |
| --- | --- | --- | --- |
| Arayüzün tamamı | Sofia Sans (değişken) | `@fontsource-variable/sofia-sans` | SIL OFL 1.1 |
| Belge numarası plakası, SKU/kod hücreleri | Sofia Sans Condensed (değişken) | `@fontsource-variable/sofia-sans-condensed` | SIL OFL 1.1 |

- Sofia Sans bir şehir tabela/ulaşım sistemi için çizilmiş bir grotesktir; raf adres plakalarının üslubuyla örtüşür. Kardeş ürün Kulvar'ın kullandığı IBM Plex kullanılmaz; Impeccable'ın "eğitim verisi varsayılanı" saydığı aileler (Inter, IBM Plex, DM Sans, Space Grotesk, Outfit, Plus Jakarta Sans vb.) seçilmedi.
- Alt kümeler: `latin` + `latin-ext` (Türkçe `ğ ü ş ı İ ö ç`). Yazı dosyaları paketle birlikte yerelde sunulur; dış istek yoktur. OFL metinleri `src/web/public/licenses/` altına ve `THIRD-PARTY-NOTICES.md` içine eklenecektir.
- Sayılar: tüm miktar, tutar ve tarih sütunlarında `font-variant-numeric: tabular-nums lining-nums`; miktar sütunları sağa hizalıdır.
- **Doğrulanacak (yazılar pakete girince, gerçek tarayıcıda):** `tnum` özelliğinin etkin olduğu ve Türkçe gliflerin yedek yazıdan değil yazı dosyasından geldiği. Biri sağlanmazsa yedek aday: Source Sans 3 (`@fontsource-variable/source-sans-3`). Paket sürümleri kurulumda kilit dosyasıyla sabitlenecektir.
- Türkçe büyük/küçük harf dönüşümü hatası riskine karşı CSS `text-transform` kullanılmaz; metinler yazıldığı gibi gösterilir, kök öğe `lang="tr"` taşır.

### 2.3 Renk belirteçleri

Karşıtlık oranları WCAG 2.x göreli parlaklık formülüyle hesaplandı.

| Belirteç | Onaltılık | Kullanım | Karşıtlık |
| --- | --- | --- | --- |
| `--ink` | `#16202B` | Metin | panel üstünde 16,46:1; zemin üstünde 14,62:1 |
| `--ink-muted` | `#4A5866` | İkincil metin | 7,29:1 / 6,48:1 |
| `--placeholder` | `#5C6A78` | Yer tutucu | 5,54:1 |
| `--ground` | `#EEF2F5` | Uygulama zemini | – |
| `--panel` | `#FFFFFF` | Tablo, form, panel | – |
| `--zebra` | `#F6F8FA` | Seçmeli satır tonu | – |
| `--line` | `#C5CED6` | İnce ayırıcı (bilgi taşımaz) | 1,59:1 |
| `--line-strong` | `#7C8A97` | Girdi/düğme kenarlığı | 3,54:1 / 3,14:1 (≥ 3:1) |
| `--rail` | `#14304A` | Gezinme rayı, belge plakası | ray metni `#E8EFF6` 11,67:1; soluk `#AEC2D4` 7,39:1 |
| `--rail-active` | `#0C2135` | Raydaki geçerli öğe | beyaz 16,35:1 |
| `--accent` | `#B4410A` | Birincil eylem, seçim | beyaz 5,67:1 |
| `--accent-hover` | `#933407` | Üzerine gelme/basılı | beyaz 7,68:1 |
| `--accent-text` | `#A23A08` | Vurgu metni | 6,68:1 |
| `--link` | `#1A568A` | Bağlantı | 7,66:1 |
| `--focus` | `#0B5FCC` | Odak halkası | 5,96:1; rayda `#FFD25A` 9,42:1 |
| `--ok` / `--ok-bg` | `#17603B` / `#E3F2E9` | Tamamlandı | 6,54:1 |
| `--warn` / `--warn-bg` | `#6F4700` / `#FFF0C7` | Uyarı, düşük stok, kısmi | 7,19:1 |
| `--danger` / `--danger-bg` | `#9C2219` / `#FCE7E4` | Hata, reddedildi | 6,66:1 |
| `--info` / `--info-bg` | `#1A568A` / `#E2EDF7` | Bilgi, onaylı | 6,45:1 |
| `--neutral` / `--neutral-bg` | `#3F4C59` / `#E7ECF0` | Taslak, iptal | 7,39:1 |

Renk beş iş görür: kabuk rayı, birincil eylem/geçerli seçim, durum, terazi şeridinin dilimleri ve odak. Süs için renk kullanılmaz; ikinci bir vurgu rengi yoktur. Açık tema esastır (kullanım sahnesi parlak ışık); koyu tema aynı belirteç adlarıyla eklenebilir ama tasarlanmamıştır.

### 2.4 Ölçek, boşluk, biçim

- Yazı ölçeği (sabit rem): 0,8125 etiket · 0,875 üst veri · 0,9375 tablo verisi · 1 gövde · 1,125 bölüm · 1,25 alt başlık · 1,5 sayfa başlığı. Akışkan (clamp) boyut yok.
- Boşluk (4 px taban): 4 · 8 · 12 · 16 · 24 · 32 · 48. Grup içi 4–8, gruplar arası 16, bölgeler arası 24–32.
- Yarıçap: denetim, etiket ve plakalarda 2 px; panellerde 4 px. Hap (pill) biçimi yok.
- Kenarlık 1 px; odak halkası 2 px + 2 px boşluk.
- Yükselti: düz. Yalnız sayfanın üstünde yüzen katmanlar (dar ekranda gezinme çekmecesi ve yardım paneli, menüler) gölge taşır: `0 8px 24px rgba(12,33,53,.18)`.
- Hareket: yalnız durum geri bildirimi, 150–200 ms, `opacity`/`transform`; `prefers-reduced-motion: reduce` altında kapalı. `transition: all` kullanılmaz.

### 2.5 Durum sözlüğü

Durum her zaman **glif + sözcük** olarak verilir; renk üçüncü işarettir.

| Durum | Glif | Ton |
| --- | --- | --- |
| Taslak | İçi boş daire | nötr |
| Onaylandı / Onaylı | Dolu daire | bilgi |
| Kısmen sevk edildi / Kısmen kabul edildi | Yarısı dolu daire | uyarı |
| Sevk edildi / Teslim alındı / Çözüldü | Onay işaretli daire | başarılı |
| İptal edildi | Çizgili daire | nötr (+ "3/10 sevk edildi" açıklaması) |
| Düşük stok, uyarı | İşaretli üçgen | uyarı |
| Hata, reddedildi | Çarpı işaretli sekizgen | tehlike |

Glifler tek 16 px ızgarada, 1,5 px çizgiyle çizilmiş satır içi SVG'dir; emoji veya Unicode simge kullanılmaz.

## 3. Bilgi mimarisi

Gezinme göreve göredir ve role göre süzülür. Kullanıcının rolü olmayan grup hiç görünmez (gizlemek yetki değildir; yetki sunucudadır).

| Grup | Öğeler | Gören roller |
| --- | --- | --- |
| **Satış** | Siparişler · Yeni sipariş | SalesAgent, SalesManager |
| **Satın Alma** | Satın alma siparişleri · Yeni sipariş | PurchasingAgent |
| **Depo** | Stok bakiyeleri · Hareketler · Sevk bekleyenler · Kabul bekleyenler · Transfer · Düzeltme | WarehouseOperator (Transfer: Operate; Düzeltme: Supervise). Stok bakiyeleri ve Hareketler ayrıca depo yetkisi olan satış/satın alma rollerine |
| **Destek** | Taleplerim · Yeni talep · Talep kuyruğu (yalnız destek personeli) | Herkes / SupportAgent, SupportManager |
| **Bilgi Bankası** | Makaleler · Belge yönetimi (yalnız destek personeli) | Herkes / SupportAgent, SupportManager |
| **Yönetim** | Kullanıcılar · Ana veri · Denetim kaydı · Operasyon | SystemAdmin |

Kabukta ayrıca: etkin depo seçici (yalnız yetkili depolar), bildirimler, kullanıcı menüsü (parola değiştir, çıkış), **Kaynaklı Yardım** düğmesi. Açılış sayfası rolün ilk görev listesidir (satışçı için siparişleri, depocu için sevk/kabul bekleyenler); her ekranda aynı KPI kartları yoktur.

Süzgeç, sekme, sayfa imleci ve açık panel URL'ye yansır; listeler `<a>` bağlantılarıyla gezilir (Ctrl/orta tık çalışır).

## 4. Ekran envanteri

Her ekran için ortak durumlar: **yükleniyor** (içerik biçiminde iskelet, ortada dönen simge yok), **boş** (ne olduğunu ve ilk adımı söyler), **hata** (sorun + kurtarma + korelasyon kimliği), **yetkisiz/bulunamadı** (kapsam dışı belge "bulunamadı" olarak gösterilir, varlığı ima edilmez).

| Ekran | Amaç | Ekrana özgü durumlar |
| --- | --- | --- |
| Giriş | Oturum açma | Hatalı giriş (kullanıcı var/yok ayrımı yok), kilitli hesap, hız sınırı (429 + bekleme süresi), parola değişikliği zorunlu |
| Satış siparişleri listesi | Duruma/depoya göre bulma | Süzgeç sonucu boş; imleçle "daha fazla" |
| Satış siparişi (taslak düzenleme) | Müşteri, depo, satırlar; anlık kullanılabilir stok ipucu | Pasif ana veri (`master_data_inactive`), eski sürüm (412), kaydedilmemiş değişiklik uyarısı |
| Satış siparişi (belge) | Durum, terazi şeridi, satırlar, sevkiyatlar; onayla / iptal | **Stok yetersiz (409)**: hangi satır, istenen ve kullanılabilir; hiçbir satır rezerve edilmedi bilgisi. **Eski sürüm (412)**: "Belge siz bakarken değişti" + yeniden yükle. Geçersiz durum (409) |
| Sevkiyat girişi (depo) | Açık miktara kadar sevk; sevk referansı | Açık miktarı aşma, yinelenen iş referansı (409 + mevcut belge no), kısmi sevk sonucu |
| Yazdırılabilir sipariş özeti | Baskıya uygun özet | "Bu belge fatura veya irsaliye değildir." ibaresi her zaman |
| Satın alma siparişleri / taslak / belge | PO yaşam döngüsü; onayla / iptal | Onay limiti aşıldı (403 `approval_limit_exceeded`), eski sürüm (412) |
| Mal kabul girişi (depo) | Kalan miktara kadar kabul; tedarikçi irsaliye no | **Kısmi kabul**: kalan miktar kabulden sonra görünür; kalanı aşma (409 `exceeds_remaining`), yinelenen irsaliye (409), satır uyuşmazlığı (422) |
| Stok bakiyeleri | Depo bazında eldeki / rezerve / kullanılabilir | Düşük stok işareti; yetkisiz depo → bulunamadı |
| Hareket günlüğü | Değiştirilemez hareketleri süzme | Süzgeç boş; yatay kaydırmalı tablo |
| Transfer / Düzeltme | Tek adımda kayıt | Yetersiz kullanılabilir (409), hedef depoda yetki yok, gerekçe zorunlu |
| Taleplerim / Talep kuyruğu / Talep | Talep, yorumlar, durum, atama | İç not (yalnız destek), ilişkili belge yalnız tür + numara metni, kapalı talep |
| **Kaynaklı Yardım paneli** | Kaynaklı açıklama | `Answered`; **kaynak yok** (`NoSource`); **sağlayıcı kullanılamıyor** (`ProviderUnavailable`, kaynak listesi yine görünür); **reddedildi** (`RejectedUnsupported`); **erişim değişti** (`AccessChanged`); **demo sağlayıcı etiketi**; yanıt süresi doldu |
| Makaleler / Makale | Okunabilir yayınlı belgeler | Okunamayan makale → bulunamadı; güvenli Markdown |
| Belge yönetimi | Taslak, sürümler, yayınla, emekli et | Eski sürüm (412), yayınlama yetkisi yok |
| Kullanıcılar / Ana veri / Denetim / Operasyon | Yönetim | Geçici parola bir kez gösterilir; mutabakat farkı uyarısı ("onarıldı" denmez) |

Hiçbir düğme sahte başarı göstermez: sonuç yalnız sunucu yanıtından sonra yazılır; bekleyen istek sırasında düğme "…" ile biten etiket ve meşgul durumu taşır; yanıtı bilinmeyen (ağ koptu) komut için "Sonuç bilinmiyor — aynı işlemi güvenle yeniden deneyebilirsiniz" durumu vardır (aynı `Idempotency-Key` ile yeniden gönderilir).

## 5. Belge sayfası anatomisi

1. **Başlık**: belge numarası plakası (dar yazı, ray rengi) · başlık (müşteri/tedarikçi) · durum etiketi (glif + sözcük) · depo · sahip · tarih.
2. **Akış adımları**: `Taslak → Onaylandı → Kısmen sevk → Sevk edildi` sıralı listesi (`<ol>`); tamamlanan, geçerli (`aria-current="step"`) ve gelecek adımlar hem biçim hem metinle ayrışır. İptal, akışın dışında ayrı bir sonuç satırı olarak gösterilir.
3. **Terazi şeridi**: orantılı tek çubuk + altındaki rakamlar. Satışta `Sipariş = Sevk edilen + İptal + Açık (rezerve)`, satın almada `Sipariş = Kabul edilen + İptal + Açık`, stokta `Eldeki = Rezerve + Kullanılabilir`. Çubuk `role="img"` ve tam cümlelik bir `aria-label` taşır; rakamlar ayrıca metin olarak durur.
4. **Satır tablosu**: SKU · ürün · birim · sipariş · sevk/kabul · iptal · açık · birim fiyat · tutar. Miktarlar sağa hizalı, tabular. Fiyat sütunları yetkisiz rolde (depo görünümü) hiç yoktur.
5. **İlişkili kayıtlar**: sevkiyatlar / mal kabuller (numara, referans, tarih, miktar).
6. **Eylem çubuğu**: tek birincil eylem (duruma göre: Onayla, Sevk et, Mal kabul et), ikinciller (Düzenle, Yazdır) ve yıkıcı eylem (İptal et / Kalanı iptal et) ayrı uçta; yıkıcı eylem satır içi onay adımı ister (modal ilk tercih değildir).

## 6. Kaynaklı Yardım paneli

- Sayfanın yanında açılan bir **yan paneldir**; tam ekran sohbet değildir, geçmiş konuşma tutmaz. Geniş ekranda 380 px sütun, dar ekranda sayfanın üstünde tam yükseklikte sayfa (sheet).
- Giriş: tek soru alanı (3–1000 karakter, sayaç), altında "Sorular kişisel veri içerebilir; soru metni saklanmaz." notu. Talep ekranından açılırsa "Bu talebin modülü ve hata kodu aramada ipucu olarak kullanılır" satırı görünür.
- Yanıt bloğu sırasıyla: **sağlayıcı etiketi** ("Demo sağlayıcı — dil modeli değil" ya da "Yerel model: <ad>") → yanıt metni (düz metin; yalnız kalın, liste, satır içi kod) → **Kaynaklar** listesi (belge başlığı · sürüm · bölüm no ve başlığı; bağlantı makaleyi normal yetkiyle açar) → sınırlamalar → "Yanıt 30 dk sonra silinir".
- Güven puanı, yıldız, yüzde yoktur. Genel "yapay zekâ yanılabilir" şeridi yerine her yanıtın kendi kaynağı, sürümü ve sağlayıcı durumu gösterilir.
- **Talebe öneri olarak kaydet** iki adımlıdır: düğme → hangi talebe ve ne kaydedileceğini gösteren satır içi onay ("TK-… numaralı talebe 'AI önerisi' notu olarak eklenecek") → **Kaydet**. Sonuç sunucudan gelir; kaynak artık okunamıyorsa `source_no_longer_available` durumu gösterilir.
- Durum çeşitleri: kaynak yok ("Yetkili olduğunuz kaynaklarda yeterli bilgi bulunamadı" + aramayı daralt/talep aç), sağlayıcı kullanılamıyor (kaynak listesi yine sunulur), reddedildi, erişim değişti.
- Model çıktısı hiçbir zaman HTML olarak işlenmez; bağlantılar etkinleştirilmez.

## 7. Duyarlı kurallar

| Genişlik | Kabuk | Tablolar | Belge | Yardım |
| --- | --- | --- | --- | --- |
| **1440** | 232 px sol ray + içerik (en çok 1360 px) | Tam tablo, 40 px satır | Başlık ve eylemler yan yana | 380 px yan sütun |
| **768** | Üst çubuk + menü düğmesiyle açılan çekmece | Tam tablo; sığmayan tablo kendi kaydırma bölgesinde, ilk sütun yapışkan | Eylem çubuğu başlığın altında | Sayfa üstü panel |
| **360** | Üst çubuk + çekmece | Her satır **etiketli yığına** döner: tüm sütunlar korunur, her değerin önünde sütun başlığı metni; gizlenen sütun yoktur | Eylem çubuğu ekranın altına yapışır; terazi şeridi rakamları iki sütunlu ızgara | Tam yükseklik sayfa |

- Başlıklar sıkışmaz: uzun müşteri adı ve ürün adı sarılır (`overflow-wrap: anywhere`), flex çocukları `min-width: 0` taşır.
- Hareket günlüğü gibi tabular kalması gereken tablolar `tabindex="0"`, `role="region"` ve `aria-label` taşıyan yatay kaydırma bölgesindedir.
- Dokunma hedefleri en az 40 × 40 px; `touch-action: manipulation`; yakınlaştırma engellenmez.
- %200 metin ölçeğinde içerik kaybı olmaz (sabit yükseklikli metin kutusu yok).

Kritik ekranlar (sipariş belgesi, mal kabul, stok bakiyeleri, talep + yardım paneli, giriş) gerçek tarayıcıda 360/768/1440 genişliklerinde elle incelenecektir; ekran görüntüsü üretmek tek başına inceleme sayılmaz.

## 8. Erişilebilirlik planı

- **Klavye**: tüm akışlar yalnız klavyeyle tamamlanır; "İçeriğe geç" bağlantısı; mantıksal sekme sırası; çekmece ve panel açıldığında odak içine taşınır, `Esc` ile kapanır ve odak tetikleyiciye döner.
- **Odak**: `:focus-visible` ile 2 px halka + 2 px boşluk; anahat hiçbir yerde karşılıksız kaldırılmaz; yapışkan çubuklar odaktaki öğeyi örtmez (`scroll-margin`).
- **Anlambilim**: eylem için `<button>`, gezinme için `<a>`; tablolar gerçek `<table>` (`<caption>`, `scope`); başlık hiyerarşisi sıralı; simge düğmeleri `aria-label` taşır, süs simgeleri `aria-hidden`.
- **Formlar**: her girdi görünür `<label>` ile; `autocomplete`, doğru `type`/`inputmode`; yapıştırma engellenmez; hata gönderimde **hata özeti** (odak alır, alanlara bağlantı verir) + alan altında satır içi ileti (`aria-describedby`, `aria-invalid`); gönder düğmesi istek başlayana kadar etkin kalır.
- **Canlı bölgeler**: eşzamansız sonuçlar `aria-live="polite"` (başarı, yardım yanıtı geldi), engelleyici hatalar `role="alert"`.
- **Renk**: tüm metin ≥ 4,5:1, denetim kenarlıkları ≥ 3:1 (bkz. 2.3); durum hiçbir yerde yalnız renkle verilmez.
- **Hareket**: `prefers-reduced-motion` altında geçiş yok.
- **Dil ve biçim**: `lang="tr"`; sayı, para ve tarih `Intl` ile `tr-TR` biçiminde; kod ve belge numaraları `translate="no"`.
- **Sınır**: otomatik axe taraması yapılacaktır, ancak axe bir ekran okuyucu testi **değildir**. Elle ekran okuyucu (NVDA/Narrator) geçişi bu görevde yapılmamışsa `known-limitations.md` içinde böyle yazılır.

## 9. İstemci durum kuralları

- Sunucu durumu sorgu önbelleğindedir ve **her anahtar kullanıcı kimliğiyle başlar** (`[userId, 'sales-orders', …]`). Çıkışta, oturum 401 döndüğünde ve `me` yanıtındaki kullanıcı kimliği değiştiğinde önbellek tümüyle temizlenir ve bekleyen sorgular iptal edilir.
- Uçuştaki eski yanıt yeni kullanıcının ekranına yazamaz: istekler `AbortController` ile iptal edilir ve yanıt, başladığı andaki oturum sayacıyla (epoch) karşılaştırılır; uyuşmayan yanıt atılır.
- Roller ve depo yetkileri yalnız `GET /api/auth/me` yanıtından okunur; istemcide türetilmiş yetki kalıcılaştırılmaz. Arayüzde bir öğeyi gizlemek güvenlik değildir.
- `localStorage`/`sessionStorage` içine oturum belirteci, CSRF belirteci, model kimlik bilgisi, yardım sorusu veya yanıtı **yazılmaz**. CSRF belirteci bellekte tutulur. Kalıcılaştırılabilen tek şey zararsız görünüm tercihidir (ör. son seçilen depo kodu) ve o da kullanıcı kimliğiyle anahtarlanır.
- Değişiklik isteklerinde `Idempotency-Key` komut başına bir kez üretilir ve aynı komutun yeniden denemesinde aynen kullanılır; `If-Match` belgenin son okunan `ETag` değeridir. 412 sonrası belge yeniden okunur, kullanıcının girdiği form değerleri korunur.
- İyimser (optimistic) arayüz güncellemesi stok ve belge durumunda kullanılmaz.

## 10. Ön izleme

`design/preview.html` tek dosyalık, dış istek yapmayan, örnek verili statik bir prototiptir ve üstünde "Tasarım ön izlemesi — örnek veri, gerçek API değil" şeridi taşır. Kabuk ve gezinmeyi, kısmen sevk edilmiş bir satış siparişini, kısmi mal kabul formlu bir satın alma siparişini, düşük stok işaretli bakiye listesini, Kaynaklı Yardım paneli açık bir destek talebini (yanıt + kaynaklar + demo etiketi ve "kaynak yok" çeşidi) ve durum galerisini (boş, hata, 412, 409, yetkisiz) içerir. Prototipte yazı tipleri paketlenmediği için Sofia Sans yığının başında adlandırılmış, sistem yazıları yedek olarak kullanılmıştır. Gerçek arayüz yalnız gerçek API'lerle çalışır.
