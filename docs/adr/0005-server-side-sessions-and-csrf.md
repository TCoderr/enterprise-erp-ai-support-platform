# ADR-0005 — Sunucu taraflı oturum, çerez ve CSRF

Durum: Kabul edildi · Tarih: 2026-10-11

## Bağlam

Arayüz API ile aynı origin'den sunulan bir tarayıcı uygulamasıdır; üçüncü taraf istemci, mobil uygulama veya servisler arası çağrı yoktur. Güvenlik gereksinimleri:

- çıkış, parola değiştirme/sıfırlama ve rol kaldırma **hemen** etkili olmalı; ele geçirilmiş eski çerez çalışmamalı;
- depo yetkisi değişikliği bir sonraki istekte geçerli olmalı;
- tarayıcıda betiğin okuyabileceği kalıcı belirteç bulunmamalı.

## Karar

- **Oturum:** girişte rastgele bir belirteç üretilir; tarayıcıya `__Host-kantar-session` çerezi olarak gider (`Secure; HttpOnly; SameSite=Lax; Path=/`). Sunucu yalnız SHA-256 özetini `iam.Sessions` içinde saklar. Boşta kalma 30 dk, mutlak süre 8 saat.
- **Her istekte güncel yetki:** oturum çözümleme sorgusu oturumun iptal edilmediğini, süresinin dolmadığını ve kullanıcının `SecurityStamp` değeriyle eşleştiğini denetler; aynı sorguda güncel roller ve depo yetkileri okunur. Karar için çerezdeki veya bellekteki eski rol kullanılmaz.
- **İptal:** çıkış → oturum `RevokedAt`; parola değiştirme, parola sıfırlama ve rol değişikliği → `SecurityStamp` yenilenir, kullanıcının oturumları düşer.
- **CSRF:** `GET/HEAD/OPTIONS` dışındaki her `/api` isteğinde `X-CSRF-Token` doğrulanır. Belirteç oturuma bağlıdır (`Sessions.CsrfSecret`); anonim belirteç yalnız giriş için geçerlidir. `SameSite=Lax` ek savunmadır, tek başına yeterli sayılmaz.
- **Hata biçimi:** `401`/`403` JSON `ProblemDetails` döner; HTML yönlendirmesi yoktur.
- **Parola:** PBKDF2 tabanlı özet; 5 hatalı denemede 15 dk kilit. Açık kayıt ucu yoktur.
- JWT, OIDC ve dış kimlik sağlayıcı kullanılmaz.

## Değerlendirilen alternatifler

| Alternatif | Neden seçilmedi |
| --- | --- |
| Çerezde imzalı claim (durumsuz çerez kimliği) | Rol kaldırma ve iptal, çerez süresi dolana kadar etkisiz kalır; iptal listesi eklenince zaten durumlu olur |
| JWT erişim belirteci + yenileme belirteci | Aynı origin tek istemci için kazanç yok; belirteç saklama (bellek/`localStorage`) ve iptal sorunu eklenir |
| OIDC / dış kimlik sağlayıcı | Gereksinim değil; ek bileşen ve bellek. Teknoloji listesi uğruna eklenmez |
| ASP.NET Core Identity'nin tamamı | Kullanıcı deposu, belirteç sağlayıcıları ve arayüz iskelesi bu kapsam için fazla; yalnız parola özetleyicisi kullanılır |
| Yalnız `SameSite` ile CSRF koruması | Tarayıcı davranışına ve üst düzey gezinme istisnalarına bağımlı; açık belirteç daha net bir sınırdır |

## Sonuçlar / maliyet

- Her istekte bir küçük veritabanı sorgusu (oturum + roller + depo yetkileri).
- Oturum tablosu büyür; süresi dolan satırlar zamanlı işle silinir.
- Rol değişikliği kullanıcıyı yeniden girişe zorlar (bilinçli tercih: yetki değişikliği görünür olur).
- Hız sınırı sayaçları ve hesap kilidi süreç/veritabanı düzeyindedir; hesap kilidi, kullanıcı adını bilen birinin hesabı geçici kilitlemesine izin verir (bilinen sınır).
- Anonim CSRF belirteci (Aşama 1'de kesinleşti): `GET /api/auth/csrf` oturum yoksa rastgele bir ön çerez (`__Host-kantar-pre`, `HttpOnly; Secure; SameSite=Strict`) yazar; belirteç `HMAC-SHA256(Security:Web:CsrfKey, "pre:" + çerez)` değeridir ve yalnız `login` uç noktasında kabul edilir. Girişte ön çerez silinir; oturumlu belirteç oturum satırındaki rastgele `CsrfSecret` değeridir. Anahtar, `local-stack init` tarafından üretilen gizli dosyadan okunur; yapılandırılmamışsa süreç başlamaz.

## Değişme koşulu

- Üçüncü taraf istemci, mobil uygulama veya servisler arası çağrı gerekirse belirteç tabanlı bir akış (OIDC) eklenir.
- Çok örnekli ve yüksek trafikli dağıtımda istek başına sorgu darboğaz olursa kısa ömürlü yetki önbelleği + iptal sürümü değerlendirilir; "hemen etkili" garantisi bu durumda önbellek süresi kadar gevşer ve bu açıkça belgelenir.

## Kaynaklar

`__Host-` çerez öneki, `SameSite` davranışı ve ASP.NET Core antiforgery belgeleri bu oturumda resmî kaynaktan doğrulanmadı. Davranış `09` §5.3'teki G4–G11 testleriyle doğrulanacaktır.
