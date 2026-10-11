import { expect, test } from '@playwright/test';
import { apiGet, apiSend, balanceOf, balanceStrip, clickAndWait, createDraft, login, openSession, pageHead, productBySku, uniqueRef } from './support/app';

// A. Satın almadan stoğa: limit reddi → onay → kısmi mal kabul → yinelenen irsaliye → fazla kabul.
const SKU_A = 'BRU-SPR-20';
const SKU_B = 'KLP-KBL-25';
const ORDERED_A = 10;
const ORDERED_B = 20;
const RECEIVED_A = 4;

interface PurchaseOrder {
  eTag: string;
  status: string;
  lines: { id: string; sku: string; openQty: number }[];
}

test('satın alma siparişi onay limitiyle reddedilir, onaylanır, kısmi kabul stoğu tam o kadar artırır', async ({ page, browser }) => {
  // --- satinalma1 creates the draft; with an approval limit of 0 the approval is refused.
  await login(page, 'satinalma1');
  const order = await createDraft(page, 'purchase', /TED-TRAKYA/, [
    { sku: SKU_A, quantity: ORDERED_A, unitCost: '310,00' },
    { sku: SKU_B, quantity: ORDERED_B, unitCost: '27,50' },
  ]);
  await expect(pageHead(page)).toContainText('Taslak');
  await expect(page.getByRole('cell', { name: '₺310,00' })).toBeVisible();

  const refused = await clickAndWait(page, page.getByRole('button', { name: 'Siparişi onayla' }), 'POST', `/api/purchasing/orders/${order.id}/approve`);
  expect(refused.status()).toBe(403);
  const refusal = page.getByRole('alert').filter({ hasText: 'onaylanamadı' });
  await expect(refusal).toContainText('YTK-403');
  await expect(refusal).toContainText('Onay limitiniz ₺0,00');
  await expect(refusal.getByRole('button', { name: /YTK-403 kodunu Kaynaklı Yardım/ })).toBeVisible();
  await expect(pageHead(page)).toContainText('Taslak');
  expect((await apiGet<PurchaseOrder>(page, `/api/purchasing/orders/${order.id}`)).status).toBe('Draft');

  // --- satinalma.onay has a sufficient limit.
  const approver = await openSession(browser, 'satinalma.onay', `/satin-alma/siparisler/${order.id}`);
  await expect(approver.page.getByRole('heading', { level: 1 })).toContainText(order.number);
  const approved = await clickAndWait(approver.page, approver.page.getByRole('button', { name: 'Siparişi onayla' }), 'POST', '/approve');
  expect(approved.status()).toBe(200);
  await expect(pageHead(approver.page)).toContainText('Onaylandı');
  await approver.context.close();

  // --- depo1 receives part of it.
  const depot = await openSession(browser, 'depo1', '/depo/kabul');
  const dp = depot.page;
  await expect(dp.getByRole('heading', { name: 'Kabul bekleyenler', level: 1 })).toBeVisible();
  const before = await balanceOf(dp, 'IST-01', SKU_A);
  const beforeB = await balanceOf(dp, 'IST-01', SKU_B);

  await dp.getByRole('link', { name: order.number, exact: true }).click();
  await expect(dp.getByRole('heading', { name: new RegExp(`${order.number}.*Mal kabul girişi`), level: 1 })).toBeVisible();
  // The warehouse view carries no costs: neither a column nor an amount anywhere on the page.
  await expect(dp.getByRole('columnheader', { name: /maliyet|Tutar|fiyat/i })).toHaveCount(0);
  await expect(dp.getByRole('main')).not.toContainText('₺');

  const deliveryNote = uniqueRef('E2E-IRS');
  await dp.getByLabel('Tedarikçi irsaliye numarası').fill(deliveryNote);
  await dp.getByLabel(`${SKU_A} için bu kabulün miktarı`).fill(String(RECEIVED_A));
  const posted = await clickAndWait(dp, dp.getByRole('button', { name: 'Mal kabulü kaydet' }), 'POST', `/api/purchasing/orders/${order.id}/receipts`);
  expect(posted.status()).toBe(201);
  expect(posted.request().headers()['if-match'], 'mal kabulde If-Match gönderilmez').toBeUndefined();
  const receipt = (await posted.json()) as { number: string };

  await expect(dp.getByText(`Mal kabul ${receipt.number} kaydedildi`)).toBeVisible();
  await expect(pageHead(dp)).toContainText('Kısmen kabul edildi');
  await expect(balanceStrip(dp)).toHaveAccessibleName(
    `Sipariş ${ORDERED_A + ORDERED_B} adet: kabul edilen ${RECEIVED_A}, iptal 0, açık ${ORDERED_A + ORDERED_B - RECEIVED_A}.`,
  );
  const lineA = dp.getByRole('row', { name: new RegExp(SKU_A) });
  await expect(lineA.getByRole('cell').nth(4)).toHaveText(String(ORDERED_A - RECEIVED_A));

  // Stock rose by exactly the received quantity, and only for the received product.
  const after = await balanceOf(dp, 'IST-01', SKU_A);
  expect(after.onHand - before.onHand).toBe(RECEIVED_A);
  expect(after.reserved).toBe(before.reserved);
  expect((await balanceOf(dp, 'IST-01', SKU_B)).onHand).toBe(beforeB.onHand);

  const product = await productBySku(dp, SKU_A);
  await dp.goto(`/depo/stok?ara=${SKU_A}`);
  await expect(dp.getByRole('row', { name: new RegExp(SKU_A) }).getByRole('cell').nth(2)).toHaveText(new Intl.NumberFormat('tr-TR').format(after.onHand));
  await dp.goto(`/depo/hareketler?urun=${product.id}`);
  const movement = dp.getByRole('row', { name: new RegExp(receipt.number) });
  await expect(movement).toContainText('Mal kabul');
  await expect(movement).toContainText(`+${RECEIVED_A}`);
  await expect(movement).toContainText(new Intl.NumberFormat('tr-TR').format(after.onHand));

  // --- The same delivery note again: refused, nothing applied twice.
  await dp.goto(`/depo/kabul/${order.id}`);
  await dp.getByLabel('Tedarikçi irsaliye numarası').fill(deliveryNote);
  await dp.getByLabel(`${SKU_A} için bu kabulün miktarı`).fill('1');
  const duplicate = await clickAndWait(dp, dp.getByRole('button', { name: 'Mal kabulü kaydet' }), 'POST', '/receipts');
  expect(duplicate.status()).toBe(409);
  const duplicateSummary = dp.getByRole('group', { name: /Bu iş referansı daha önce kullanıldı/ });
  await expect(duplicateSummary).toBeFocused();
  await expect(duplicateSummary).toContainText('REF-409');
  await expect(duplicateSummary).toContainText('ikinci kez');
  await expect(dp.getByLabel('Tedarikçi irsaliye numarası')).toHaveAttribute('aria-invalid', 'true');
  expect((await balanceOf(dp, 'IST-01', SKU_A)).onHand).toBe(after.onHand);

  // --- Over-receipt. The form itself refuses a quantity above the open quantity it shows…
  const open = ORDERED_A - RECEIVED_A;
  await dp.getByLabel('Tedarikçi irsaliye numarası').fill(uniqueRef('E2E-IRS'));
  await dp.getByLabel(`${SKU_A} için bu kabulün miktarı`).fill(String(open + 1));
  let sent = 0;
  const count = (request: { url(): string; method(): string }) => {
    if (request.method() === 'POST' && request.url().includes('/receipts')) {
      sent += 1;
    }
  };
  dp.on('request', count);
  await dp.getByRole('button', { name: 'Mal kabulü kaydet' }).click();
  await expect(dp.getByRole('group', { name: /Mal kabul kaydedilmedi: 1 alanı düzeltin/ })).toContainText(`En fazla ${open} girilebilir.`);
  expect(sent, 'açık miktarı aşan giriş sunucuya gönderilmez').toBe(0);
  dp.off('request', count);

  // …and when the open quantity shrinks behind the form's back (another delivery recorded elsewhere),
  // the server refuses with MKB-409 and applies nothing.
  const current = await apiGet<PurchaseOrder>(dp, `/api/purchasing/orders/${order.id}`);
  const lineId = current.lines.find((l) => l.sku === SKU_A)?.id ?? '';
  const elsewhere = await apiSend(dp, 'POST', `/api/purchasing/orders/${order.id}/receipts`, {
    externalReference: uniqueRef('E2E-IRS'),
    lines: [{ orderLineId: lineId, quantity: open - 1 }],
  });
  expect(elsewhere.status()).toBe(201);
  const afterElsewhere = await balanceOf(dp, 'IST-01', SKU_A);

  await dp.getByLabel(`${SKU_A} için bu kabulün miktarı`).fill(String(open));
  const over = await clickAndWait(dp, dp.getByRole('button', { name: 'Mal kabulü kaydet' }), 'POST', '/receipts');
  expect(over.status()).toBe(409);
  const overSummary = dp.getByRole('group', { name: /Miktar kalan miktarı aşıyor/ });
  await expect(overSummary).toContainText('MKB-409');
  await expect(overSummary).toContainText(`İstenen ${open}, kalan 1`);
  await expect(overSummary).toContainText('Hiçbir satır kaydedilmedi');
  expect((await balanceOf(dp, 'IST-01', SKU_A)).onHand).toBe(afterElsewhere.onHand);
  // The table was refreshed with the real open quantity.
  await expect(dp.getByRole('row', { name: new RegExp(SKU_A) }).getByRole('cell').nth(4)).toHaveText('1');

  // --- The order document as the warehouse sees it: still no costs.
  await dp.goto(`/satin-alma/siparisler/${order.id}`);
  await expect(dp.getByRole('heading', { level: 1 })).toContainText(order.number);
  await expect(dp.getByText(/Depo görünümü: maliyet ve ticari notlar gösterilmez/)).toBeVisible();
  await expect(dp.getByRole('columnheader', { name: /maliyet|Tutar/i })).toHaveCount(0);
  await expect(dp.getByRole('main')).not.toContainText('₺');
  await depot.context.close();
});
