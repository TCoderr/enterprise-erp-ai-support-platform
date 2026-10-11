# Kantar

Küçük ve orta ölçekli bir toptan ticaret işletmesi için **sipariş, satın alma, stok ve uygulama destek merkezi**. Yerelde uçtan uca çalışan, sınırları bilerek dar tutulmuş bir ürün dilimi: satın alma siparişi → mal kabul → stok → satış rezervasyonu → kısmi sevkiyat, ve yetkiye duyarlı bilgi bankası üzerinde kaynaklı yardım.

Yazar: **TCoderr**

> Bu depo bir geliştirme ve portföy çalışmasıdır. Üretim dağıtımı tanımlamaz; e-fatura, muhasebe, ödeme, kargo veya KVKK uyumluluğu iddiası yoktur. Tüm veriler sentetiktir. Doğrulanan ve doğrulanmayan her şey [`docs/final-verification.md`](docs/final-verification.md) ve [`docs/known-limitations.md`](docs/known-limitations.md) içinde ayrı ayrı yazılıdır.

## Ne yapar

| Alan | Kapsam |
| --- | --- |
| Satış | Taslak → Onaylandı → Kısmen sevk edildi → Sevk edildi; iptal ve kalan miktarı iptal. Onayda bütün satırlar için ya hep ya hiç stok rezervasyonu; fiyat ve müşteri snapshot'ı sunucudan |
| Satın alma | Taslak → Onaylandı → Kısmen kabul edildi → Kabul edildi; onay limiti; tedarikçi irsaliye numarası benzersiz mal kabul |
| Stok | Eldeki / Rezerve / Kullanılabilir; değiştirilemez hareket günlüğü ve ayrı rezervasyon defteri; depolar arası tek transaction'lı transfer; gerekçeli düzeltme; salt okunur mutabakat |
| Destek merkezi | Destek talepleri, iç notlar, beyaz listeli tanılama özeti; sürümlü ve görünürlük sınıflı bilgi bankası |
| Kaynaklı yardım | Soruyu yalnız kullanıcının okuyabildiği yayınlı belgelerden yanıtlar, belge/sürüm/bölüm atfı gösterir. ERP verisini değiştirmez; araç, SQL veya otomatik işlem yoktur |

Kapsam dışı: muhasebe/defter, bordro, üretim, CRM, e-fatura/e-arşiv, gerçek ödeme ve kargo, satış iadesi, lot/seri, çok kiracılılık, bulut dağıtımı.

## Teknoloji

- .NET 10 (SDK 10.0.401), ASP.NET Core Minimal API, modüler monolit (`Domain ← Application ← Infrastructure ← Api`)
- **SQL Server 2022 Developer** (`mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04`), EF Core 10. Developer sürümü yalnız geliştirme ve test için lisanslıdır. Neden PostgreSQL değil ve ölçülen bellek maliyeti: [`docs/adr/0001-database-provider.md`](docs/adr/0001-database-provider.md)
- React 19 + TypeScript + Vite; aynı origin, sunucu taraflı oturum + çerez + CSRF
- Testler: xUnit v3 (birim + gerçek SQL Server üzerinde entegrasyon), Vitest (bileşen), Playwright (tarayıcı)
- Yanıt üretimi: deterministik **demo sağlayıcı** (dil modeli değildir) veya zaten çalışan **yerel** bir OpenAI-uyumlu model sunucusu. Ücretli/uzak servis çağrısı yoktur

## Önkoşullar

- Windows 10/11 (PowerShell 5.1+) veya Linux/macOS (bash)
- Docker Desktop / Docker Engine (Compose v2)
- .NET SDK 10.0.401 ve bir .NET HTTPS geliştirme sertifikası (`dotnet dev-certs https` — yoksa bir kez oluşturun)
- Node.js ≥ 22.12 (yalnız arayüzü Docker dışında derlemek veya test etmek için)

**Bellek**: SQL Server container'ı boşta yaklaşık 0,9 GB kullanır (resmî asgari gereksinim 2 GB RAM). `local-stack up` boş bellek 1200 MB'ın altındaysa başlamaz; başka süreçleri veya container'ları kapatmaz. Aynı anda yalnız bir Kantar yığını çalıştırın.

## Yerelde çalıştırma

Bütün komutlar **depo kök dizininden** çalıştırılır:

```powershell
cd "D:\Enterprise ERP & AI Support Platform\enterprise-erp-ai-support-platform"

./scripts/local-stack.ps1 init      # gizli dosyaları üretir (.local/, git dışı); tekrar çalıştırılabilir
./scripts/local-stack.ps1 up        # imajı derler, yığını başlatır, sağlıklı olana kadar bekler
./scripts/local-stack.ps1 seed      # demo verisi (tekrar çalıştırılabilir)
./scripts/local-stack.ps1 status
```

Linux/macOS: `./scripts/local-stack.sh init | up | seed | status`.

Adres: **https://localhost:8943** (kendinden imzalı geliştirme sertifikası; tarayıcı uyarı gösterir).

| Komut | Etki |
| --- | --- |
| `down` | Container'ları kaldırır, **veritabanı kalır** |
| `logs` | Son günlükler |
| `reset -ConfirmReset kantar` | **Yıkıcı**: container'lar + veritabanı + `.local/`. Yalnız `dev.kantar.stack=true` etiketli volume'lara dokunur |

Yalnız API portu yayınlanır ve yalnız `127.0.0.1` üzerinde. SQL Server'ın host portu yoktur.

### Demo hesapları

Parolalar hiçbir yerde yazılı değildir. `init`, her kullanıcı için rastgele bir parola üretir ve şu dosyaya yazar: `.local/seed/Seed__Passwords__<kullanıcı adı>`

| Kullanıcı | Rol | Depo yetkisi | Not |
| --- | --- | --- | --- |
| `satis1` | SalesAgent | IST-01 Görüntüle | |
| `satis2` | SalesAgent | IST-01, ANK-01 Görüntüle | Sahiplik senaryoları |
| `satis.mudur` | SalesManager | IST-01, ANK-01 Görüntüle | |
| `satinalma1` | PurchasingAgent | IST-01, ANK-01 Görüntüle | Onay limiti 0: onaylayamaz |
| `satinalma.onay` | PurchasingAgent | IST-01, ANK-01 Görüntüle | Onay limiti 250.000 TRY |
| `depo1` | WarehouseOperator | IST-01 İşlem | |
| `depo.sorumlu` | WarehouseOperator | IST-01 Sorumlu, ANK-01 İşlem | Düzeltme (IST-01), transfer |
| `depo.izmir` | WarehouseOperator | IZM-01 İşlem | Başkasının görmediği depo |
| `destek1` | SupportAgent | – | |
| `destek.mudur` | SupportManager | – | Bilgi belgesi yayınlar |
| `admin` | SystemAdmin | – | Ticari belgeleri ve stoku **göremez** |

Ayrıntılı yetki matrisi: [`docs/02-domain-and-state-machines.md`](docs/02-domain-and-state-machines.md) §2.4.

### Kaynaklı yardım sağlayıcısı

Varsayılan `Demo`: kaynak cümlelerini seçip atıf veren kural tabanlı bir sağlayıcıdır, **dil modeli değildir** ve arayüzde böyle etiketlenir. Bu makinede zaten çalışan bir yerel model sunucunuz varsa (ör. Ollama, LM Studio) `.env.example` içindeki `KANTAR_AI_*` değişkenleriyle bağlayabilirsiniz. Kantar model indirmez, kurmaz, başlatmaz; loopback veya açıkça izin verilen özel ağ adresi dışındaki uçlara bağlanmayı reddeder.

## Doğrulama

```powershell
./scripts/verify.ps1 -List                      # kapılar, bellek eşikleri, bu kaynak için kayıtlı sonuçlar
./scripts/verify.ps1 -Gate scripts,restore,build,unit
./scripts/verify.ps1 -Gate integration          # gerçek SQL Server (Testcontainers), kısıtlı kantar_app girişi
./scripts/verify.ps1 -Gate web
./scripts/verify.ps1 -Gate migrations
./scripts/verify.ps1 -Gate e2e                  # kendi yığını: kantar-e2e, port 8963, tek tarayıcı işçisi
./scripts/verify.ps1 -Gate smoke                # kendi yığını: kantar-smoke, port 8953
./scripts/verify.ps1 -Gate scans
./scripts/verify.ps1 -Gate ai-live              # yalnız yerel model yapılandırılmışsa; yoksa "NOT RUN"
```

- Her kapıdan önce boş bellek ölçülür. Eşik altındaysa betik bekler, düzelmezse **çıkış kodu 3** ile durur; aynı komut sonra yeniden çalıştırılır.
- Sonuçlar kaynak parmak izine (`scripts/source-fingerprint.ps1`) bağlı olarak `artifacts/verification/checkpoints.json` içinde saklanır. Aynı kaynakta geçmiş bir kapı yeniden koşulmaz ve özetinde "earlier invocation" olarak, hangi çağrıda koşulduğuyla birlikte gösterilir. Ayrı çağrıların sonuçları tek koşu gibi sunulmaz.
- Çıkış kodları: `0` istenen kapılar geçti, `1` bir kapı başarısız veya kaynak koşu sırasında değişti, `3` bellek nedeniyle duruldu.

Betiklerin kendi testleri: `powershell -NoProfile -File scripts/tests/run.ps1`.

### Son doğrulamanın durumu

Son koşu 2026-10-12, kaynak parmak izi `sha256:1cd663dc…c6a0`. Kapılar ayrı çağrılarla ve sırayla koşuldu; çağrı kimlikleri, ara kaynak durumları ve başarısız ara koşular [`docs/final-verification.md`](docs/final-verification.md) içindedir.

| Kapı | Sonuç |
| --- | --- |
| `scripts` | 42 / 42 |
| `restore`, `build` (Release, uyarılar hata) | geçti |
| `unit` | 2036 / 2036, 0 atlanan |
| `integration` (gerçek SQL Server, kısıtlı giriş) | 330 / 330, 0 atlanan |
| `web` (typecheck, lint, build, bileşen testleri) | geçti; 110 / 110 |
| `migrations` | model eşit; gerçek uygulama 20 / 20 |
| `e2e` (Chrome, tek işçi, güncel kaynaktan derlenen imaj) | 29 / 29, 0 atlanan, 0 kararsız |
| `smoke` | 30 / 30 denetim |
| `scans` | bulgu yok (güvenlik denetimi değildir) |
| `ai-live` | **koşulamadı** — bu makinede yerel model yok |

| Aşama | Durum |
| --- | --- |
| Tasarım belgeleri ve kararlar | Tamam |
| Kimlik, ana veri, stok, satın alma, satış, sevk/iptal, worker/mutabakat | Tamam; kapılarla doğrulandı |
| Docker/Compose yığını ve betikler | Tamam; duman kapısıyla doğrulandı |
| Arayüz | Tamam; bileşen ve tarayıcı testleriyle doğrulandı. Ekran okuyucu ve Chrome dışı tarayıcı sınanmadı |
| Destek merkezi ve kaynaklı yardım | Tamam (demo sağlayıcıyla). **Canlı dil modeliyle doğrulanmadı** |
| Uzak CI | Çalıştırılmadı (depo push edilmedi) |

ERP kabulü ile canlı AI kabulü ayrıdır: ilki yukarıdaki kapılara dayanır; ikincisi **verilmemiştir**. Demo sağlayıcıyla geçen testler bir dil modelinin kalitesi hakkında bir şey söylemez.

## Belgeler

| Belge | İçerik |
| --- | --- |
| [`docs/01-product-requirements.md`](docs/01-product-requirements.md) | Kapsam, roller, demo verisi |
| [`docs/02-domain-and-state-machines.md`](docs/02-domain-and-state-machines.md) | İş kuralları, durum makineleri, yetki matrisi |
| [`docs/03-target-architecture.md`](docs/03-target-architecture.md), [`docs/adr/`](docs/adr/) | Mimari ve kararlar |
| [`docs/04-database-design.md`](docs/04-database-design.md) | Şema, kilitleme, roller, mutabakat |
| [`docs/05-api-contracts.md`](docs/05-api-contracts.md) | API sözleşmesi |
| [`docs/06-security-and-failure-behavior.md`](docs/06-security-and-failure-behavior.md) | Güvenlik ve hata davranışı |
| [`docs/10-ai-grounding-and-access-design.md`](docs/10-ai-grounding-and-access-design.md) | Kaynaklı yardımın sınırları |
| [`docs/inventory-correctness-verification.md`](docs/inventory-correctness-verification.md) | Stok değişmezleri ↔ testler |
| [`docs/ai-verification.md`](docs/ai-verification.md) | AI: sahte/gerçek sağlayıcı durumu, senaryolar |
| [`docs/ui-review.md`](docs/ui-review.md) | Tasarım kaynakları, inceleme bulguları |
| [`docs/final-verification.md`](docs/final-verification.md) | Çalıştırılan komutlar, sayılar, parmak izleri |
| [`docs/known-limitations.md`](docs/known-limitations.md) | Bilinen sınırlar |
| [`docs/portfolio-summary.md`](docs/portfolio-summary.md) | Kısa özet ve yeniden üretilebilir tur |
| [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) | Üçüncü taraf bileşenler ve lisanslar |

## Lisans ve atıf

Kaynak kodun yazarı TCoderr'dır. Üçüncü taraf bileşenler kendi lisanslarıyla kullanılır; ayrıntı `THIRD-PARTY-NOTICES.md` dosyasındadır.
