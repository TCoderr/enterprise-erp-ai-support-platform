# Kaynaklı Yardım Doğrulaması

Bu belge kaynaklı yardım için **neyin, hangi sağlayıcıyla** doğrulandığını ve neyin doğrulanmadığını ayırır. Tasarım: `10-ai-grounding-and-access-design.md`. Koşu sayıları ve parmak izleri: `final-verification.md`.

## 1. Özet durum

| Katman | Durum | Neyle |
| --- | --- | --- |
| Getirme, yetki filtresi, atıf doğrulama, yeniden yetkilendirme, hata davranışı, açık onayla kaydetme | **Doğrulandı** (otomatik testler) | Gerçek SQL Server + betiklenebilir test sağlayıcısı (varsayılan davranışı deterministik demo sağlayıcı) |
| İstem kurma ve temizleme (roller, sınırlayıcılar, görünmez karakterler) | **Doğrulandı** (birim testleri) | Saf fonksiyonlar |
| Yerel model adaptörü: istek biçimi, yanıt ayrıştırma, hata eşleme, uç nokta kısıtı | **Doğrulandı** (birim testleri) | Sahte HTTP işleyicisi ve gerçek loopback soketi; **gerçek model yok** |
| Demo sağlayıcı | **Doğrulandı**: belirlenimci, yalnız kullandığı kaynağa atıf verir, "dil modeli değil" etiketi taşır | Birim + entegrasyon |
| **Gerçek bir dil modelinin davranışı** (yanıt kalitesi, kaynağa sadakat, çekimser kalma, enjeksiyona direnç, Türkçe) | **DOĞRULANMADI** | Bu makinede yerel model yoktu; `ai-live` kapısı koşulamadı ve `not-run` kaydetti (çağrı `20261011T220422Z-5eib`, kapı çıkış kodu 4; bkz. §6). Canlı AI kabulü yoktur |

Demo sağlayıcıyla geçen hiçbir test, bir dil modelinin kalitesi, halüsinasyon oranı veya anlamsal yararlılığı hakkında bir şey söylemez ve burada öyle sunulmaz.

## 2. Sağlayıcılar

| Sağlayıcı | Ne | Testlerdeki rolü |
| --- | --- | --- |
| `demo` — `DemoAnswerGenerator` (`kural-tabanli-alinti-v1`) | Getirilen parçalardan soruyla ortak terimi olan cümleleri seçer, kullandığı parçalara atıf verir. **Dil modeli değildir**; her yanıtın `limitations` alanı ve arayüz etiketi bunu söyler | Orkestrasyon testlerinin varsayılan davranışı; demo yığınının varsayılanı |
| Betiklenebilir test çifti — `ScriptedAnswerGenerator` (yalnız test derlemesinde) | Uygulamanın sağlayıcıya gönderdiği her isteği yakalar; bir sonraki davranış değiştirilebilir (uydurma atıf, bozuk çıktı, hata, üretim ortasında duraklama) | Yetki ve hata senaryoları. Üründe yoktur |
| `local-model` — `LocalModelAnswerGenerator` | OpenAI-uyumlu `POST /v1/chat/completions`; yalnız loopback veya açıkça izin verilen özel ağ adresi | Yalnız birim testlerinde, sahte HTTP ile. Canlı çağrı yapılmadı |
| `disabled` | Her soruya `ProviderUnavailable` | Compose duman testi (yığın `Disabled` ile yeniden başlatılır) |

Ücretli veya uzak bir uç noktaya hiçbir testte çağrı yapılmadı; adaptör loopback/izinli liste dışındaki adresi açılışta ve bağlantı anında reddeder.

## 3. Yetkili kaynaklar

Yanıt üretimine giren metnin tek kaynağı `KnowledgeService.ReadableChunks` sorgusudur; yetki koşulları SQL `WHERE` içindedir (yayınlı + yayınlanan sürüm + görünürlük + modül + depo kapsamı). Ayrı bir dizin veya önbellek yoktur.

| Garanti | Test (`tests/Kantar.IntegrationTests`) | Bakılan |
| --- | --- | --- |
| Okunamayan belgenin metni sağlayıcıya gitmez, yanıtta da yoktur | `Assistance.AssistOrchestrationTests.Text_of_a_document_the_caller_may_not_read_reaches_neither_the_provider_nor_the_response` | Rastgele kanarya dizgesi: yakalanan sağlayıcı girdisinde ve HTTP yanıtının tamamında yok; yetkili kullanıcıda var |
| Taslak, emekli, eski sürüm ve yayınlanmamış yeni sürüm metni gitmez; depo kapsamlı belge yalnız yetkiliye | `…Draft_retired_superseded_and_unpublished_text_never_reaches_the_provider_and_warehouse_scope_needs_a_grant` | Aynı yöntem |
| Aynı soru, iki kullanıcı → farklı izinli kaynaklar; biri diğerinin yanıtını okuyamaz; yanıt `no-store` | `…Two_users_get_different_sources_for_the_same_question_and_cannot_read_each_others_answers` | Atıf kümeleri; `GET /api/assist/answers/{id}` → 404 |
| Kaynak yoksa sağlayıcı **çağrılmaz** | `…Without_a_readable_matching_source_the_provider_is_not_called` | `NoSource`; çağrı sayısı değişmedi |
| En çok 5 parça / 6000 karakter | `…At_most_five_passages_and_six_thousand_characters_of_source_text_are_sent` | Yakalanan girdi |
| Seçilen talepten yalnız hata kodu/başlık/modül arama ipucudur; talebin açıklaması sağlayıcıya gitmez | `…The_tickets_error_code_guides_retrieval_and_nothing_else_from_the_ticket_reaches_the_provider`, `…A_ticket_the_caller_cannot_see_is_not_found_and_nothing_is_sent_to_the_provider` | Yakalanan girdi; 404 |
| Görünürlük × modül × depo matrisi (okuma, liste, arama) | `Support.KnowledgeAccessTests.Visibility_module_and_warehouse_scope_decide_what_each_role_reads_lists_and_finds` (30 belge × 14 okuyucu), `…System_admin_reads_general_articles_only_and_cannot_manage_the_knowledge_base` | Okunamayan → 404, listede ve aramada yok |
| Sürüm yaşam döngüsü | `…Only_the_published_version_is_served_through_draft_publish_new_version_and_retire`, `…Publishing_an_older_version_again_serves_that_version` | Okuyucu, arama ve `/api/assist/search` |
| Depo yetkisi kalkınca depo kapsamlı belgeler de gider | `…Removing_a_warehouse_grant_removes_the_warehouse_scoped_articles` | Yeni oturum |

## 4. Atıf, çıktı ve yeniden yetkilendirme

| Garanti | Test | Sonuç |
| --- | --- | --- |
| Uydurma, eksik veya ayrıştırılamayan atıf → yanıtın tamamı reddedilir | `…Output_with_an_invented_missing_or_unparsable_citation_is_rejected_whole` | `RejectedUnsupported`; metin ve kimlik yok |
| Sağlayıcı "kaynak yetmiyor" derse | `…A_provider_that_declines_to_answer_from_the_sources_is_reported_as_no_source` | `NoSource` |
| Sağlayıcı hatası ERP'yi etkilemez | `…A_failing_provider_yields_provider_unavailable_with_the_sources_and_leaves_erp_commands_untouched` | `ProviderUnavailable` + kaynaklar; hemen ardından ERP komutu başarılı |
| Üretim sırasında rol kaldırılırsa | `…Removing_the_callers_role_during_generation_discards_the_answer` | `AccessChanged` (HTTP 200); metin, kimlik ve `ai.Answers` satırı yok |
| … depo yetkisi kaldırılırsa | `…Removing_the_callers_warehouse_grant_during_generation_discards_the_answer` | aynı |
| … atıf yapılan belge emekli edilirse | `…Retiring_the_cited_document_during_generation_discards_the_answer` | aynı |
| … atıf yapılan belgenin yeni sürümü yayınlanırsa | `…Publishing_a_new_version_of_the_cited_document_during_generation_discards_the_answer` | aynı |
| Karşı örnekler: değişiklik yoksa ya da yalnız yayınlanmamış sürüm eklendiyse yanıt teslim edilir | `…Pausing_the_provider_without_any_change_still_delivers_the_answer`, `…Adding_an_unpublished_version_during_generation_does_not_discard_the_answer` | `Answered` |
| Çıktıdaki işaretleme ve görünmez karakterler etkisiz, sınırlı düz metindir | `…Markup_and_invisible_characters_in_provider_output_are_returned_as_inert_bounded_text` | Denetim/biçim karakterleri çıkarılır; `<script>` ve `javascript:` düz metin; 4000 karakter sınırı. Arayüz tarafı: `src/web` bileşen testleri ve tarayıcı testi (uyarı penceresi açılmaz, bağlantı oluşmaz) |
| Güven puanı yoktur; her yanıt sağlayıcısını söyler | `…Every_answer_names_its_provider_and_says_that_the_demo_provider_is_not_a_language_model`, `…Status_reports_the_demo_provider_as_a_demo` | `isDemo: true`; `limitations` metni |

Üretim ortası senaryoları belirlenimcidir: test sağlayıcısı üretimi bir engelde tutar, test değişikliği yapar, sonra engeli kaldırır.

**Önemli sınır**: sunucu, atıf etiketinin gönderilen parçalardan birine ait olduğunu doğrular. Yanıt cümlesinin o parçayla **anlamca desteklendiğini doğrulamaz**. Bunu denetleyen otomatik bir test yoktur; arayüz, kullanıcının karşılaştırabilmesi için kaynak alıntısını yanıtın yanında gösterir.

## 5. Enjeksiyon

| Düzey | Test | Kapsam |
| --- | --- | --- |
| Veri erişimi | `…Instructions_planted_in_a_document_or_a_question_do_not_widen_what_the_provider_is_given` | Belge içinde "önceki talimatları yok say…", sahte `system:` satırı; soru başka belgeleri ister → sağlayıcı girdisi yine yalnız okunabilir parçalar |
| İstem yapısı | `tests/Kantar.UnitTests/PromptBuilderTests` (21 yöntem; düşmanca metinler × kaynak metni / başlık / soru) | `system` iletisi sabittir ve kaynak/soru metni içermez; sınırlayıcılar (`<<<`, `>>>`, blok adları) ve satır başındaki sahte `[S9]` etiketleri etkisizleştirilir; HTML etiketleri, denetim karakterleri, tüm Unicode "Format" karakterleri (sıfır genişlikli, yön işaretleri, etiket karakterleri) çıkarılır; Unicode satır ayırıcıları satır sonuna çevrilir |
| Girdi doğrulama | `…Question_length_and_shape_are_validated_before_anything_else_happens`, `TextValidatorTests` | 3–1000 karakter; görünmez karakterli soru `400` |
| Model davranışı | – | **Sınanmadı** (gerçek model yok). İstem düzeyi önlemler güvenlik sınırı değildir; sınır, modele yetkisiz hiçbir metnin gitmemesi, araç bulunmaması ve çıktının sunucuda doğrulanmasıdır |

## 6. Gerçek yerel model kapısı (`ai-live`)

- **Durum: YEREL MODEL YOK; DOĞRULANAMADI.** Kapı son doğrulamada çağrıldı ve `not-run` kaydetti (çağrı `20261011T220422Z-5eib`, kapı çıkış kodu 4); senaryoların hiçbiri koşmadı. Kapıdan hemen önce yeniden denetlendi: `KANTAR_AI_LOCAL_BASE_URL` tanımlı değil; `ollama`, `lms`, `llama-server` PATH'te yok; `127.0.0.1` üzerinde 11434, 1234 ve 8080 portları yanıt vermiyor. Model indirmek, kurmak veya ücretli bir servise çağrı yapmak bu çalışmanın kuralları dışındadır ve yapılmadı. ERP kabulü bu kapıya bağlı değildir; canlı AI kabulü ise yalnız bu kapıyla verilebilir.
- **Hazır olan**: `scripts/gates/ai-live.ps1` + uygulamanın `--ai-live` modu. Sentetik beş senaryo (`LiveModelCheck`): beklenen kaynağa atıf (hata kodu; prosedür), kaynak yokken çekimser kalma, kaynağın içine gömülü talimat, sorunun içine gömülü talimat. Yalnız sunucunun dayandığı özellikler denetlenir (ayrıştırılabilir çıktı, atıfların verilen etiketler içinde kalması, çekimserlik, gömülü talimata uyulmaması); yanıt metinleri okunmak üzere yazdırılır. Değerlendirme kuralları birim testlidir (`LiveModelCheckTests`).
- **Nasıl çalıştırılır** (zaten çalışan bir yerel model sunucunuz varsa):

  ```powershell
  $env:KANTAR_AI_LOCAL_BASE_URL = 'http://127.0.0.1:11434'
  $env:KANTAR_AI_LOCAL_MODEL    = '<o sunucuda zaten bulunan model adı>'
  ./scripts/verify.ps1 -Gate build,ai-live
  ```

  Yapılandırma yoksa veya sunucu yanıt vermezse kapı `not-run` olarak kaydedilir (çıkış kodu 4); "geçti" sayılmaz.
- Bu senaryo seti genel bir model kıyaslaması veya güvenlik sertifikası değildir.

## 7. Yerel model adaptörü — gerçek model olmadan doğrulananlar

`tests/Kantar.UnitTests/LocalModelAnswerGeneratorTests` (21 yöntem), `LocalEndpointPolicyTests` (11):

- İstek `/v1/chat/completions` yoluna gider: `stream=false`, `temperature=0`, model adı; tam iki ileti (`system`, `user`); kaynak metni yalnız `user` iletisinde.
- Ayrıştırma: geçerli JSON nesnesi → atıflı yanıt; düz yazı veya kod bloğu içine sarılmış nesne de ayrıştırılır; `insufficient: true` → çekimser; eksik alan, dizgi olmayan atıf, boş içerik, JSON olmayan gövde, HTML hata sayfası, kesilmiş JSON, nesne olmayan kök (`null`, dizi, dizgi) → "geçersiz çıktı" (istisna değil).
- HTTP 429/500/404/3xx, bağlantı hatası ve zaman aşımı → "kullanılamıyor"; yönlendirme **izlenmez** (gerçek loopback soketiyle de sınandı); çağıranın iptali iptal olarak yayılır.
- Uç nokta kısıtı: loopback kabul; genel adresler, yol/sorgu/kimlik bilgisi içeren URL'ler, http(s) dışı şemalar ve listede olmayan özel adresler red; listelenmiş bir ad genel bir adrese çözülürse bağlantı anında red.

Resmî belgelerden doğrulananlar: Ollama için yol ve yanıt yolu (`choices[0].message.content`); LM Studio için yol. LM Studio'nun yanıt biçimi ve `response_format` davranışı belgeden doğrulanmadı; adaptör `response_format`'a güvenmez.

## 8. Saklama ve gizlilik

| Garanti | Test |
| --- | --- |
| Soru metni hiçbir tabloda yok (`ai.Answers`, `ai.Usage`, `ops.AuditEntries`, `ops.IdempotencyRecords`); `ai.Usage` kullanıcı sütunu taşımaz | `…The_question_is_not_stored_anywhere_and_usage_rows_carry_no_user` |
| Yanıt yalnız sahibine, 30 dakika | `…An_expired_answer_can_be_neither_read_nor_saved`, `…Two_users_get_different_sources…` |
| Talebe yazma yalnız açık komutla, anahtar başına bir kez | `…An_answer_is_written_to_a_ticket_only_by_the_explicit_save_command_and_only_once_per_key` |
| Başkasının yanıtı/talebi kullanılamaz; kaynağı değişmiş yanıt kaydedilemez; kapalı talebe kaydedilemez | `…Somebody_elses_answer_or_ticket_cannot_be_used_for_a_save`, `…An_answer_whose_source_was_retired_republished_or_became_unreadable_cannot_be_saved`, `…A_suggestion_cannot_be_saved_to_a_closed_ticket` |

Günlüklerde soru, istem ve sağlayıcı yanıtı yazılmaz; ölçüm etiketleri yalnız sağlayıcı türü ve sonuç durumudur.

## 9. Doğrulanmayanlar (özet)

- Gerçek bir dil modeliyle hiçbir senaryo.
- Yanıt içeriğinin kaynakla anlamca desteklenmesi (otomatik denetim yok).
- Kaynaklı yardım için hız sınırı (dakikada 6): test ve tarayıcı yığınlarında sınır gevşetilmiştir; sınır kodu yapılandırmayla sınanmadı.
- Büyük bilgi bankası (2000 parça üstü) davranışı ve arama kalitesi ölçümü.
- Yerel model sunucusunun kendi kayıt tutması (Kantar'ın denetiminde değildir).
