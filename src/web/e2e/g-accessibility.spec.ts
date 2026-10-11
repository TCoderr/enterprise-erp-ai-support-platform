import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import { apiGet, createApprovedPurchaseOrder, login, openSession, tabTo, typePassword } from './support/app';
import { ask, assistPanel } from './support/assist';

// G. Erişilebilirlik ve klavye. axe otomatik denetimdir; ekran okuyucu testi değildir.

/** Violations of impact serious or critical. No rule is disabled. */
async function seriousViolations(page: Page): Promise<string[]> {
  const results = await new AxeBuilder({ page }).analyze();
  return results.violations
    .filter((v) => v.impact === 'serious' || v.impact === 'critical')
    .map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(' ')).slice(0, 4).join(' | ')}`);
}

async function expectAccessible(page: Page, screen: string): Promise<void> {
  expect(await seriousViolations(page), `axe: ${screen}`).toEqual([]);
}

test('axe: giriş ekranı', async ({ page }) => {
  await page.goto('/giris');
  await expect(page.getByRole('heading', { name: 'Giriş', level: 1 })).toBeVisible();
  await expectAccessible(page, 'giriş');
  // With the error summary showing.
  await page.getByRole('button', { name: 'Giriş yap' }).click();
  await expect(page.getByRole('group', { name: /Giriş yapılamadı/ })).toBeFocused();
  await expectAccessible(page, 'giriş (hata özeti)');
});

test('axe: satış listesi, sipariş belgesi, makale ve yardım paneli açık talep', async ({ page }) => {
  await login(page, 'satis1');
  await expect(page.getByRole('table')).toBeVisible();
  await expectAccessible(page, 'sipariş listesi');

  const partial = await apiGet<{ items: { id: string; number: string }[] }>(page, '/api/sales/orders?status=PartiallyShipped&limit=1');
  const order = partial.items[0];
  expect(order, 'kısmen sevk edilmiş sipariş').toBeDefined();
  await page.goto(`/satis/siparisler/${order?.id ?? ''}`);
  await expect(page.getByRole('heading', { level: 1 })).toContainText(order?.number ?? '');
  await expectAccessible(page, 'sipariş belgesi');
  // With the inline cancel step open.
  await page.getByRole('button', { name: /Kalanı iptal et \(\d+\)…/ }).click();
  await expect(page.getByLabel('İptal gerekçesi')).toBeFocused();
  await expectAccessible(page, 'sipariş belgesi (iptal adımı)');

  await page.goto('/bilgi-bankasi/makale/hata-stk-409');
  await expect(page.getByRole('heading', { name: /STK-409/, level: 1 })).toBeVisible();
  await expectAccessible(page, 'makale');

  const tickets = await apiGet<{ items: { id: string; number: string }[] }>(page, '/api/support/tickets?mine=true&limit=1');
  const ticket = tickets.items[0];
  expect(ticket, 'satis1’in bir talebi').toBeDefined();
  await page.goto(`/destek/talepler/${ticket?.id ?? ''}`);
  await page.getByRole('button', { name: 'Bu talep için Kaynaklı Yardım' }).click();
  expect((await ask(page, 'STK-409 hatası ne anlama geliyor?')).status()).toBe(200);
  await expect(assistPanel(page).getByRole('region', { name: 'Yanıt', exact: true })).toBeVisible();
  await expectAccessible(page, 'talep + yardım paneli');
});

test('axe: stok bakiyeleri ve mal kabul formu', async ({ page, browser }) => {
  const buyer = await openSession(browser, 'satinalma.onay');
  const order = await createApprovedPurchaseOrder(buyer.page, [
    { sku: 'DBL-PLS-8', quantity: 12, unitCost: 31.5 },
    { sku: 'VDA-SUN-4X40', quantity: 8, unitCost: 172 },
  ]);
  await buyer.context.close();

  await login(page, 'depo1', '/depo/stok');
  await expect(page.getByRole('table')).toBeVisible();
  await expectAccessible(page, 'stok bakiyeleri');
  await page.goto('/depo/hareketler');
  await expect(page.getByRole('table')).toBeVisible();
  await expectAccessible(page, 'hareketler');

  await page.goto(`/depo/kabul/${order.id}`);
  await expect(page.getByRole('heading', { level: 1 })).toContainText(order.number);
  await expectAccessible(page, 'mal kabul formu');
  // With field errors showing.
  await page.getByLabel('DBL-PLS-8 için bu kabulün miktarı').fill('99');
  await page.getByRole('button', { name: 'Mal kabulü kaydet' }).click();
  await expect(page.getByRole('group', { name: /Mal kabul kaydedilmedi/ })).toBeFocused();
  await expectAccessible(page, 'mal kabul formu (hatalı)');
});

test('axe: yönetim — kullanıcılar', async ({ page }) => {
  await login(page, 'admin', '/yonetim/kullanicilar');
  await expect(page.getByRole('table')).toBeVisible();
  await expectAccessible(page, 'kullanıcılar');
  await page.getByRole('button', { name: 'Yeni kullanıcı' }).click();
  await expect(page.getByRole('heading', { name: 'Yeni kullanıcı', level: 2 })).toBeVisible();
  await expectAccessible(page, 'kullanıcılar (yeni kullanıcı formu)');
});

test('klavye: giriş → talep açma → yardım panelini açıp kapatma, odak tetikleyiciye döner', async ({ page }) => {
  await page.goto('/');
  const userName = page.getByLabel('Kullanıcı adı');
  await expect(userName).toBeVisible();
  await tabTo(page, userName);
  await page.keyboard.type('satis1');
  await page.keyboard.press('Tab');
  await expect(page.getByLabel('Parola', { exact: true })).toBeFocused();
  await typePassword(page, 'satis1');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('navigation', { name: 'Ana gezinme' })).toBeVisible();
  // After signing in, focus is placed on the content of the landing screen.
  await expect(page.getByRole('heading', { name: 'Satış siparişleri', level: 1 })).toBeVisible();
  await expect(page.getByRole('main')).toBeFocused();

  // On a freshly loaded page the first Tab stop is the skip link; it moves focus to the content.
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Satış siparişleri', level: 1 })).toBeVisible();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('link', { name: 'İçeriğe geç' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('main')).toBeFocused();

  // Navigate with the keyboard and fill the form.
  await tabTo(page, page.getByRole('navigation', { name: 'Ana gezinme' }).getByRole('link', { name: 'Yeni talep' }));
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Yeni destek talebi', level: 1 })).toBeVisible();
  await tabTo(page, page.getByLabel('Başlık'));
  const title = `E2E klavye ${Date.now().toString(36)}: sevkiyat ekranı sorusu`;
  await page.keyboard.type(title);
  await page.keyboard.press('Tab');
  await expect(page.getByLabel('Açıklama')).toBeFocused();
  await page.keyboard.type('Sevkiyat girişinde açık miktar sütununu nasıl okumalıyım?');
  await tabTo(page, page.getByLabel('Hata kodu (isteğe bağlı)'));
  await page.keyboard.type('STK-409');
  await tabTo(page, page.getByRole('button', { name: 'Talebi aç' }));
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { level: 1 })).toContainText(title);

  // Open the panel: focus lands in the question box.
  const trigger = page.getByRole('button', { name: 'Bu talep için Kaynaklı Yardım' });
  await tabTo(page, trigger);
  await page.keyboard.press('Enter');
  const panel = assistPanel(page);
  await expect(panel).toBeVisible();
  await expect(panel.getByLabel('Sorunuz')).toBeFocused();
  await page.keyboard.type('STK-409 ne anlama geliyor?');
  await page.keyboard.press('Tab');
  await expect(panel.getByRole('button', { name: 'Kaynaklarda ara ve yanıtla' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(panel.getByRole('region', { name: 'Yanıt', exact: true })).toBeVisible();

  // Escape closes it and focus returns to the control that opened it.
  await page.keyboard.press('Escape');
  await expect(panel).toHaveCount(0);
  await expect(trigger).toBeFocused();

  // Reopen; close with the close button this time.
  await page.keyboard.press('Enter');
  await expect(panel.getByLabel('Sorunuz')).toBeFocused();
  await expect(panel.getByRole('region', { name: 'Yanıt', exact: true })).toHaveCount(0);
  const closeButton = panel.getByRole('button', { name: 'Kaynaklı Yardım panelini kapat' });
  for (let i = 0; i < 4 && !(await closeButton.evaluate((el) => el === document.activeElement)); i += 1) {
    await page.keyboard.press('Shift+Tab');
  }

  await expect(closeButton).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(panel).toHaveCount(0);
  await expect(trigger).toBeFocused();
});

test('klavye: dar ekranda yardım paneli ve gezinme çekmecesi odağı içinde tutar, Escape kapatır', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 800 });
  await login(page, 'satis1', '/bilgi-bankasi');

  const focusLeaks = async (containerId: string, presses: number): Promise<{ outside: string[]; inside: number }> => {
    const outside: string[] = [];
    const inside = new Set<string>();
    for (let i = 0; i < presses; i += 1) {
      await page.keyboard.press('Tab');
      const where = await page.evaluate((id) => {
        const active = document.activeElement;
        const container = document.getElementById(id);
        if (active === null || active === document.body) {
          return { inside: true, label: '' };
        }

        return { inside: container !== null && container.contains(active), label: `${active.tagName}:${(active.textContent ?? '').trim().slice(0, 30)}` };
      }, containerId);
      if (!where.inside) {
        outside.push(where.label);
      } else if (where.label !== '') {
        inside.add(where.label);
      }
    }

    return { outside, inside: inside.size };
  };

  // --- Assist sheet (a dialog on narrow screens).
  const trigger = page.getByRole('banner').getByRole('button', { name: 'Kaynaklı Yardım' });
  await trigger.focus();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name: 'Kaynaklı Yardım' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Kaynaklı Yardım panelini kapat' })).toBeFocused();
  const sheet = await focusLeaks('yardim', 24);
  expect(sheet.outside, 'odak panelin dışına çıkmamalı').toEqual([]);
  expect(sheet.inside).toBeGreaterThanOrEqual(3);
  // The page behind is inert: it cannot be clicked or focused.
  await expect(page.getByRole('main')).toHaveAttribute('inert', '');
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(trigger).toBeFocused();

  // --- Navigation drawer.
  const menu = page.getByRole('button', { name: 'Menü', exact: true });
  await menu.focus();
  await page.keyboard.press('Enter');
  const drawer = page.getByRole('navigation', { name: 'Ana gezinme' });
  await expect(drawer.getByRole('button', { name: 'Menüyü kapat' })).toBeFocused();
  const nav = await focusLeaks('gezinme', 24);
  expect(nav.outside, 'odak çekmecenin dışına çıkmamalı').toEqual([]);
  expect(nav.inside).toBeGreaterThanOrEqual(3);
  await page.keyboard.press('Escape');
  await expect(menu).toBeFocused();
  await expect(menu).toHaveAttribute('aria-expanded', 'false');
});

test('klavye: başarısız form gönderimi odağı hata özetine, özet bağlantıları alanlara taşır', async ({ page }) => {
  await login(page, 'satis1', '/destek/talepler/yeni');
  await expect(page.getByRole('heading', { name: 'Yeni destek talebi', level: 1 })).toBeVisible();
  let posted = 0;
  page.on('request', (request) => {
    if (request.method() === 'POST' && request.url().endsWith('/api/support/tickets')) {
      posted += 1;
    }
  });

  await page.getByLabel('Hata kodu (isteğe bağlı)').fill('409');
  await page.getByRole('button', { name: 'Talebi aç' }).focus();
  await page.keyboard.press('Enter');
  const summary = page.getByRole('group', { name: /Talep açılmadı: 3 alanı düzeltin/ });
  await expect(summary).toBeFocused();
  expect(posted).toBe(0);

  const links = summary.getByRole('link');
  await expect(links).toHaveCount(3);
  await page.keyboard.press('Tab');
  await expect(links.nth(0)).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByLabel('Başlık')).toBeFocused();
  await expect(page.getByLabel('Başlık')).toHaveAttribute('aria-invalid', 'true');
  await expect(page.getByLabel('Başlık')).toHaveAccessibleDescription('Başlık 5–160 karakter olmalıdır.');

  await links.nth(2).click();
  await expect(page.getByLabel('Hata kodu (isteğe bağlı)')).toBeFocused();

  // The server's own field errors are shown next to the field as well: an invisible character is refused.
  await page.getByLabel('Başlık').fill(`E2E görünmez${String.fromCharCode(0x200b)}karakter denemesi`);
  await page.getByLabel('Açıklama').fill('Sıfır genişlikli boşluk içeren başlık sunucuda reddedilmeli.');
  await page.getByLabel('Hata kodu (isteğe bağlı)').fill('');
  const rejected = page.waitForResponse((r) => r.request().method() === 'POST' && r.url().endsWith('/api/support/tickets'));
  await page.getByRole('button', { name: 'Talebi aç' }).click();
  expect((await rejected).status()).toBe(400);
  await expect(page.getByRole('group', { name: /Talep açılmadı: 1 alanı düzeltin/ })).toBeFocused();
  await expect(page.getByLabel('Başlık')).toHaveAttribute('aria-invalid', 'true');
  await expect(page.getByLabel('Başlık')).toHaveAccessibleDescription('Denetim veya görünmez karakterler kullanılamaz.');
});
