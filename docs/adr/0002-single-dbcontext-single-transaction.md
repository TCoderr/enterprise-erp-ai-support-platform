# ADR-0002 — Tek DbContext, komut başına tek transaction

Durum: Kabul edildi · Tarih: 2026-10-11

## Bağlam

Bir iş komutu birden çok modülün verisine dokunur. Örnek: satış onayı sipariş durumunu (Sales), stok bakiyesini ve rezervasyon defterini (Inventory), idempotency kaydını, denetim satırını ve outbox mesajını (Operations) birlikte değiştirir. Bunlardan biri yazılıp diğeri yazılmazsa stok ile belge birbirinden kopar ve bunu sonradan güvenle düzeltmenin yolu yoktur.

Kantar modüler bir monolittir: tek süreç ailesi, tek veritabanı. Modüllerin ayrı dağıtılması veya ayrı ölçeklenmesi gereksinim değildir.

## Karar

- Tek veritabanı, tek `KantarDbContext`.
- Her komut tek bir veritabanı transaction'ıdır: belge durumu, satır miktarları, bakiye, günlükler, idempotency, denetim ve outbox birlikte commit edilir veya birlikte geri alınır.
- Transaction sınırı `IUnitOfWork.ExecuteAsync` içindedir; yeniden deneme de oradadır. İşleyiciler transaction açıp kapatmaz.
- Modüller şema (`iam`, `md`, `inv`, `sales`, `purch`, `sup`, `ai`, `ops`) ve klasör/ad alanı düzeyinde ayrılır. Modüller arası yazma, Application katmanındaki açık arayüzlerle yapılır (ör. Sales stoka yalnız `IStockLedger` üzerinden dokunur).
- Dış çağrı (yardım sağlayıcısı) hiçbir zaman transaction içinde yapılmaz.

## Değerlendirilen alternatifler

| Alternatif | Neden seçilmedi |
| --- | --- |
| Modül başına DbContext, paylaşılan bağlantı/transaction | Aynı tutarlılığı verir ama bağlantı ve transaction paylaşımı için ek tesisat ve iki kat migration yönetimi ister; bu ölçekte kazancı yok |
| Modül başına veritabanı + süreç içi olaylarla nihai tutarlılık | Rezervasyon ile sipariş durumunun arasına gözlemlenebilir tutarsız an girer; telafi (compensation) mantığı gerekir |
| Dağıtık transaction / saga | Tek veritabanı varken gereksiz karmaşıklık |
| Genel repository katmanı | EF Core zaten birim iş ve depo soyutlaması sağlar; ek katman sorguları gizler |

## Sonuçlar / maliyet

- Modül sınırı derleyici tarafından değil, kural ve testle korunur (mimari yön testi; stok tablolarına yalnız Inventory altyapısının yazması).
- Tek `DbContext` modeli büyür; migration'lar tek kümedir.
- Bir modülün şeması değişince tüm uygulama yeniden dağıtılır.
- Uzun transaction riski: komutlar kısa tutulur, satır sayısı sınırlıdır (belge başına en fazla 50 satır).

## Değişme koşulu

Bir modülün ayrı ölçeklenmesi veya ayrı dağıtılması gerekirse (ör. yardım/bilgi bankası ayrı servis olursa) o modül kendi veritabanına taşınır ve aradaki tutarlılık outbox üzerinden kurulur. Stok, satış ve satın almanın aynı transaction'da kalması bu durumda da korunur.

## Kaynaklar

Bu karar için bu oturumda dış kaynak doğrulanmadı; gerekçe ürünün kendi tutarlılık gereksinimine dayanır (`02-domain-and-state-machines.md` §4, §8).
