import { expect, test } from '@playwright/test';
import { apiGet, balanceOf, balanceStrip, clickAndWait, createDraft, fillDraft, login, openSession, pageHead, productBySku, uniqueRef } from './support/app';

// B. Siparişten sevkiyata: taslak → onay (rezervasyon) → kısmi sevk → kalanı iptal.
const SKU_A = 'PRZ-TOP-BYZ';
const SKU_B = 'SGR-OTO-16A';
const QTY_A = 6;
const QTY_B = 4;
const SHIPPED_A = 2;

test('sipariş onayı rezerve eder, kısmi sevk eldekini düşürür, kalan iptal rezervasyonu geri verir', async ({ page, browser }) => {
  await login(page, 'satis1');
  const beforeA = await balanceOf(page, 'IST-01', SKU_A);
  const beforeB = await balanceOf(page, 'IST-01', SKU_B);

  // --- Draft: prices come from the server and cannot be typed.
  await fillDraft(page, 'sales', /MUS-MARMARA/, [
    { sku: SKU_A, quantity: QTY_A },
    { sku: SKU_B, quantity: QTY_B },
  ]);
  const draftRow = page.getByRole('row', { name: new RegExp(SKU_A) });
  await expect(draftRow.getByRole('cell', { name: '₺68,50' })).toBeVisible();
  await expect(page.getByRole('textbox', { name: /fiyat|maliyet/i })).toHaveCount(0);
  await expect(page.getByText(/Fiyatlar sunucudaki liste fiyatıdır/)).toBeVisible();

  const created = await clickAndWait(page, page.getByRole('button', { name: 'Taslak siparişi oluştur' }), 'POST', '/api/sales/orders');
  expect(created.status()).toBe(201);
  expect(created.request().postDataJSON(), 'istemci fiyat göndermez').toEqual({
    customerId: expect.any(String),
    warehouseId: expect.any(String),
    lines: [
      { productId: expect.any(String), quantity: QTY_A },
      { productId: expect.any(String), quantity: QTY_B },
    ],
  });
  const order = (await created.json()) as { id: string; number: string };
  await expect(page.getByRole('heading', { level: 1 })).toContainText(order.number);
  await expect(pageHead(page)).toContainText('Taslak');
  await expect(pageHead(page)).toContainText('Satış Temsilcisi 1 (siz)');
  // A draft reserves nothing.
  expect((await balanceOf(page, 'IST-01', SKU_A)).reserved).toBe(beforeA.reserved);

  // --- Confirm: every line is reserved.
  const confirmed = await clickAndWait(page, page.getByRole('button', { name: 'Siparişi onayla' }), 'POST', `/api/sales/orders/${order.id}/confirm`);
  expect(confirmed.status()).toBe(200);
  expect(confirmed.request().headers()['if-match']).toBeTruthy();
  await expect(page.getByText('Sipariş onaylandı')).toBeVisible();
  await expect(pageHead(page)).toContainText('Onaylandı');
  await expect(balanceStrip(page)).toHaveAccessibleName(`Sipariş ${QTY_A + QTY_B} adet: sevk edilen 0, iptal 0, açık (rezerve) ${QTY_A + QTY_B}.`);

  const reservedA = await balanceOf(page, 'IST-01', SKU_A);
  const reservedB = await balanceOf(page, 'IST-01', SKU_B);
  expect(reservedA.reserved - beforeA.reserved).toBe(QTY_A);
  expect(beforeA.available - reservedA.available).toBe(QTY_A);
  expect(reservedA.onHand).toBe(beforeA.onHand);
  expect(reservedB.reserved - beforeB.reserved).toBe(QTY_B);
  expect(beforeB.available - reservedB.available).toBe(QTY_B);

  // --- depo1 ships part of one line.
  const depot = await openSession(browser, 'depo1', '/depo/sevk');
  const dp = depot.page;
  await expect(dp.getByRole('heading', { name: 'Sevk bekleyenler', level: 1 })).toBeVisible();
  await dp.getByRole('link', { name: order.number, exact: true }).click();
  await expect(dp.getByRole('heading', { name: new RegExp(`${order.number}.*Sevkiyat girişi`), level: 1 })).toBeVisible();
  await expect(dp.getByRole('main')).not.toContainText('₺');
  const dispatchRef = uniqueRef('E2E-SVK');
  await dp.getByLabel('Sevk referansı (irsaliye no)').fill(dispatchRef);
  await dp.getByLabel(`${SKU_A} için bu sevkiyatın miktarı`).fill(String(SHIPPED_A));
  const shippedResponse = await clickAndWait(dp, dp.getByRole('button', { name: 'Sevkiyatı kaydet' }), 'POST', `/api/sales/orders/${order.id}/shipments`);
  expect(shippedResponse.status()).toBe(201);
  const shipment = (await shippedResponse.json()) as { number: string };
  await expect(dp.getByText(`Sevkiyat ${shipment.number} kaydedildi`)).toBeVisible();
  await expect(pageHead(dp)).toContainText('Kısmen sevk edildi');
  await expect(balanceStrip(dp)).toHaveAccessibleName(
    `Sipariş ${QTY_A + QTY_B} adet: sevk edilen ${SHIPPED_A}, iptal 0, açık (rezerve) ${QTY_A + QTY_B - SHIPPED_A}.`,
  );

  // --- satis1 sees the partial shipment and cancels the remainder in two explicit steps.
  await page.reload();
  await expect(pageHead(page)).toContainText('Kısmen sevk edildi');
  await expect(page.getByRole('row', { name: new RegExp(shipment.number) })).toContainText(dispatchRef);
  const remaining = QTY_A + QTY_B - SHIPPED_A;
  await page.getByRole('button', { name: `Kalanı iptal et (${remaining})…` }).click();
  const reason = page.getByLabel('İptal gerekçesi');
  await expect(reason).toBeFocused();
  await expect(page.getByText(`Kalan ${remaining} adet iptal edilsin mi?`)).toBeVisible();
  // Opening the step cancels nothing.
  expect((await apiGet<{ status: string }>(page, `/api/sales/orders/${order.id}`)).status).toBe('PartiallyShipped');

  // A reason is mandatory.
  await page.getByRole('button', { name: 'Kalanı iptal et', exact: true }).click();
  await expect(page.getByText('İptal gerekçesi 3–300 karakter olmalıdır.')).toBeVisible();
  await reason.fill('E2E: müşteri kalan miktardan vazgeçti');
  const cancelled = await clickAndWait(page, page.getByRole('button', { name: 'Kalanı iptal et', exact: true }), 'POST', `/api/sales/orders/${order.id}/cancel`);
  expect(cancelled.status()).toBe(200);
  await expect(page.getByText('Kalan miktar iptal edildi')).toBeVisible();
  await expect(pageHead(page)).toContainText('İptal edildi');
  await expect(pageHead(page)).toContainText(`${SHIPPED_A}/${QTY_A + QTY_B} sevk edildi`);
  await expect(balanceStrip(page)).toHaveAccessibleName(
    `Sipariş ${QTY_A + QTY_B} adet: sevk edilen ${SHIPPED_A}, iptal ${remaining}, açık (rezerve) 0.`,
  );

  // Eldeki fell only by what was shipped; Rezerve is back where it was.
  const afterA = await balanceOf(page, 'IST-01', SKU_A);
  const afterB = await balanceOf(page, 'IST-01', SKU_B);
  expect(beforeA.onHand - afterA.onHand).toBe(SHIPPED_A);
  expect(afterA.reserved).toBe(beforeA.reserved);
  expect(beforeA.available - afterA.available).toBe(SHIPPED_A);
  expect(afterB.onHand).toBe(beforeB.onHand);
  expect(afterB.reserved).toBe(beforeB.reserved);

  // Journal and ledger tell the same story.
  const product = await productBySku(page, SKU_A);
  await page.goto(`/depo/hareketler?urun=${product.id}`);
  const movement = page.getByRole('row', { name: new RegExp(shipment.number) });
  await expect(movement).toContainText('Sevkiyat');
  await expect(movement).toContainText(`-${SHIPPED_A}`);

  await page.goto(`/depo/rezervasyonlar?urun=${product.id}`);
  const ledger = page.getByRole('row', { name: new RegExp(order.number) });
  await expect(ledger).toHaveCount(3);
  await expect(ledger.filter({ hasText: 'Rezerve edildi (onay)' })).toContainText(`+${QTY_A}`);
  await expect(ledger.filter({ hasText: 'Sevkle düşüldü' })).toContainText(`-${SHIPPED_A}`);
  await expect(ledger.filter({ hasText: 'İptalle serbest bırakıldı' })).toContainText(`-${QTY_A - SHIPPED_A}`);

  // --- A closed order offers nothing to change.
  await page.goto(`/satis/siparisler/${order.id}`);
  await expect(pageHead(page)).toContainText('İptal edildi');
  await expect(page.getByRole('button', { name: /onayla|iptal et/i })).toHaveCount(0);
  await expect(page.getByRole('link', { name: /Taslağı düzenle|Sevkiyat gir/ })).toHaveCount(0);
  await page.goto(`/satis/siparisler/${order.id}/duzenle`);
  await expect(page.getByRole('heading', { name: 'Bu sipariş artık düzenlenemez' })).toBeVisible();

  await dp.goto(`/depo/sevk/${order.id}`);
  await expect(dp.getByRole('heading', { name: 'Bu sipariş için sevkiyat girilemez' })).toBeVisible();
  await expect(dp.getByRole('button', { name: 'Sevkiyatı kaydet' })).toHaveCount(0);
  await depot.context.close();
});

test('stok yetmeyen sipariş onaylanmaz: satır bazında istenen ve kullanılabilir miktar ile STK-409 gösterilir', async ({ page }) => {
  const sku = 'KBL-NYA-3X25';
  await login(page, 'satis1');
  const before = await balanceOf(page, 'IST-01', sku);
  const wanted = before.available + 1;
  const order = await createDraft(page, 'sales', /MUS-EGE/, [
    { sku: 'BNT-IZO-SYH', quantity: 1 },
    { sku, quantity: wanted },
  ]);

  // The draft form already warned that the quantity is above what is available.
  const refused = await clickAndWait(page, page.getByRole('button', { name: 'Siparişi onayla' }), 'POST', '/confirm');
  expect(refused.status()).toBe(409);
  const problem = (await refused.json()) as { code: string; userCode: string; errors: { field: string; detail: string }[] };
  expect(problem.code).toBe('insufficient_available');
  expect(problem.errors).toEqual([expect.objectContaining({ field: 'lines[1]', detail: `İstenen ${wanted}, kullanılabilir ${before.available}` })]);

  const alert = page.getByRole('alert').filter({ hasText: 'stok yetersiz' });
  await expect(alert.getByRole('heading')).toHaveText('Sipariş onaylanamadı: 1 satırda stok yetersiz');
  await expect(alert).toContainText(`İstenen ${wanted}, kullanılabilir ${before.available}`);
  await expect(alert).toContainText('STK-409');
  await expect(alert).toContainText('Hiçbir satır rezerve edilmedi');
  await expect(page.getByRole('row', { name: new RegExp(sku) })).toContainText(`Stok yetersiz: İstenen ${wanted}, kullanılabilir ${before.available}`);
  await expect(page.getByRole('row', { name: /BNT-IZO-SYH/ })).not.toContainText('Stok yetersiz');

  await expect(pageHead(page)).toContainText('Taslak');
  expect((await apiGet<{ status: string }>(page, `/api/sales/orders/${order.id}`)).status).toBe('Draft');
  // All or nothing: the line that had stock was not reserved either.
  const after = await balanceOf(page, 'IST-01', sku);
  expect(after.reserved).toBe(before.reserved);

  // The error code leads to grounded help.
  await alert.getByRole('button', { name: /STK-409 kodunu Kaynaklı Yardım/ }).click();
  await expect(page.getByRole('complementary', { name: 'Kaynaklı Yardım' }).getByLabel('Sorunuz')).toHaveValue('STK-409 ne anlama geliyor?');
});
