import { expect, test } from '@playwright/test';
import { clickAndWait, createDraft, login, openSession } from './support/app';

// C. Eski sürüm: iki kullanıcı aynı taslağı düzenler; ikinci kayıt 412 alır, yeniden yükler ve kaydeder.
const SKU = 'SLT-TEK-BYZ';

test('aynı taslağı iki kişi düzenlerse ikinci kayıt 412 "belge değişti" durumunu görür ve yeniden yükleyip kaydeder', async ({ page, browser }) => {
  await login(page, 'satis1');
  const order = await createDraft(page, 'sales', /MUS-YILDIZ/, [{ sku: SKU, quantity: 5 }]);
  const editUrl = `/satis/siparisler/${order.id}/duzenle`;

  // Both open the same version of the draft.
  await page.goto(editUrl);
  await expect(page.getByRole('heading', { name: new RegExp(`${order.number}.*Taslağı düzenle`), level: 1 })).toBeVisible();
  const manager = await openSession(browser, 'satis.mudur', editUrl);
  await expect(manager.page.getByRole('heading', { name: new RegExp(`${order.number}.*Taslağı düzenle`), level: 1 })).toBeVisible();

  // The manager saves first.
  await manager.page.getByLabel(`${SKU} miktarı`).fill('7');
  const first = await clickAndWait(manager.page, manager.page.getByRole('button', { name: 'Taslağı kaydet' }), 'PUT', `/api/sales/orders/${order.id}`);
  expect(first.status()).toBe(200);
  await expect(manager.page.getByRole('row', { name: new RegExp(SKU) }).getByRole('cell').nth(2)).toHaveText('7');
  await manager.context.close();

  // The owner's copy is now stale: the save is refused and says so.
  await page.getByLabel(`${SKU} miktarı`).fill('9');
  await page.getByLabel('Not (isteğe bağlı)').fill('E2E: eski sürüm denemesi');
  const stale = await clickAndWait(page, page.getByRole('button', { name: 'Taslağı kaydet' }), 'PUT', `/api/sales/orders/${order.id}`);
  expect(stale.status()).toBe(412);
  const alert = page.getByRole('alert').filter({ hasText: 'değişti' });
  await expect(alert.getByRole('heading')).toHaveText('Bu taslak siz görüntüledikten sonra değişti');
  await expect(alert).toContainText('İşleminiz uygulanmadı');
  await expect(alert).toContainText('SIP-412');

  // Reload keeps what the user typed and fetches the current version.
  const reloaded = await clickAndWait(page, alert.getByRole('button', { name: 'Güncel taslak bilgisini yükle' }), 'GET', `/api/sales/orders/${order.id}`);
  expect(reloaded.status()).toBe(200);
  await expect(page.getByRole('heading', { name: 'Güncel sürüm yüklendi' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Bu taslak siz görüntüledikten sonra değişti' })).toHaveCount(0);
  await expect(page.getByLabel(`${SKU} miktarı`)).toHaveValue('9');
  await expect(page.getByLabel('Not (isteğe bağlı)')).toHaveValue('E2E: eski sürüm denemesi');

  const saved = await clickAndWait(page, page.getByRole('button', { name: 'Taslağı kaydet' }), 'PUT', `/api/sales/orders/${order.id}`);
  expect(saved.status()).toBe(200);
  expect(saved.request().headers()['if-match']).not.toBe(stale.request().headers()['if-match']);
  await expect(page.getByRole('heading', { name: new RegExp(`${order.number}.*Satış siparişi`), level: 1 })).toBeVisible();
  await expect(page.getByRole('row', { name: new RegExp(SKU) }).getByRole('cell').nth(2)).toHaveText('9');
});
