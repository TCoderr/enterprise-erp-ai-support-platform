# Sıradaki Adım

Bu dosya her zaman tek bir "şimdi ne yapılacak" durumunu gösterir. Oturum kesilirse buradan ve `implementation-progress.md` dosyasından devam edilir; sıfırdan üretilmez.

## Şu an: son doğrulama tamamlandı; açık kalan iki kabul var (2026-10-12 01:10 TR)

`scripts/verify.ps1` kapılarının tamamı son kaynak parmak izinde koşuldu. Ayrıntı, çağrı kimlikleri ve başarısız ara koşular: `final-verification.md`.

Kaynak parmak izi: `sha256:1cd663dcc5ec6be55b9054bf746abfefb8835bc88d711c6b0725944f9220c6a0` (316 dosya).

| Kapı | Bu kaynak parmak izinde durum |
| --- | --- |
| `scripts`, `restore`, `build`, `unit`, `integration`, `web`, `migrations`, `e2e`, `smoke`, `scans` | **geçti** |
| `ai-live` | **koşulamadı (`not-run`)** — yerel model yok; canlı model doğrulanamadı |

Bu görevden kalan container, volume, ağ, `.local*` dizini veya süreç yok. Commit, push, PR ve dağıtım yapılmadı.

## Açık kalanlar (yeni iş başlatılmadı; hiçbiri kendiliğinden yapılmaz)

1. **Canlı AI kabulü.** Bu makinede zaten çalışan bir yerel OpenAI-uyumlu model sunucusu olduğunda:

   ```powershell
   $env:KANTAR_AI_LOCAL_BASE_URL = 'http://127.0.0.1:11434'   # yalnız loopback
   $env:KANTAR_AI_LOCAL_MODEL    = '<o sunucuda hazır olan model adı>'
   ./scripts/verify.ps1 -Gate build,ai-live
   ```

   Kantar model indirmez, kurmaz, başlatmaz; ücretli veya uzak servis çağırmaz. Kapı koşulana kadar "canlı AI doğrulanmadı" ifadesi geçerlidir.
2. **Uzak CI.** Depo hiç push edilmedi; `.github/workflows/ci.yml` yalnız yazıldı. Push kullanıcının kararıdır.
3. **Küçük, bilinen kalemler** (`final-verification.md` §9): seed sırasında iki EF Core uyarısı; art arda üç deadlock ile vazgeçme sınanmadı; ekran okuyucu ve Chrome dışı tarayıcı testi yok; ekran görüntülerini bir insan incelemedi.

## Kaynak değişirse

Kaynakta (`docs/`, `artifacts/`, `design/` ve kök README/NOTICE/PRODUCT/DESIGN dışında) herhangi bir dosya değişirse parmak izi değişir ve kapıların yeni parmak izinde koşulması gerekir. Her kapı ayrı çağrıyla koşulur; sıra:

```powershell
cd "D:\Enterprise ERP & AI Support Platform\enterprise-erp-ai-support-platform"
./scripts/verify.ps1 -List                 # bu kaynakta hangi kapı geçti
./scripts/verify.ps1 -Gate scripts,restore,build,unit
./scripts/verify.ps1 -Gate integration     # ~2 dk; tek SQL Server container'ı (~0,9 GB)
./scripts/verify.ps1 -Gate web
./scripts/verify.ps1 -Gate migrations
./scripts/verify.ps1 -Gate e2e             # kendi yığını (kantar-e2e, 8963) + tek tarayıcı
./scripts/verify.ps1 -Gate smoke           # kendi yığını (kantar-smoke, 8953)
./scripts/verify.ps1 -Gate scans
```

## Bilinen engeller

- **Bellek**: 15,7 GB makinede kullanıcının tarayıcısı ve komşu container'larla birlikte boş bellek bu oturumda 0,6–3,7 GB arasında oynadı. Kapılar tek tek koşulmalı; betik eşik altında bekler, düzelmezse çıkış kodu 3 ile durur ve başka süreçlere dokunmaz.
- **Yerel model yok**: `ai-live` çalıştırılamaz.
- **Uzak CI**: çalıştırılmadı.
