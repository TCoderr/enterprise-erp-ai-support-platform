# 08 — Uygulama Planı

Aşamalar sırayla uygulanır. Bir aşamanın kabul kapıları geçmeden sonraki aşamanın koduna başlanmaz; kapı geçince rutin onay beklenmez. Her aşama sonunda `docs/implementation-progress.md` (kararlar, dosyalar, komut, çıkış kodu, test sayıları, atlanan test, kaynak parmak izi) ve `docs/next-action.md` güncellenir; oturum kesilirse iş bu iki dosyadan sürdürülür.

## 0. Ortak kurallar

| Kural | Uygulama |
| --- | --- |
| Tek SQL Server örneği | Bu makinede aynı anda yalnız bir Kantar SQL Server container'ı çalışır (geliştirme yığını **veya** test container'ı **veya** duman/E2E yığını). Ağır kapıdan önce kendi sahip olduğumuz diğer yığın indirilir |
| Sıralı kapılar | Build, veritabanı testleri, tarayıcı ve duman testi aynı anda başlatılmaz |
| Tarayıcı | Playwright `workers: 1`, `fullyParallel: false` |
| Bellek ölçümü | `scripts/verify.ps1` her ağır kapıdan önce boş belleği ölçer; eşiğin altındaysa kapıyı başlatmaz ve kontrol noktası yazıp 3 koduyla çıkar |
| Komşu kaynaklar | Diğer projelerin container, volume, port ve süreçlerine dokunulmaz; bellek açmak için durdurulmazlar |
| Tekrar | Kaynak değişmediyse geçmiş kapı yeniden koşulmaz; aynı parmak izinde ayrı çağrılarla geçen kapılar ayrı çağrı olarak raporlanır |
| Dış etki | Commit, push, PR, dağıtım, ücretli hizmet yok |
| Portlar | Geliştirme `8943`, duman `8953`, E2E `8963`, Vite `5193`; Compose projeleri `kantar`, `kantar-smoke`, `kantar-e2e` |

Depo düzeni:

```
Kantar.slnx  global.json  Directory.Build.props  Directory.Packages.props  Dockerfile  compose.yaml
src/   Kantar.Domain  Kantar.Application  Kantar.Infrastructure  Kantar.Api  web/
tests/ Kantar.Testing  Kantar.UnitTests  Kantar.IntegrationTests
db/    grants.sql  (rol ve izin betikleri)
scripts/  local-stack.ps1|.sh  verify.ps1  source-fingerprint.ps1  tests/
docs/  design/  artifacts/verification/  .github/workflows/
```

Modüller her projede klasördür: `Identity`, `MasterData`, `Inventory`, `Sales`, `Purchasing`, `Support`, `Ai`, `Operations`.

## Aşama 1 — Temel, kimlik, ana veri

**Teslim edilenler**

- Çözüm iskeleti, SDK sabitleme, merkezi paket sürümleri, kilit dosyaları, `Authors = TCoderr`.
- `Kantar.Domain`: roller, depo yetki seviyeleri, yetki politikası (saf fonksiyonlar), hata kodları.
- `Kantar.Infrastructure/Persistence`: `KantarDbContext`, `iam` + `md` + `ops` (denetim, idempotency) şemaları, ilk migration, `db/grants.sql`.
- `Kantar.Api`: istek hattı (korelasyon, güvenlik başlıkları, oturum, CSRF, hız sınırı, katı JSON bağlama, ProblemDetails), `auth`, `admin`, ana veri uçları, mod seçimi (`--migrate`, `--seed`).
- `Kantar.Application`: `CommandPipeline`, `IUnitOfWork` (yeniden denemeli), idempotency deposu, istek kanonikleştirme.
- `tests/Kantar.Testing`: test veritabanı sahiplik koruması; entegrasyon fixture'ı (tek SQL Server container'ı, migrator ile kurulum, uygulama `kantar_app` ile).

**Kabul:** rol + depo IDOR testleri (`404`/`403`), CSRF akışı, güncel izin okuması, oturum iptali, günlükte gizli veri olmaması, gerçek kısıtlı DB girişiyle izin testleri; Release build, birim ve entegrasyon testleri atlamasız.

## Aşama 2 — Docker, yerel yığın, CI, test belleği

**Teslim edilenler**

- `Dockerfile` (web derlemesi + .NET yayın; kök olmayan kullanıcı), `compose.yaml` (`sqlserver`, `migrator`, `api`, `worker`, `seed` profili; etiketli volume'lar; host'a yalnız API portu).
- `scripts/local-stack.ps1` ve `.sh`: `init`, `up`, `down`, `status`, `logs`, korumalı `reset`. `down` volume silmez.
- Seed: ana veri, demo kullanıcıları, açılış stoku (PO + mal kabul yoluyla); tekrar çalıştırılabilir.
- `scripts/verify.ps1` iskeleti: adlı kapılar, bellek eşiği, kontrol noktası, parmak izi; `scripts/tests` altında betik testleri.
- `.github/workflows/ci.yml` (yerelde çalıştırılmaz; uzak CI çalışmadığı açıkça yazılır).

**Kabul:** boş kurulum `init` + `up`; yeniden başlatmada veri kalıcılığı; veritabanı indir/kaldır sonrası `ready`; seed tekrarı güvenli ve günlük toplamları bakiyeyle eşit; gizli değerler depo dışında ve süreç kök değil; yanlış hedefe sıfır yazım (sahiplik koruması testi); port yalıtımı.

## Aşama 3 — Stok günlüğü, düzeltme, transfer

**Teslim edilenler:** `inv` şeması ve kısıtlar; `IStockLedger` + SQL ilkel işlemleri (`Infrastructure/Inventory`); düzeltme ve transfer komutları; bakiye/hareket/rezervasyon sorguları; düşük stok listesi; salt okunur mutabakat sorguları.

**Kabul:** negatif ve rezerve sınırı koruması; eşzamanlı değişikliklerde değişmezlerin korunması; transferde kaynak–hedef geri alma; aynı iş referansının ikinci etki üretmemesi; gerçek `kantar_app` girişiyle günlükte `UPDATE`/`DELETE`/`TRUNCATE` reddi.

## Aşama 4 — Satın alma, onay, kısmi mal kabul

**Teslim edilenler:** `purch` şeması; PO taslak/düzenleme/onay/iptal; onay limiti; snapshot'lar; çok satırlı mal kabul; (`SupplierId`, `ExternalReference`) benzersizliği.

**Kabul:** fazla kabul ve yetkisiz depo reddi; paralel kabulde yalnız kalan miktar; farklı anahtarlı tekrar denemede iş referansı koruması; kısmi hatada stok/günlük etkisi olmaması; PO iptal–kabul yarışı.

## Aşama 5 — Satış onayı ve rezervasyon

**Teslim edilenler:** `sales` şeması; taslak/düzenleme; sunucu fiyatı ve satır snapshot'ı; confirm (hepsi-ya-da-hiçbiri rezervasyon); rezervasyon defteri.

**Kabul:** kapasite sınırında ≥ 20 eşzamanlı confirm ile fazla satış olmaması; çok satırlı geri alma; tekrar oynatmanın bayt düzeyinde aynı olması ve rol yeniden denetimi; aynı siparişe farklı anahtarla ikinci confirm'in tek etki bırakması; sürüm (`If-Match`) korumaları.

## Aşama 6 — Kısmi sevkiyat, iptal, belge işlemleri

**Teslim edilenler:** sevkiyat komutu; kalan iptal; yazdırmaya uygun özet ("fatura veya irsaliye değildir" uyarısıyla); stok geçmişi filtreleri ve imleçli sayfalama.

**Kabul:** sevkiyat–iptal yarışının iki sırası; farklı anahtarla aynı `DispatchReference`; miktar kaybı veya çift stok etkisi olmaması; tamamlanan belgelerin ve snapshot'ların değişmezliği; filtre/imleç kapsamı.

## Aşama 7 — Destek, bilgi bankası, yardım orkestrasyonu

**Teslim edilenler:** `sup` + `ai` şemaları; talep, yorum, iç not, atama, durum; tanılama snapshot'ı; bilgi belgesi taslak/sürüm/yayın/emekli; parçalama; yetkili getirme sorgusu ve sözcüksel puanlama; `IAnswerGenerator`, demo sağlayıcı, yerel model adaptörü; atıf doğrulama, yeniden yetkilendirme; `from-assist` komutu.

**Kabul:** talep–belge yetki sınırı; yetkisiz kanarya metninin sağlayıcı girdisinde olmaması; üretim ortasında erişim iptali; uydurma atıf ve güvensiz çıktı işlenmesi; deterministik sahte sağlayıcı paketi. Gerçek yerel model kapısının durumu **ayrıca** ve olduğu gibi raporlanır.

## Aşama 8 — Arayüz

**Teslim edilenler:** `design/preview.html` prototipi; `src/web` (satış, satın alma, stok, sevkiyat/kabul, destek, bilgi bankası, yönetim ekranları; kaynaklı yardım paneli); yerel font dosyaları ve lisansları; bileşen testleri; Playwright senaryoları + axe.

**Kabul:** tip denetimi, lint, üretim derlemesi, bileşen testleri; gerçek veriyle durum ve yetki ekranları; kritik ekranların 360/768/1440 genişlikte elle incelenmesi; klavye ve axe; sahte başarı iletisi olmaması; yardım panelinde sağlayıcı/kaynak/sürüm etiketi.

## Aşama 9 — Worker, operasyon, kurtarma, raporlama

**Teslim edilenler:** outbox dağıtımı (kiralama + çit belirteci), tüketici tekrar etkisizliği, bildirimler, temizlik işleri, zamanlı mutabakat, operasyon uçları, sınırlı ölçümler, zarif kapanış; güvenlik taramaları (gizli değer, bağımlılık, lisans).

**Kabul:** transaction geri alma / çökme / yeniden deneme etkileri; günlük sapmasının salt okunur raporlanması ve düzeltmeyle çözümü; yardım kesintisinde ERP'nin bağımsız çalışması; en az yetki ve etiketlerde kimlik olmaması; zarif kapanış.

## Aşama 10 — Son doğrulama, belgeler

**Teslim edilenler:** `README.md`, `docs/inventory-correctness-verification.md`, `ai-verification.md`, `ui-review.md`, `final-verification.md`, `known-limitations.md`, `portfolio-summary.md`, `THIRD-PARTY-NOTICES.md`; `artifacts/verification/` altında maskeli günlükler ve ekran görüntüleri.

**Kabul:** `09-test-and-verification-plan.md` §6'daki son kapılar aynı kaynak parmak izinde; kritik ERP kapılarında atlama yok. Gerçek model çalıştırılamadıysa bu "kısmi yetenek" olarak açıkça yazılır; sonuç "tüm kapılar geçti" diye özetlenmez.

## Bağımlılık sırası

```
1 Temel ──▶ 2 Yığın ──▶ 3 Stok ──▶ 4 Satın alma ──▶ 5 Satış onayı ──▶ 6 Sevkiyat/iptal
                              └────────────────────────────────────────────┐
                        7 Destek/KB/Yardım (1'e bağlı, 3–6'dan bağımsız) ──┼──▶ 8 Arayüz ──▶ 9 Operasyon ──▶ 10 Son
```

Aşama 4, Aşama 5'ten öncedir çünkü stok girişi yalnız mal kabulle olur; satış testleri stoklarını mal kabul komutuyla hazırlar.
