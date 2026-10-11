# Stok Doğruluğu: Değişmez ↔ Test ↔ Gerçek Etki

Bu belge, stok ve belge tutarlılığı için verilen her garantinin **hangi testle, hangi veritabanı rolüyle ve neye bakılarak** doğrulandığını gösterir. Koşu sonuçları (sayılar, tarih, kaynak parmak izi) `final-verification.md` içindedir; burada yalnız eşleme vardır.

## Nasıl sınanıyor

- **Veritabanı**: tek kullanımlık gerçek SQL Server 2022 (Developer, CU27) container'ı. SQLite veya bellek içi sağlayıcı kullanılmaz; kilit ve kısıt davranışı gerçek sağlayıcıda sınanır.
- **Rol**: şema `kantar_migrator` ile kurulur; test edilen uygulama **yalnız kısıtlı `kantar_app` girişiyle** bağlanır. Testlerin doğrudan yaptığı SQL denetimleri de bu girişle yapılır.
- **Yol**: istekler gerçek HTTP hattından geçer (oturum çerezi, CSRF, katı JSON, yetki, idempotency, transaction).
- **Eşzamanlılık**: gerçek paralel isteklerle. Kanıt, bekleme süresi değil **sonuçtur**: kesin başarı sayıları, bakiye, günlük satır sayıları ve aşağıdaki mutabakat. Sıralı yarış testlerinde ilk komut bir hata noktasında (failpoint) tutulur, ikincinin veritabanında gerçekten kilit beklediği `sys.dm_exec_requests` üzerinden görülür, sonra ilk komut bırakılır.
- **Hata enjeksiyonu**: yalnız test derlemesinin DI kaydıyla (`IFailpoints`); üründe bunu açan rota, başlık veya ayar yoktur.
- **Mutabakat denetimi** (`World.AssertReconciledAsync`), stokla ilgili her testin sonunda, dokunulan her (depo, ürün) için:
  `OnHand = Σ hareket`, `Reserved = Σ rezervasyon kaydı`, `Reserved = Σ onaylı açık sipariş miktarı`, `OnHand ≥ Reserved ≥ 0`.

Test sınıfları `tests/Kantar.IntegrationTests` altındadır (aksi belirtilmedikçe).

## 1. Bakiye ve günlük

| Değişmez | Test | Bakılan gerçek etki |
| --- | --- | --- |
| (depo, ürün) başına tek bakiye satırı; `OnHand ≥ Reserved ≥ 0` veritabanı kısıtıdır | `Database.RuntimeRolePermissionTests.Check_constraints_reject_invalid_balances_even_outside_the_api` | `kantar_app` ile doğrudan `UPDATE` → SQL hatası 547; bakiye değişmedi |
| Satır miktarları ve günlük işaretleri veritabanı kısıtıdır (HTTP dışı yazma da reddedilir) | `…Check_constraints_reject_inconsistent_line_quantities_and_journal_rows` | Fazla `ShippedQty` ve ters işaretli hareket → 547 |
| Hareket günlüğü ve rezervasyon defteri değiştirilemez | `…Append_only_tables_refuse_update_delete_and_truncate` (14 tablo), `…Journal_rows_written_by_a_real_transaction_cannot_be_changed_afterwards` | `kantar_app` ile `UPDATE`/`DELETE` → 229, `TRUNCATE` reddi; satır sayısı ve toplam aynı |
| Çalışma zamanı rolü şema değiştiremez, sahip/yazıcı rollerinde değildir | `…Schema_changes_are_refused`, `…Application_login_is_the_restricted_one` | `CREATE`/`ALTER`/`DROP` reddi; `IS_ROLEMEMBER` = 0 |
| Her `OnHand` değişikliği tek hareket satırı ve doğru "sonraki değer" üretir | `Inventory.AdjustmentTests.Positive_and_negative_adjustments_write_one_journal_row_each_with_the_balance_after`, `Flows.ProcureToShipFlowTests.Purchase_receive_reserve_ship_and_cancel_keep_balances_and_journals_consistent` | Hareket listesi: tür, miktar, `onHandAfter`; rezervasyon listesi: neden, miktar, `reservedAfter` |
| Açılış/demo stoku da günlüktedir; doğrudan bakiye yazımı yoktur | `Seeding.DemoSeederTests.Seed_puts_opening_stock_in_through_receipts_and_a_second_run_changes_nothing` | `Σ OnHand = Σ hareket`; düzeltme türünde hareket 0; 3 açılış mal kabulü; ikinci seed'de tüm sayımlar aynı |

## 2. Düzeltme ve transfer

| Değişmez | Test | Bakılan gerçek etki |
| --- | --- | --- |
| Negatif düzeltme rezerve stoğun altına inemez; reddedilen düzeltme hiçbir şey yazmaz | `Inventory.AdjustmentTests.A_negative_adjustment_cannot_eat_into_reserved_stock_and_writes_nothing` | `409 insufficient_available`; bakiye, hareket ve belge sayısı aynı |
| Çok satırlı düzeltmede tek yetersiz satır hepsini reddeder | `…One_short_line_rejects_the_whole_adjustment_including_the_lines_that_would_fit` | Uygun satırların bakiyesi de değişmedi |
| Düzeltme yalnız depo sorumlusu (Supervise) | `…Adjusting_needs_the_warehouse_operator_role_with_supervise_on_that_warehouse` | Görünür depo `403`, görünmeyen `404` |
| Transfer kaynak düşüşü ve hedef artışını birlikte yazar | `Inventory.TransferTests.A_transfer_moves_stock_with_one_out_and_one_in_movement_per_line` | Satır başına bir `TransferOut` + bir `TransferIn`; toplam korunur |
| Rezerve stok transfer edilemez | `…Reserved_stock_cannot_be_transferred_away` | `409`; iki depo da aynı |
| Arada hata olursa hiçbir tarafta kısmi etki kalmaz | `…A_failure_after_the_first_posting_leaves_no_trace_and_the_same_key_then_succeeds` | İlk bakiye güncellemesinden sonra enjekte edilen hata → iki bakiye, hareket, transfer belgesi, denetim ve idempotency kaydı yok; aynı anahtar sonra başarılı |
| Çok satırda tek yetersiz satır hiçbir satırı taşımaz | `…One_short_line_moves_nothing_for_any_line` | Tüm bakiyeler aynı |
| Eşzamanlı transferler stoğu aşamaz | `…Twenty_parallel_transfers_of_one_unit_from_a_stock_of_ten_move_exactly_ten` | Tam 10 başarı; kaynak 0, hedef 10 |
| Ters yönlü paralel transferlerde (deadlock'a açık) toplam korunur | `…Opposite_direction_transfers_in_parallel_conserve_the_totals` | Her istek `201` ya da temiz `409`/`503`; toplamlar ve mutabakat tutuyor |
| Transfer iki tarafta da işlem yetkisi ister | `…Transfer_needs_operate_on_both_sides` | Tek taraf yetkisi yetmez |

## 3. Satın alma ve mal kabul

| Değişmez | Test | Bakılan gerçek etki |
| --- | --- | --- |
| Fazla mal kabul yok | `Purchasing.ReceiptTests.Over_receipt_is_refused_and_writes_nothing` | `409 exceeds_remaining`; stok aynı |
| Yanlış/başka siparişe ait satır reddedilir | `…A_line_of_another_order_or_a_repeated_line_is_a_line_mismatch` | `422 line_mismatch`; stok aynı |
| Kısmi hata kısmi stok/günlük bırakmaz | `…One_bad_line_rejects_the_whole_receipt_including_the_good_line`, `…A_failure_on_the_second_line_leaves_no_stock_no_movement_and_no_receipt_for_the_first` | İyi satır için de stok, hareket ve belge yok |
| Yetkisiz depo | `…Receiving_needs_an_operator_who_operates_the_order_s_warehouse`, `…A_draft_order_is_invisible_to_the_warehouse_and_cannot_be_received` | `404` |
| Paralel mal kabul yalnız kalan miktarı alır | `…Twenty_parallel_receipts_of_ten_units_against_an_order_of_one_hundred_receive_exactly_one_hundred`, `…Parallel_receipts_that_do_not_divide_the_order_stop_at_the_remaining_quantity` | Tam 10 başarı, stok 100, sipariş `Received`; bölünmeyen durumda fazlası `exceeds_remaining` |
| Farklı anahtar + aynı irsaliye numarası ikinci etki yaratmaz | `Idempotency.IdempotencyTests.A_new_key_with_an_already_used_receipt_reference_is_a_duplicate_and_adds_no_stock`, `…Ten_parallel_receipts_with_different_keys_and_one_reference_receive_once` | `409 duplicate_business_reference`; stok ve hareket bir kez |
| İptal yalnız kalanı kapatır; iptal ile kabul yarışı tutarlı biter | `…Cancelling_after_a_receipt_closes_only_the_remainder_and_keeps_the_received_stock`, `…Receiving_a_cancelled_order_is_invalid_state_and_adds_no_stock`, `…Receive_racing_with_cancel_ends_in_one_of_the_two_serial_outcomes`, `…Receipt_first_a_cancel_waiting_behind_it_closes_nothing_until_it_is_sent_against_the_new_version` | `kabul + iptal = sipariş`; stok = kabul edilen |
| Onay limiti durum geçişinin parçasıdır | `Flows.ProcureToShipFlowTests` (limit 0 → `403 approval_limit_exceeded`), birim: `PurchaseOrderStateTests` | – |
| Taslakta kayıp güncelleme yok | `…Draft_update_needs_the_current_etag`, `…Two_concurrent_draft_updates_with_the_same_etag_let_exactly_one_through` | `412`/`428`; tam bir `200` |

## 4. Satış: onay ve rezervasyon

| Değişmez | Test | Bakılan gerçek etki |
| --- | --- | --- |
| Eşzamanlı onay kapasiteyi aşamaz (≥ 20 paralel) | `Sales.ConfirmConcurrencyTests.Twenty_five_concurrent_confirmations_against_stock_for_ten_never_oversell` | 25 sipariş × 10, stok 100 → tam 10 onay, 15 `409`; `Reserved = 100`, `Available = 0`; tam 10 `Reserve` kaydı; reddedilenler `Draft` |
| Çok satırlı onay ya hep ya hiç | `…A_multi_line_order_with_one_short_line_reserves_nothing`, `…Both_short_lines_are_listed_in_one_response` | Yeten satır için de rezervasyon yok; sipariş `Draft`; hata yetersiz satırları listeler |
| Aynı sipariş farklı anahtarlarla tek etki | `…The_same_order_confirmed_ten_times_at_once_with_different_keys_is_reserved_once` | Tam bir `200`, kalanı `412`/`409`; tek rezervasyon |
| Sürüm koruması | `…Confirm_requires_the_current_etag` | Eski `412`, eksik `428` |
| Tekrar oynatma güncel yetkiyi atlamaz | `…Replaying_a_confirmation_after_the_sales_role_was_removed_does_not_return_the_stored_response`, `Idempotency.IdempotencyTests.Replaying_a_receipt_after_the_warehouse_grant_was_removed_does_not_return_the_stored_response` | Kayıtlı `200`/`201` dönmez |
| Negatif düzeltme, onayın ayırdığını alamaz | `Sales.DispatchCancelTests.A_negative_adjustment_racing_with_confirm_cannot_take_what_the_confirmation_reserves`, `Inventory.AdjustmentTests.Of_a_confirmation_and_a_negative_adjustment_the_second_is_decided_on_the_state_the_first_committed` | Her sırada `OnHand ≥ Reserved`; kaybeden `409` |

## 5. Satış: kısmi sevk ve iptal

| Değişmez | Test | Bakılan gerçek etki |
| --- | --- | --- |
| Sevk `OnHand` ve `Reserved`'ı birlikte düşürür; kalan korunur | `Flows.ProcureToShipFlowTests`, `Sales.DispatchCancelTests.Partial_shipments_that_add_up_to_the_order_end_in_shipped` | Bakiye; sipariş `Shipped`; rezerve 0 |
| İptal yalnız sevk edilmemiş kalanı serbest bırakır; sevk edilen stok geri gelmez | `…Cancelling_after_a_shipment_releases_only_the_remainder` | `OnHand` yalnız sevk kadar azaldı; `Reserved` 0 |
| Her iki sıralamada sevk–iptal | `…Shipment_first_a_cancel_waiting_behind_it_releases_nothing_until_it_is_sent_against_the_new_version`, `…Cancel_first_a_shipment_waiting_behind_it_is_refused_and_moves_nothing`, `…Shipping_a_cancelled_order_is_invalid_state_and_moves_nothing` | İkinci komut birincinin commit'lediği durumu görür |
| Yarışta miktar kaybı veya çift etki yok | `…Ship_racing_with_cancel_on_ten_orders_never_loses_or_doubles_a_unit` | Her sipariş: `sevk + iptal = sipariş`; `OnHand` tam sevk kadar azaldı |
| Açık miktardan fazlası / yabancı satır | `…Shipping_more_than_open_or_a_foreign_line_is_refused_as_a_whole` | `409` / `422`; stok aynı |
| Aynı sevk referansı ikinci etki yaratmaz | `Idempotency.IdempotencyTests.A_new_key_with_an_already_used_dispatch_reference_ships_nothing_more`, `Sales.DispatchCancelTests.Ten_parallel_shipments_of_the_full_open_quantity_ship_it_once`, `…Parallel_partial_shipments_stop_at_the_open_quantity` | Tek sevkiyat; fazlası reddedildi |
| Sevk ile negatif düzeltme yarışı | `…A_negative_adjustment_racing_with_a_shipment_sees_only_the_free_quantity` | `OnHand ≥ Reserved` |
| Taslak iptalinin stok etkisi yok | `…Cancelling_a_draft_has_no_stock_or_ledger_effect` | Hareket/rezervasyon satırı eklenmedi |
| Kapanan belge değişmez; snapshot korunur | `Flows.ProcureToShipFlowTests` (kapalı siparişe sevk/düzenleme `409`), `…Snapshots_survive_later_master_data_changes` | Fiyat, ad, müşteri, adres ana veri değişse de aynı |

## 6. Idempotency

| Değişmez | Test | Bakılan gerçek etki |
| --- | --- | --- |
| Aynı anahtar + aynı istek → aynı durum, başlıklar ve **bayt bayt aynı gövde**; tek etki | `Idempotency.IdempotencyTests.The_same_key_and_body_on_a_receipt_replay_the_first_response_byte_for_byte_with_one_stock_effect`, `…The_same_key_on_a_confirmation_replays_the_first_response_and_reserves_once` | Bayt dizisi eşit; `Location`/`ETag` eşit; `Idempotent-Replay: true`; tek hareket |
| Aynı anahtar + farklı gövde reddedilir | `…The_same_key_with_a_different_body_is_refused_and_has_no_effect` | `409 idempotency_key_reused` |
| Eşzamanlı aynı anahtar tek etki | `…Ten_parallel_requests_with_one_key_and_body_get_identical_bytes_and_cause_one_effect` | 10 yanıt bayt bayt aynı; tek etki |
| Anahtar kullanıcı + işlem kapsamındadır | `…A_key_is_scoped_to_the_user_and_the_operation` | – |
| Süresi dolan anahtar yeni istektir; iş referansı çift etkiyi engeller | `…After_the_retention_period_the_key_is_a_new_request_and_the_business_reference_prevents_a_second_effect` | Saat 25 saat ileri; `409`; stok aynı |
| Reddedilen komut kayıt bırakmaz | `…A_rejected_command_leaves_no_record_so_the_same_key_succeeds_once_the_cause_is_fixed`, `…Validation_and_precondition_failures_leave_no_record_either` | Aynı anahtar sonra başarılı |
| Geri alınan transaction'da idempotency kaydı da yoktur | `Inventory.TransferTests.A_failure_after_the_first_posting_leaves_no_trace_and_the_same_key_then_succeeds` | Kayıt satırı yok |

## 7. Kapsam ve sorgular

| Değişmez | Test |
| --- | --- |
| Kapsam dışı depo/belge, yok olan gibi yanıtlanır | `Security.AuthorizationScopeTests.Documents_and_stock_of_another_warehouse_answer_exactly_like_missing_ones` |
| Sahiplik | `…Another_agents_order_is_hidden_from_an_agent_and_open_to_the_sales_manager` |
| Sistem yöneticisi ticari/stok verisi alamaz | `…System_admin_never_receives_commercial_or_stock_data` |
| Depo görevlisi fiyat görmez, taslak görmez | `…Warehouse_operator_gets_the_fulfilment_view_of_confirmed_documents_and_no_drafts` |
| Kaldırılan depo yetkisi yeni oturumda geçerlidir | `…A_removed_or_lowered_warehouse_grant_is_gone_for_the_next_session` |
| Sayfalama: araya yeni satır girse de mevcut satırlar tam bir kez; aynı zaman damgasında kararlı | `Inventory.QueryTests.Movement_pages_cover_every_existing_row_exactly_once_while_new_rows_keep_arriving`, `…Sales_order_pages_are_stable_when_every_order_has_the_same_creation_time`, `…Purchase_order_transfer_and_adjustment_pages_are_stable_under_identical_timestamps` |
| Listeler yalnız görülebilir depoların satırlarını döndürür | `…Lists_return_only_rows_of_warehouses_the_caller_may_see` |

Yetki matrisinin tamamı (rol × işlem × depo seviyesi × sahiplik) ayrıca birim düzeyinde tablo güdümlü sınanır: `tests/Kantar.UnitTests/AccessPolicyTests.cs`. Durum makineleri: `SalesOrderStateTests`, `PurchaseOrderStateTests`.

## 8. Mutabakat ve arka plan işleri

Outbox teslimi (kiralama, çit, yeniden deneme), mutabakatın salt okunur olduğu ve farkı "onarmadığı", temizlik işinin yalnız süresi dolan satırları sildiği `Operations.OutboxAndReconciliationTests` sınıfında sınanır; eşleme ve sonuçlar `final-verification.md` içindedir.

## 9. Transaction yeniden deneme (deadlock ve kilit zaman aşımı)

Sınıf: `Database.TransactionRetryTests`. Hata enjekte edilmez; SQL Server'ın kendisi üretir. İkinci oturum ("rakip") kısıtlı `kantar_app` girişiyle, havuz dışı ayrı bir bağlantıdan yalnız bakiye satırlarında güncelleme kilidi (`UPDLOCK, ROWLOCK`) alır; veri değiştirmez.

| Değişmez | Test | Bakılan gerçek etki |
| --- | --- | --- |
| Deadlock kurbanı olan komut bütünüyle geri alınır, yeniden denenir ve **tam bir kez** etki eder | `…A_command_chosen_as_deadlock_victim_is_rolled_back_retried_and_takes_effect_exactly_once` | Rakip, transferin ikinci dokunacağı satırı tutar; transfer ilk satırı güncelledikten sonra hata noktasında bekletilir; rakibin ilk satırı isterken kilit beklediği `sys.dm_exec_requests` ile görülür; transfer bırakılır → döngü. Sunucunun kendi sayacı (`Number of Deadlocks/sec`, `_Total`) **tam 1** artar; uygulama günlüğünde tam bir yeniden deneme kaydı: hata **1205**, deneme 1; vazgeçme kaydı yok. Rakip kilitleri tutarken bakiye, hareket, belge, denetim ve idempotency kaydı **yok**. Sonuç `201`; iki bakiye, tek belge, tek `TransferOut` + tek `TransferIn`, tek denetim ve tek idempotency kaydı; aynı anahtarın tekrarı bayt bayt aynı yanıt ve ek etki yok; mutabakat tutuyor |
| Yeniden deneme sınırlıdır: kilit alınamıyorsa 3 denemeden sonra vazgeçilir ve hiçbir etki kalmaz | `…A_command_that_cannot_get_its_lock_gives_up_after_three_attempts_and_leaves_nothing_behind` | Rakip ilk satırı istek boyunca tutar; ürünün kendi `LOCK_TIMEOUT 5000` ayarıyla her deneme **1222** alır. Günlük: yeniden deneme (1222, deneme 1), (1222, deneme 2); vazgeçme (1222, deneme 3). Yanıt `503 retry_later`; hiçbir bakiye/hareket/belge/denetim/idempotency kaydı yok. Rakip bırakınca aynı anahtar ilk yürütme olarak `201` alır ve tam bir kez etki eder |

Açık bırakılan iki ayrıntı:

- Deadlock testinde, ürünün 5 sn'lik kilit zaman aşımı ile motorun deadlock taraması (en geç 5 sn'de bir) aynı beklemeyi hangisinin bitireceği konusunda yarışırdı. Test, **yalnız o ilk denemede** isteğin kendi oturumunda `LOCK_TIMEOUT`'u 30 sn'ye çıkarır (hata noktasından, isteğin transaction bağlantısı üzerinden); böylece beklemeyi yalnız motorun deadlock tespiti bitirebilir. Kurban seçimi rakibin `DEADLOCK_PRIORITY HIGH` ayarıyla belirlenir. İkinci deneme ürünün kendi ayarıyla çalışır. Ürün kodu değişmedi.
- Art arda üç **deadlock** ile vazgeçme sınanmadı; sınır, aynı `catch` dalından geçen kilit zaman aşımıyla (1222) gösterildi. Anlık görüntü çakışması (3960) ve bağlantı hatalarıyla yeniden deneme gözlenmedi.

## 10. Doğrulanmayanlar

- **Gerçekten kopan bağlantıda yanıt kaybı**: tekrar oynatma aynı anahtar/baytlarla ve hata noktasıyla geri alma + yeniden deneme üzerinden kanıtlandı; ağ düzeyinde bağlantı koparma sınanmadı.
- **"Önce PO iptali, mal kabul bekliyor" sıralı yarışı** yalnız ardışık testle sınandı (PO iptali üzerinde tutulacak bir hata noktası yok). Diğer yön ve paralel yarış sınandı.
- **Yük ve çok örnek**: testler doğruluk içindir (onlarca paralel istek, tek API süreci). Kapasite ve çok örnekli çalışma sınanmadı.
