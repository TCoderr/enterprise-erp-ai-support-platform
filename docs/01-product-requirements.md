# 01 — Ürün Gereksinimleri

## 1. Ürün

**Kantar**, küçük/orta ölçekli bir toptan ticaret işletmesi için sipariş, satın alma, stok ve uygulama destek merkezidir. Tek işletme, birden çok depo içindir; çok kiracılı (SaaS) bir ürün değildir.

Ürün bilinçli olarak dar tutulmuş bir dikey kesittir: büyük bir ERP'nin bütün alanlarını taklit etmez. Kapsadığı akış şudur:

```
Satın alma siparişi → onay → (kısmi) mal kabul → stok
                                                  │
Satış siparişi (taslak) → onay + rezervasyon ─────┤
                              → (kısmi) sevkiyat → kalan iptal
Destek talebi → yayınlı bilgi belgesi → kaynaklı yardım → öneriyi talebe kaydet
```

Kantar'ı benzer örneklerden ayıran iki şey:

1. **Miktarın izlenebilirliği.** Satın alma siparişinden mal kabule, stok bakiyesine, satış rezervasyonuna ve kısmi sevkiyata kadar her adet değiştirilemez bir günlük satırıyla izlenir; eşzamanlı isteklerde fazla satış (oversell) veya çift stok etkisi oluşmaması veritabanı düzeyinde sınanır.
2. **Yetkiye duyarlı destek bilgisi.** Bilgi bankası belgeleri görünürlük sınıfı, modül ve depo kapsamı taşır. Kaynaklı yardım yalnız soran kullanıcının okuyabildiği yayınlı belgelerden yanıt üretir; yetkisiz belge metni yanıt üreticiye hiç gönderilmez.

Yardım işlevi iş verisini değiştirmez; SQL, araç veya komut çalıştırmaz. Ayrıntı: `10-ai-grounding-and-access-design.md`.

## 2. Kullanıcılar (rol başına)

| Rol | Kim | Ne yapmak ister | Neyi göremez / yapamaz |
| --- | --- | --- | --- |
| `SalesAgent` | Satış temsilcisi | Müşteri için taslak sipariş açar, stok yeterliyse onaylar, kendi siparişlerinin sevk durumunu izler | Başkasının siparişi, yetkisiz depo, satın alma maliyeti |
| `SalesManager` | Satış yöneticisi | Yetkili depolardaki tüm siparişleri görür, onaylar/iptal eder; yöneticiye özel prosedürleri okur | Satın alma belgeleri, stok düzeltmesi |
| `PurchasingAgent` | Satın almacı | Tedarikçiye PO açar; onay limiti varsa onaylar; kabul durumunu izler | Satış fiyatları, müşteri verisi; limit üstü onay |
| `WarehouseOperator` | Depo görevlisi / depo sorumlusu | Mal kabul, sevkiyat, transfer yapar; `Supervise` seviyesinde gerekçeli stok düzeltir | Fiyat ve maliyet; yetkisiz depo; Draft belgeler |
| `SupportAgent` | Destek uzmanı | Talepleri yanıtlar, iç not yazar, bilgi belgesi taslağı hazırlar | Belge yayınlama; satış/PO/stok içeriği |
| `SupportManager` | Destek yöneticisi | Talep atar, bilgi belgesi yayınlar/emekli eder | Satış/PO/stok içeriği |
| `SystemAdmin` | Sistem yöneticisi | Kullanıcı, rol, depo yetkisi ve ana veriyi yönetir; denetim kaydı, outbox ve mutabakat raporunu okur | Satış siparişi, PO, stok bakiyesi, `SupportOnly`/`ManagerOnly` belgeler (örtük yetki yok) |

Her oturum açmış kullanıcı destek talebi açabilir ve kendi talebini izleyebilir. Somut yetki matrisi `02-domain-and-state-machines.md` §2.4'tedir.

## 3. Kapsam (modül başına)

| Modül | İlk sürümde var |
| --- | --- |
| Identity / Access | Sunucu taraflı oturum, çerez + CSRF, parola değiştirme/sıfırlama, hesap kilitleme, rol + depo yetkisi (`View`/`Operate`/`Supervise`) + sahiplik, oturum iptali. Açık kayıt yok |
| MasterData | Ürün (SKU, ad, birim, TRY liste fiyatı, yeniden sipariş seviyesi), müşteri, tedarikçi, depo; pasifleştirme; belge snapshot'ları geçmişi korur |
| Purchasing | PO: `Draft → Approved → PartiallyReceived → Received`, kalan iptal; onay limiti; tedarikçi/fiyat snapshot'ı; kısmi mal kabul; tedarikçi irsaliye numarasında iş benzersizliği |
| Inventory | `OnHand` / `Reserved` / `Available`; değiştirilemez hareket günlüğü ve ayrı rezervasyon defteri; depolar arası atomik transfer; gerekçeli +/− düzeltme; hareket raporu; düşük stok listesi; salt okunur mutabakat |
| Sales | Sipariş: `Draft → Confirmed → PartiallyShipped → Shipped`, iptal; sunucu fiyat snapshot'ı; hepsi-ya-da-hiçbiri rezervasyon; kısmi sevkiyat; kalan iptal; yazdırmaya uygun özet (yasal belge değil) |
| Support / Knowledge | Talep (öncelik, durum, atanan, modül, ekran, hata kodu, tür + numara olarak belge bağlantısı), düz metin yorum ve iç not, beyaz listeli tanılama snapshot'ı; sürümlü bilgi bankası (`Draft`/`Published`/`Retired`), görünürlük sınıfı + modül + depo kapsamı |
| AiAssistance | Okunabilir kaynak araması; kaynaklı yanıt (belge/sürüm/bölüm atıflı); "yeterli yetkili kaynak yok" sonucu; yanıtı açık onayla talebe "AI önerisi" notu olarak kaydetme; deterministik demo sağlayıcı ve isteğe bağlı yerel model adaptörü |
| Operations | Outbox + kiralamalı worker, süreç içi bildirim, denetim kaydı, süresi dolan kayıt temizliği, sınırlı ölçümler, sağlık uçları |

## 4. Kapsam dışı

Aşağıdakiler ilk sürümde **yoktur** ve varmış gibi sunulmaz:

- Muhasebe, çift taraflı defter (ledger), tahsilat, ödeme/checkout, banka entegrasyonu.
- Maaş/İK, üretim/MRP, CRM satış hunisi.
- e-Fatura / e-Arşiv; KVKK veya başka bir mevzuata uyum iddiası.
- Gerçek ödeme ve gerçek kargo entegrasyonu; taşıma/transit takibi, GPS.
- Satış iadesi / RMA.
- Lot/seri takibi, son kullanma tarihi, ondalıklı ölçü, paket/birim dönüşümü.
- Stok sayım oturumu, stok değerleme, satılan malın maliyeti (COGS).
- Mikroservisler, platform IAM entegrasyonu, SaaS aboneliği, gerçek bulut dağıtımı.
- Serbest dosya yükleme ve PDF ayrıştırma (bilgi bankası yalnız düz metin/Markdown).
- İş verisini değiştiren yapay zekâ ajanı, otomatik SQL, otomatik iade/düzeltme/talep kapatma.
- E-posta/SMS gönderimi.

Makinedeki diğer portföy projeleri (finans, e-ticaret, lojistik, kimlik) bu ürünün parçası değildir ve genişletilmez.

## 5. İşlevsel olmayan gereksinimler

| Konu | Gereksinim |
| --- | --- |
| Çalışma ortamı | Yalnız yerel: Docker Compose, `127.0.0.1` üzerinde HTTPS. Bulut, dağıtım, uzak depo yok |
| Bellek | Geliştirme makinesi 15,7 GB; oturum boyunca 0,5–1,8 GB boş bellek ölçüldü. Aynı anda tek Kantar SQL Server örneği; tarayıcı testleri tek worker; ağır doğrulama kapıları sırayla ve bellek ölçülerek çalışır |
| Veri | Tümü sentetik. Gerçek müşteri, şirket, personel, belge veya kimlik bilgisi yok |
| Ücretli hizmet | Yok. Ücretli API veya kullanıcı kredisi kullanılmaz; uzak model uç noktası varsayılan olarak kapalıdır |
| Yerel model | Otomatik model indirme/kurulum yok. Yapılandırılmış ve erişilebilir bir yerel model varsa kullanılır; yoksa canlı model doğrulaması "çalıştırılmadı" olarak raporlanır |
| Doğruluk | Stok değişmezleri veritabanı kısıtıdır; kritik komutlar tek transaction'dır; tekrarlanan istek aynı yanıtı verir |
| Güvenlik | Varsayılan olarak en az yetki: kısıtlı çalışma zamanı DB girişi, kapsam dışı kaynak `404`, gizli değerler depo dışında |
| Erişilebilirlik | Klavye ile kullanım, görünür odak, etiketli alanlar, hata özeti, yeterli karşıtlık, azaltılmış hareket desteği. Otomatik axe denetimi elle ekran okuyucu testinin yerine sayılmaz |
| Duyarlı düzen | 360 / 768 / 1440 px genişlikte kritik ekranlar içerik kaybı olmadan kullanılabilir |
| Dil | Arayüz Türkçe; tarih ve sayılar `tr-TR` biçiminde; kod ve tanımlayıcılar İngilizce |
| Yazar | TCoderr |

## 6. Demo verisi

### 6.1 Depolar

| Kod | Ad | Amaç |
| --- | --- | --- |
| `IST-01` | İstanbul Merkez | Ana senaryolar |
| `ANK-01` | Ankara Bölge | Transfer hedefi, ikinci depo kapsamı |
| `IZM-01` | İzmir Aktarma | Kapsam reddi senaryoları: yalnız bir depo görevlisinin yetkisi vardır |

### 6.2 Demo kullanıcıları

Görünen adlar bilerek rol tanımıdır; gerçek kişi adı kullanılmaz.

| Kullanıcı adı | Görünen ad | Roller | Depo yetkileri | PO onay limiti (TRY) |
| --- | --- | --- | --- | --- |
| `satis1` | Satış Temsilcisi 1 | `SalesAgent` | `IST-01` View | – |
| `satis2` | Satış Temsilcisi 2 | `SalesAgent` | `IST-01` View, `ANK-01` View | – |
| `satis.mudur` | Satış Yöneticisi | `SalesManager` | `IST-01` View, `ANK-01` View | – |
| `satinalma1` | Satın Alma Uzmanı | `PurchasingAgent` | `IST-01` View, `ANK-01` View | 0 |
| `satinalma.onay` | Satın Alma Onaycısı | `PurchasingAgent` | `IST-01` View, `ANK-01` View | 250.000 |
| `depo1` | Depo Görevlisi (İstanbul) | `WarehouseOperator` | `IST-01` Operate | – |
| `depo.sorumlu` | Depo Sorumlusu | `WarehouseOperator` | `IST-01` Supervise, `ANK-01` Operate | – |
| `depo.izmir` | Depo Görevlisi (İzmir) | `WarehouseOperator` | `IZM-01` Operate | – |
| `destek1` | Destek Uzmanı | `SupportAgent` | – | – |
| `destek.mudur` | Destek Yöneticisi | `SupportManager` | – | – |
| `admin` | Sistem Yöneticisi | `SystemAdmin` | – (örtük yetki yok) | – |

Bu tabloyla gösterilebilen senaryolar:

| Senaryo | Kullanıcılar |
| --- | --- |
| Sahiplik: aynı depoda başkasının taslak siparişi `404` | `satis1` ↔ `satis2` |
| Depo kapsamı: `ANK-01` siparişi `satis1` için `404` | `satis2` oluşturur, `satis1` dener |
| Onay limiti: `403 approval_limit_exceeded` | `satinalma1` (limit 0) → `satinalma.onay` |
| Limit üstü PO'yu demo kullanıcılarından hiçbiri onaylayamaz | toplamı 250.000 TRY üstü PO |
| Mal kabul ve sevkiyat yalnız `Operate` ile | `depo1` (`IST-01`), `ANK-01` için `404` |
| Transfer kaynak **ve** hedefte `Operate` ister | `depo.sorumlu` yapar; `depo1` yapamaz |
| Stok düzeltmesi yalnız `Supervise` | `depo.sorumlu` `IST-01`'de yapar, `ANK-01`'de yapamaz; `depo1` hiç yapamaz |
| Kimsenin görmediği depo | `IZM-01`: `depo.izmir` dışındaki herkes için `404` |
| Yönetici ticari içeriği okuyamaz | `admin` → sipariş/PO/stok uçları |
| Bilgi belgesi görünürlüğü | `SupportOnly` → `destek1`; `ManagerOnly` → `destek.mudur`, `satis.mudur`; depo kapsamlı belge → o depoda yetkisi olanlar |

Parolalar hiçbir belgede, kaynak dosyada veya çıktıda yazılmaz. `scripts/local-stack.ps1 init` her demo kullanıcısı için rastgele parola üretir ve git dışındaki `.local/seed/` altına yazar; seed bu dosyaları okur.

### 6.3 Açılış stoku

Açılış stoku doğrudan bakiyeye yazılmaz; seed, onaylı PO ve mal kabul belgeleri oluşturur (bkz. `04-database-design.md` §5). Seed sonunda mutabakat çalışır ve fark varsa seed hata ile biter.

Açılış belgelerinin (üç deponun açılış PO'su ve mal kabulü) sahibi, oturum açamayan (`IsActive = 0`) teknik kullanıcı `sistem.acilis`'tir. Bu kullanıcı yalnız seed süreci içinde çağıran kimliği olarak kullanılır; parolası kimsenin bilmediği rastgele bir değerin özetidir. Böylece `IZM-01` için hiçbir demo satın almacıya yetki vermek gerekmez ve "kimsenin görmediği depo" senaryosu korunur.

## 7. Başarı ölçütleri

- `09-test-and-verification-plan.md` içindeki değişmez tablosunun her satırı, belirtilen katmanda geçen bir teste karşılık gelir; kritik ERP kapılarında atlanan (skip) test yoktur.
- Dört tarayıcı senaryosu gerçek API ve gerçek veritabanıyla çalışır: PO onay → kısmi kabul → stok; satış taslak → onay → kısmi sevk → kalan iptal; rol/depo reddi ve oturum iptali; talep → yayınlı belge → kaynaklı yardım → öneriyi kaydet.
- Yerel yığın boş veritabanından kurulur, yeniden başlatmada veriyi korur, yardım sağlayıcısı kapalıyken ERP işlevleri çalışır.
- Gerçek yerel model doğrulanmadıysa bu açıkça "doğrulanmadı" olarak raporlanır; demo sağlayıcının sonucu onun yerine sayılmaz.
