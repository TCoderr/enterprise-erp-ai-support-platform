import { randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import {
  addLine,
  apiGet,
  apiSend,
  balanceOf,
  clickAndWait,
  createDraft,
  login,
  loginHere,
  loginWith,
  logout,
  me,
  openSession,
  productBySku,
  uniqueRef,
  warehouseId,
} from './support/app';

// D. Erişim ve oturum: kapsam dışı belge "bulunamadı", role göre gezinme, depo kapsamı, oturum sonu, önbellek yalıtımı.

const navigation = (page: Page) => page.getByRole('navigation', { name: 'Ana gezinme' });

test('satis1, satis2’nin ANK-01 siparişini ve IST-01 taslağını göremez: bulunamadı veya erişim yok (404)', async ({ page, browser }) => {
  const other = await openSession(browser, 'satis2');
  const ankara = await createDraft(other.page, 'sales', /MUS-BASKENT/, [{ sku: 'SLT-TEK-BYZ', quantity: 3 }], 'ANK-01');
  const istanbul = await createDraft(other.page, 'sales', /MUS-BASKENT/, [{ sku: 'SLT-TEK-BYZ', quantity: 2 }], 'IST-01');
  await other.context.close();

  await login(page, 'satis1');
  for (const order of [ankara, istanbul]) {
    const response = page.waitForResponse((r) => r.url().endsWith(`/api/sales/orders/${order.id}`));
    await page.goto(`/satis/siparisler/${order.id}`);
    expect((await response).status()).toBe(404);
    await expect(page.getByRole('heading', { name: 'Sipariş bulunamadı veya erişiminiz yok' })).toBeVisible();
    await expect(page.getByRole('main')).not.toContainText(order.number);
    await expect(page.getByRole('main')).not.toContainText('Başkent');
    // Neither the summary nor the edit screen leaks it.
    expect((await page.request.get(`/api/sales/orders/${order.id}/summary`)).status()).toBe(404);
  }

  // The list of a sales agent holds only their own orders.
  await page.goto('/satis/siparisler');
  await expect(page.getByRole('table')).toBeVisible();
  await expect(page.getByRole('main')).not.toContainText(istanbul.number);
  const list = await apiGet<{ items: { number: string; ownerDisplayName: string }[] }>(page, '/api/sales/orders?limit=100');
  expect(list.items.map((o) => o.number)).not.toContain(istanbul.number);
  expect(new Set(list.items.map((o) => o.ownerDisplayName))).toEqual(new Set(['Satış Temsilcisi 1']));
});

test('admin ticari ve depo gezinmesini görmez; sipariş adresi ve kısıtlı makaleler açılmaz', async ({ page, browser }) => {
  const sales = await openSession(browser, 'satis1');
  const orders = await apiGet<{ items: { id: string }[] }>(sales.page, '/api/sales/orders?limit=1');
  const orderId = orders.items[0]?.id ?? '';
  await sales.context.close();
  expect(orderId).not.toBe('');

  await login(page, 'admin');
  await expect(page.getByRole('heading', { name: 'Kullanıcılar', level: 1 })).toBeVisible();
  const links = await navigation(page).getByRole('link').allTextContents();
  expect(links).toEqual(['Taleplerim', 'Yeni talep', 'Makaleler', 'Kullanıcılar', 'Ana veri', 'Denetim kaydı', 'Operasyon']);
  await expect(page.getByRole('combobox', { name: 'Depo', exact: true })).toHaveCount(0);

  await page.goto(`/satis/siparisler/${orderId}`);
  await expect(page.getByRole('heading', { name: 'Bu ekran rolünüze açık değil', level: 1 })).toBeVisible();
  await expect(page.getByRole('main')).not.toContainText('SO-');
  // The server says the same, whatever the UI hides.
  expect([403, 404]).toContain((await page.request.get(`/api/sales/orders/${orderId}`)).status());
  expect((await page.request.get('/api/sales/orders')).status()).toBe(403);
  expect((await page.request.get('/api/purchasing/orders')).status()).toBe(403);
  const ist = await warehouseId(page, 'IST-01');
  expect([403, 404]).toContain((await page.request.get(`/api/inventory/balances?warehouseId=${ist}`)).status());
  await page.goto('/depo/stok');
  await expect(page.getByRole('heading', { name: 'Bu ekran rolünüze açık değil', level: 1 })).toBeVisible();

  for (const slug of ['destek-stok-farki-incelemesi', 'yonetici-kalan-iptal-kararlari']) {
    const response = page.waitForResponse((r) => r.url().endsWith(`/api/kb/articles/${slug}`));
    await page.goto(`/bilgi-bankasi/makale/${slug}`);
    expect((await response).status()).toBe(404);
    await expect(page.getByRole('heading', { name: 'Makale bulunamadı veya erişiminiz yok' })).toBeVisible();
  }
});

test('depo.izmir depo seçicide yalnız IZM-01 görür', async ({ page }) => {
  await login(page, 'depo.izmir');
  const who = await me(page);
  expect(who.warehouses.map((w) => w.code)).toEqual(['IZM-01']);
  const topBar = page.getByRole('banner');
  await expect(topBar).toContainText('IZM-01');
  await expect(topBar).not.toContainText('IST-01');
  await expect(topBar).not.toContainText('ANK-01');
  // With a single warehouse there is nothing to choose: no selector, hence no other option.
  await expect(page.getByRole('combobox', { name: 'Depo', exact: true })).toHaveCount(0);

  // Another warehouse cannot be forced through the address bar either.
  await page.goto('/depo/stok?depo=IST-01');
  await expect(page.getByRole('main')).toContainText('IZM-01');
  await expect(page.getByRole('main')).not.toContainText('IST-01');
  const ist = await warehouseId(page, 'IST-01');
  expect((await page.request.get(`/api/inventory/balances?warehouseId=${ist}`)).status()).toBe(404);
});

test('depo1 düzeltme yapamaz; depo.sorumlu IST-01’de gerekçe ve açıklamayla düzeltir, ANK-01’de düzeltemez', async ({ page, browser }) => {
  const sku = 'BNT-IZO-SYH';
  // --- depo1: neither a navigation item nor the screen nor the API.
  const operator = await openSession(browser, 'depo1');
  await expect(navigation(operator.page).getByRole('link', { name: 'Düzeltme' })).toHaveCount(0);
  await expect(navigation(operator.page).getByRole('link', { name: 'Transfer' })).toBeVisible();
  await operator.page.goto('/depo/duzeltme');
  await expect(operator.page.getByRole('heading', { name: 'Bu ekran rolünüze açık değil', level: 1 })).toBeVisible();
  const product = await productBySku(operator.page, sku);
  const istanbul = await warehouseId(operator.page, 'IST-01');
  const forbidden = await apiSend(operator.page, 'POST', '/api/inventory/adjustments', {
    reference: uniqueRef('E2E-DZL'),
    warehouseId: istanbul,
    reasonCode: 'Found',
    note: 'E2E: yetkisiz düzeltme denemesi',
    lines: [{ productId: product.id, quantityDelta: 1 }],
  });
  expect(forbidden.status()).toBe(403);
  await operator.context.close();

  // --- depo.sorumlu: Supervise in IST-01 only.
  await login(page, 'depo.sorumlu', '/depo/duzeltme?depo=IST-01');
  await expect(page.getByRole('heading', { name: 'Stok düzeltmesi', level: 1 })).toBeVisible();
  const before = await balanceOf(page, 'IST-01', sku);
  const depotField = page.getByRole('main').getByLabel('Depo', { exact: true });
  await expect(depotField.getByRole('option')).toHaveText([/^IST-01/]);

  // Reason code and note are mandatory.
  const reference = uniqueRef('E2E-DZL');
  await page.getByLabel('Düzeltme referansı').fill(reference);
  await addLine(page, sku);
  await page.getByLabel(`${sku} düzeltme miktarı (artı veya eksi)`).fill('+2');
  await page.getByRole('button', { name: 'Düzeltmeyi gözden geçir…' }).click();
  const summary = page.getByRole('group', { name: /Düzeltme kaydedilmedi: 2 alanı düzeltin/ });
  await expect(summary).toBeFocused();
  await expect(summary).toContainText('Gerekçe kodunu seçin.');
  await expect(summary).toContainText('Açıklama zorunludur');

  await page.getByLabel('Gerekçe kodu').selectOption('Found');
  await page.getByLabel('Açıklama (zorunlu)').fill('E2E: raf arkasında iki adet bulundu');
  await page.getByRole('button', { name: 'Düzeltmeyi gözden geçir…' }).click();
  // Second, explicit step: nothing is posted until it is confirmed.
  await expect(page.getByText(new RegExp(`${reference} referanslı 1 satırlık düzeltme kaydedilsin mi\\?`))).toBeVisible();
  expect((await balanceOf(page, 'IST-01', sku)).onHand).toBe(before.onHand);
  const posted = await clickAndWait(page, page.getByRole('button', { name: 'Düzeltmeyi kaydet' }), 'POST', '/api/inventory/adjustments');
  expect(posted.status()).toBe(201);
  const adjustment = (await posted.json()) as { number: string; createdByDisplayName: string };
  await expect(page.getByText(`Düzeltme ${adjustment.number} kaydedildi`)).toBeVisible();
  expect((await balanceOf(page, 'IST-01', sku)).onHand - before.onHand).toBe(2);
  const listed = page.getByRole('row', { name: new RegExp(adjustment.number) });
  await expect(listed).toContainText('Bulunan');
  await expect(listed).toContainText(`${adjustment.createdByDisplayName} (siz)`);

  // ANK-01 (Operate only): not offered, and refused by the server.
  const ankara = await warehouseId(page, 'ANK-01');
  const refused = await apiSend(page, 'POST', '/api/inventory/adjustments', {
    reference: uniqueRef('E2E-DZL'),
    warehouseId: ankara,
    reasonCode: 'Found',
    note: 'E2E: ANK-01 için yetkisiz düzeltme denemesi',
    lines: [{ productId: product.id, quantityDelta: 1 }],
  });
  expect(refused.status()).toBe(403);
});

test('çıkıştan sonra geri tuşu ve korumalı adres girişe düşer; API 401 döner', async ({ page }) => {
  await login(page, 'satis1');
  await navigation(page).getByRole('link', { name: 'Taleplerim' }).click();
  await expect(page.getByRole('heading', { name: 'Taleplerim', level: 1 })).toBeVisible();
  await logout(page);
  await expect(page.getByText('Çıkış yapıldı')).toBeVisible();

  await page.goBack();
  await expect(page.getByRole('heading', { name: 'Giriş', level: 1 })).toBeVisible();
  await expect(navigation(page)).toHaveCount(0);

  await page.goto('/satis/siparisler');
  await expect(page.getByRole('heading', { name: 'Giriş', level: 1 })).toBeVisible();
  await expect(page).toHaveURL(/\/giris$/);
  expect((await page.request.get('/api/auth/me')).status()).toBe(401);
  expect((await page.request.get('/api/sales/orders')).status()).toBe(401);
});

test('yeni kullanıcı geçici parolayla yalnız parola değiştirebilir; rolü kaldırılınca bir sonraki işlemde girişe gönderilir', async ({ page, browser }) => {
  const userName = `e2e.${Date.now().toString(36)}`;
  await login(page, 'admin', '/yonetim/kullanicilar');
  await page.getByRole('button', { name: 'Yeni kullanıcı' }).click();
  await page.getByLabel('Kullanıcı adı').fill(userName);
  await page.getByLabel('Ad soyad').fill('E2E Geçici Kullanıcı');
  await page.getByLabel('Satış temsilcisi').check();
  const created = await clickAndWait(page, page.getByRole('button', { name: 'Kullanıcıyı oluştur' }), 'POST', '/api/admin/users');
  expect(created.status()).toBe(201);
  expect(created.request().headers()['idempotency-key'], 'parola döndüren istek idempotent kaydedilmez').toBeUndefined();

  // The temporary password is shown once. It is read from the page and never logged or captured.
  const notice = page.getByRole('alert').filter({ hasText: 'geçici parola yalnız şimdi gösteriliyor' });
  // Whatever happens, the notice is dismissed before anything else, so no failure artefact can hold it.
  let temporary: string;
  try {
    temporary = (await notice.getByTestId('temporary-password').innerText({ timeout: 15_000 })).trim();
  } finally {
    await page
      .getByRole('button', { name: 'Parolayı ilettim, ekrandan kaldır' })
      .click({ timeout: 5_000 })
      .catch(() => undefined);
  }

  expect(temporary.length).toBeGreaterThanOrEqual(12);
  await expect(page.getByTestId('temporary-password')).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole('link', { name: userName })).toBeVisible();
  await expect(page.getByTestId('temporary-password')).toHaveCount(0);

  // --- The new user: forced password change, nothing else reachable.
  const context = await browser.newContext();
  const userPage = await context.newPage();
  await userPage.goto('/satis/siparisler');
  await loginWith(userPage, userName, temporary);
  await expect(userPage.getByRole('heading', { name: 'Parolanızı değiştirin', level: 1 })).toBeVisible();
  await expect(navigation(userPage)).toHaveCount(0);
  await userPage.goto('/destek/talepler');
  await expect(userPage.getByRole('heading', { name: 'Parolanızı değiştirin', level: 1 })).toBeVisible();
  const blocked = await userPage.request.get('/api/support/tickets');
  expect(blocked.status()).toBe(403);
  expect(((await blocked.json()) as { code: string }).code).toBe('password_change_required');

  const fresh = `Kn!${randomUUID()}`;
  await userPage.getByLabel('Mevcut parola').fill(temporary);
  await userPage.getByLabel('Yeni parola', { exact: true }).fill(fresh);
  await userPage.getByLabel('Yeni parola (tekrar)').fill(fresh);
  const changed = await clickAndWait(userPage, userPage.getByRole('button', { name: 'Yeni parolayı kaydet ve devam et' }), 'POST', '/api/auth/change-password');
  expect(changed.status()).toBe(204);
  await expect(userPage.getByRole('button', { name: /Kullanıcı menüsü: E2E Geçici Kullanıcı/ })).toBeVisible();
  await expect(navigation(userPage).getByRole('link', { name: 'Siparişler' })).toBeVisible();

  // --- admin removes the role: the user's sessions end.
  await page.getByRole('link', { name: userName }).click();
  await expect(page.getByRole('heading', { name: 'E2E Geçici Kullanıcı', level: 1 })).toBeVisible();
  await page.getByRole('group', { name: 'Roller' }).getByLabel('Satış temsilcisi').uncheck();
  const saved = await clickAndWait(page, page.getByRole('button', { name: 'Rolleri ve limiti kaydet' }), 'PUT', '/roles');
  expect(saved.status()).toBe(200);
  expect(saved.request().headers()['if-match']).toBeTruthy();
  await expect(page.getByText(/Roller ve onay limiti kaydedildi/)).toBeVisible();

  const rejected = userPage.waitForResponse((r) => r.url().includes('/api/') && r.status() === 401);
  await navigation(userPage).getByRole('link', { name: 'Taleplerim' }).click();
  expect((await rejected).status()).toBe(401);
  await expect(userPage.getByRole('heading', { name: 'Giriş', level: 1 })).toBeVisible();
  await expect(userPage.getByText('Oturumunuz sona erdi')).toBeVisible();
  await expect(navigation(userPage)).toHaveCount(0);
  await context.close();

  // Tidy up: the throwaway account is deactivated.
  await page.getByRole('button', { name: 'Hesabı pasifleştir…' }).click();
  const deactivated = await clickAndWait(page, page.getByRole('button', { name: 'Hesabı pasifleştir', exact: true }), 'POST', '/deactivate');
  expect(deactivated.status()).toBe(200);
  await expect(page.getByText('Hesap pasifleştirildi', { exact: false })).toBeVisible();
});

test('aynı sayfada satis1 çıkıp depo1 girince satis1’e ait hiçbir şey DOM’da kalmaz', async ({ page }) => {
  await login(page, 'satis1');
  const mine = await apiGet<{ items: { id: string; number: string; status: string }[] }>(page, '/api/sales/orders?status=Draft&limit=5');
  const draft = mine.items[0];
  expect(draft, 'satis1’in en az bir taslağı var').toBeDefined();
  const draftNumber = draft?.number ?? '';

  // Fill the page and the cache with satis1's private data: list, a draft with prices, the user name.
  await expect(page.getByRole('main')).toContainText(draftNumber);
  await page.goto(`/satis/siparisler/${draft?.id ?? ''}`);
  await expect(page.getByRole('heading', { level: 1 })).toContainText(draftNumber);
  await expect(page.getByRole('main')).toContainText('₺');
  await expect(page.getByRole('banner')).toContainText('Satış Temsilcisi 1');

  await logout(page);
  await expect(page.locator('body')).not.toContainText(draftNumber);
  await expect(page.locator('body')).not.toContainText('₺');

  // Same page, same tab, another colleague.
  await loginHere(page, 'depo1');
  await expect(page.getByRole('heading', { name: 'Sevk bekleyenler', level: 1 })).toBeVisible();
  await expect(page.getByRole('table').first().or(page.getByRole('heading', { name: /sevk bekleyen sipariş yok/ }))).toBeVisible();
  const html = await page.content();
  expect(html).not.toContain(draftNumber);
  expect(html).not.toContain('₺');
  expect(html).not.toContain('Satış Temsilcisi 1');
  // Nothing was parked in browser storage either.
  const stored = await page.evaluate(() => ({ local: window.localStorage.length, session: window.sessionStorage.length }));
  expect(stored).toEqual({ local: 0, session: 0 });
  // A draft is invisible to the warehouse: the address answers "not found".
  await page.goto(`/satis/siparisler/${draft?.id ?? ''}`);
  await expect(page.getByRole('heading', { name: 'Sipariş bulunamadı veya erişiminiz yok' })).toBeVisible();
});
