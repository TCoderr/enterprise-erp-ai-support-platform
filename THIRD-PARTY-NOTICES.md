# Üçüncü Taraf Bileşenler ve Atıflar

Kantar'ın kendi kaynak kodunun yazarı TCoderr'dır. Aşağıdaki bileşenler kendi sahiplerine aittir ve kendi lisanslarıyla kullanılır. Bu dosya bir hukuki görüş değildir; lisans bilgileri paketlerin kendi bildirdiği meta veriden alınmıştır.

Tam, sürümlü envanter her `scans` kapısı koşusunda yeniden üretilir:

- `artifacts/verification/licenses-nuget.txt` — kilit dosyalarındaki tüm NuGet paketleri (geçişli dahil)
- `artifacts/verification/licenses-npm-runtime.txt` — çalışma zamanı npm paketleri

Envanterin son üretimindeki dağılım (2026-10-11): 110 NuGet paketi — 99 MIT, 9 Apache-2.0, 2 pakette lisans dosya olarak bildirilmiş; 11 çalışma zamanı npm paketi — 9 MIT, 2 OFL-1.1.

## 1. Uygulamayla birlikte dağıtılanlar

### Yazı tipleri (arayüzle birlikte sunulur)

| Bileşen | Paket | Lisans | Lisans metni |
| --- | --- | --- | --- |
| Sofia Sans (değişken) | `@fontsource-variable/sofia-sans` 5.3.0 | SIL Open Font License 1.1 | `src/web/public/licenses/Sofia-Sans-OFL-1.1.txt` (uygulamada `/licenses/` altında sunulur) |
| Sofia Sans Condensed (değişken) | `@fontsource-variable/sofia-sans-condensed` 5.3.0 | SIL Open Font License 1.1 | `src/web/public/licenses/Sofia-Sans-Condensed-OFL-1.1.txt` |

Yazı tipi dosyaları değiştirilmeden, paketin sağladığı `latin` ve `latin-ext` alt kümeleriyle kullanılır. Telif ve "Reserved Font Name" bildirimleri lisans dosyalarının içindedir.

### Arayüz çalışma zamanı (npm)

| Paket | Sürüm | Lisans |
| --- | --- | --- |
| react, react-dom, scheduler | 19.3.0 / 19.3.0 / 0.28.0 | MIT |
| react-router, react-router-dom | 7.18.4 | MIT |
| @tanstack/react-query, @tanstack/query-core | 5.104.1 | MIT |
| cookie, set-cookie-parser (react-router bağımlılıkları) | 1.1.1 / 2.7.2 | MIT |

### Sunucu çalışma zamanı (NuGet, doğrudan başvurulanlar)

| Paket | Sürüm | Lisans |
| --- | --- | --- |
| Microsoft.EntityFrameworkCore, .Relational, .SqlServer | 10.0.12 | MIT |
| Microsoft.AspNetCore.OpenApi | 10.0.12 | MIT |
| Microsoft.Data.SqlClient (EF Core SQL Server sağlayıcısının bağımlılığı) | kilit dosyasındaki sürüm | MIT |
| Microsoft.Data.SqlClient.SNI.runtime | 6.0.2 | Paketle gelen `LICENSE.txt` (Microsoft yazılım lisansı) |
| Microsoft.Identity.Client.NativeInterop (SqlClient'ın geçişli bağımlılığı) | 0.20.6 | Paketle gelen `LICENSE` |

.NET çalışma zamanı ve ASP.NET Core paylaşılan çerçevesi (MIT) temel imajla gelir.

### Container imajları

| İmaj | Kullanım | Lisans / koşul |
| --- | --- | --- |
| `mcr.microsoft.com/dotnet/aspnet:10.0.12`, `mcr.microsoft.com/dotnet/sdk:10.0.401` | Çalışma zamanı ve derleme | MIT (.NET); imajdaki işletim sistemi paketleri kendi lisanslarıyla |
| `node:24.18.0-bookworm-slim` | Yalnız arayüz derlemesi (son imajda yer almaz) | Node.js lisansı (MIT ve bileşen lisansları) |
| `mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04` | Veritabanı | **SQL Server Developer sürümü**: Microsoft lisans koşulları; yalnız geliştirme ve test için lisanslıdır, üretimde kullanılamaz. `ACCEPT_EULA=Y` ile son kullanıcı lisans sözleşmesi kabul edilir |

## 2. Yalnız geliştirme ve testte kullanılanlar (dağıtılmaz)

| Bileşen | Lisans |
| --- | --- |
| xunit.v3 ve bileşenleri 4.0.2, xunit.analyzers | Apache-2.0 |
| Testcontainers, Testcontainers.MsSql 4.16.0 | MIT |
| Microsoft.AspNetCore.Mvc.Testing 10.0.12, Microsoft.Extensions.TimeProvider.Testing 10.10.0, Microsoft.EntityFrameworkCore.Design 10.0.12, dotnet-ef 10.0.12 | MIT |
| Vite 8.3.4, @vitejs/plugin-react, TypeScript 6.0.3, Vitest 5.0.3, jsdom, Testing Library paketleri, ESLint ve eklentileri | MIT / Apache-2.0 (TypeScript: Apache-2.0) |
| Playwright (`@playwright/test` 1.64.0) | Apache-2.0 |
| axe-core (`@axe-core/playwright` 4.13.0) | MPL-2.0 |

## 3. Tasarım rehberleri (kod olarak dağıtılmaz)

| Kaynak | Sürüm / başvuru | Lisans | Nasıl kullanıldı |
| --- | --- | --- | --- |
| **Impeccable** — Paul Bakaus, <https://github.com/pbakaus/impeccable> | `skill-v4.5.2`, commit `d631a8827f99414d2b6daba4ef08b7f8701751d7` | Apache-2.0 | Beceri dosyaları değiştirilmeden, proje kapsamında `.claude/skills/impeccable/` altına kopyalandı; `LICENSE` ve `NOTICE.md` dosyaları aynı dizinde korunur. Başlatıcı ikili dosyası ve kancalar **kurulmadı**. Rehber tasarım kararlarında ve inceleme geçişlerinde kullanıldı; ürünün parçası olarak dağıtılmaz |
| Impeccable'ın `NOTICE.md` dosyasında anılan **platform-design-skills** (ehmo) | – | MIT | Impeccable içindeki `ios.md`/`android.md` başvuru dosyalarının kaynağı; Kantar'da bu dosyalar kullanılmadı, atıf Impeccable'ın bildirimi gereği korunur |
| **Vercel Web Interface Guidelines** — <https://github.com/vercel-labs/web-interface-guidelines> (`command.md`), beceri sarmalayıcısı <https://github.com/vercel-labs/agent-skills/tree/main/skills/web-design-guidelines> | 2026-10-11 tarihinde okunan ana dal | `vercel-labs/agent-skills` deposu GitHub API'sinde lisans bildirmiyor | Metin depoya **kopyalanmadı**. Kurallar erişilebilirlik, form, odak ve tipografi denetim listesi olarak uygulandı ve yalnız kaynağına başvurulur |

## 4. Resmî başvuru kaynakları (tasarım kararlarında kullanılan)

- SQL Server Linux container gereksinimleri: <https://learn.microsoft.com/en-us/sql/linux/install-upgrade/quickstart-install-docker>
- SQL Server ortam değişkenleri: <https://learn.microsoft.com/en-us/sql/linux/configure/environment-variables>
- SQL Server sürümleri ve Developer sürümü lisans kapsamı: <https://learn.microsoft.com/en-us/sql/sql-server/editions-and-components-of-sql-server-2025>
- SQL Server imaj etiketleri: <https://mcr.microsoft.com/v2/mssql/server/tags/list>
- Ollama OpenAI uyumluluğu: <https://docs.ollama.com/api/openai-compatibility>
- LM Studio OpenAI uyumlu uçlar: <https://lmstudio.ai/docs/developer/openai-compat>
- NuGet paket sürüm dizinleri: <https://api.nuget.org/v3-flatcontainer/>

## 5. Bu dosyada olmayan ne var

Geçişli bağımlılıkların tamamı tek tek burada sayılmaz; tam liste yukarıda adı geçen iki envanter dosyasındadır. Envanter, paketlerin bildirdiği lisans alanını okur; lisans metinlerinin uygunluğu ayrıca incelenmedi.
