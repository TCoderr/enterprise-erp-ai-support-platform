import { expect, test } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';
import { WIDTHS, apiGet, capture, createApprovedPurchaseOrder, expectNoPageOverflow, login, openSession } from './support/app';
import type { CreatedOrder } from './support/app';
import { DEMO_LABEL, ask, assistPanel } from './support/assist';

// H. Üç genişlik: 360×800, 768×1024, 1440×900. Her kritik ekran için yatay taşma, tablo erişilebilirliği,
// birincil eylemin görünürlüğü denetlenir ve tam sayfa ekran görüntüsü <ekran>-<genişlik>.png adıyla kaydedilir.
// Ekran görüntüleri yalnız oturum açıldıktan sonra alınır; giriş ekranında parola alanı boştur ve ayrıca maskelenir.

/**
 * Every table cell must be reachable: inside the viewport (stacked rows on narrow screens) or inside a
 * horizontally scrollable region that takes keyboard focus.
 */
async function expectTablesReachable(page: Page): Promise<void> {
  const problems = await page.evaluate(() => {
    const issues: string[] = [];
    const viewport = document.documentElement.clientWidth;
    for (const cell of document.querySelectorAll<HTMLElement>('main table th, main table td, aside table td')) {
      const style = getComputedStyle(cell);
      if (style.display === 'none' && cell.textContent !== '' && cell.querySelector('*') !== null) {
        issues.push(`gizli hücre: ${cell.textContent.trim().slice(0, 30)}`);
        continue;
      }

      const rect = cell.getBoundingClientRect();
      if (rect.width === 0 || (rect.left >= -1 && rect.right <= viewport + 1)) {
        continue;
      }

      let scroller: HTMLElement | null = cell.parentElement;
      while (scroller !== null && !(scroller.scrollWidth > scroller.clientWidth + 1 && /auto|scroll/.test(getComputedStyle(scroller).overflowX))) {
        scroller = scroller.parentElement;
      }

      if (scroller === null || scroller === document.documentElement || scroller === document.body) {
        issues.push(`görünüm dışında hücre: ${cell.textContent.trim().slice(0, 30)}`);
      } else if (scroller.tabIndex < 0) {
        issues.push(`odaklanamayan kaydırma bölgesinde hücre: ${cell.textContent.trim().slice(0, 30)}`);
      }
    }

    return issues;
  });
  expect(problems, 'tablo içeriğine erişilemiyor').toEqual([]);
}

/** The main action of the screen is on screen horizontally: no sideways scrolling to find it. */
async function expectReachable(page: Page, control: Locator): Promise<void> {
  await control.scrollIntoViewIfNeeded();
  const box = await control.boundingBox();
  const width = page.viewportSize()?.width ?? 0;
  expect(box, 'eylem görünür olmalı').not.toBeNull();
  expect(box?.x ?? -1).toBeGreaterThanOrEqual(0);
  expect((box?.x ?? 0) + (box?.width ?? 0)).toBeLessThanOrEqual(width + 1);
  await page.evaluate(() => window.scrollTo(0, 0));
}

/** Controls a finger has to hit are at least 40 px tall on the phone width. */
async function expectTouchTargets(page: Page): Promise<void> {
  const small = await page.evaluate(() => {
    const found: string[] = [];
    for (const el of document.querySelectorAll<HTMLElement>('button:not(.linkbtn), select, textarea, input:not([type="checkbox"]):not([type="radio"]):not([hidden]), a.btn')) {
      const rect = el.getBoundingClientRect();
      if (rect.width > 0 && rect.height > 0 && rect.height < 39.5 && !el.closest('[inert]')) {
        found.push(`${el.tagName.toLowerCase()} "${(el.getAttribute('aria-label') ?? el.textContent ?? '').trim().slice(0, 30)}" ${Math.round(rect.height)}px`);
      }
    }

    return found;
  });
  expect(small, 'dokunma hedefi 40 px’ten kısa').toEqual([]);
}

async function check(page: Page, screen: string, width: number, primary: Locator): Promise<void> {
  await expectNoPageOverflow(page);
  await expectTablesReachable(page);
  await expectReachable(page, primary);
  if (width === 360) {
    await expectTouchTargets(page);
  }

  await capture(page, screen, width);
}

test('giriş ekranı üç genişlikte', async ({ page }) => {
  for (const size of WIDTHS) {
    await page.setViewportSize(size);
    await page.goto('/giris');
    await expect(page.getByRole('heading', { name: 'Giriş', level: 1 })).toBeVisible();
    await expect(page.getByLabel('Parola', { exact: true })).toHaveValue('');
    await page.evaluate(() => document.fonts.ready);
    await check(page, 'giris', size.width, page.getByRole('button', { name: 'Giriş yap' }));
  }
});

test('kısmen sevk edilmiş satış siparişi üç genişlikte; yazı tipleri ve tabular rakamlar gerçekten uygulanır', async ({ page }) => {
  await login(page, 'satis1');
  const partial = await apiGet<{ items: { id: string; number: string }[] }>(page, '/api/sales/orders?status=PartiallyShipped&limit=1');
  const order = partial.items[0];
  expect(order, 'kısmen sevk edilmiş sipariş').toBeDefined();

  for (const size of WIDTHS) {
    await page.setViewportSize(size);
    await page.goto(`/satis/siparisler/${order?.id ?? ''}`);
    await expect(page.getByRole('heading', { level: 1 })).toContainText(order?.number ?? '');
    await expect(page.getByRole('img', { name: /^Sipariş [\d.]+ adet: sevk edilen/ })).toBeVisible();
    await page.evaluate(() => document.fonts.ready);
    await check(page, 'satis-siparisi', size.width, page.getByRole('button', { name: /Kalanı iptal et \(\d+\)…/ }));
  }

  // Fonts: the bundled faces are loaded (not a fallback) and figures are tabular.
  const fonts = await page.evaluate(() => {
    const probe = (text: string, family: string, numeric: string) => {
      const span = document.createElement('span');
      span.textContent = text;
      span.style.cssText = `position:absolute;visibility:hidden;white-space:nowrap;font:400 32px ${family};font-variant-numeric:${numeric}`;
      document.body.appendChild(span);
      const width = span.getBoundingClientRect().width;
      span.remove();
      return width;
    };
    const ui = '"Sofia Sans Variable"';
    const cell = document.querySelector('main table td.num');
    const plate = document.querySelector('main h1 span');
    return {
      sans: document.fonts.check('400 16px "Sofia Sans Variable"'),
      sansBold: document.fonts.check('650 16px "Sofia Sans Variable"'),
      condensed: document.fonts.check('700 22px "Sofia Sans Condensed Variable"'),
      turkish: document.fonts.check('400 16px "Sofia Sans Variable"', 'ğüşıİöçĞŞ'),
      loaded: [...document.fonts].filter((f) => f.status === 'loaded').map((f) => f.family.replace(/"/g, '')),
      bodyFamily: getComputedStyle(document.body).fontFamily,
      plateFamily: plate !== null ? getComputedStyle(plate).fontFamily : '',
      cellNumeric: cell !== null ? getComputedStyle(cell).fontVariantNumeric : '',
      cellAlign: cell !== null ? getComputedStyle(cell).textAlign : '',
      // With tnum active every digit has the same advance width; without it "1111" is narrower than "0000".
      tabularOnes: probe('1111111111', ui, 'tabular-nums'),
      tabularZeros: probe('0000000000', ui, 'tabular-nums'),
      proportionalOnes: probe('1111111111', ui, 'proportional-nums'),
      proportionalZeros: probe('0000000000', ui, 'proportional-nums'),
      fallbackOnes: probe('1111111111', 'serif', 'normal'),
      lira: document.fonts.check('400 16px "Sofia Sans Variable"', '₺'),
    };
  });
  expect(fonts.sans, 'Sofia Sans yüklü').toBe(true);
  expect(fonts.sansBold).toBe(true);
  expect(fonts.condensed, 'Sofia Sans Condensed yüklü').toBe(true);
  expect(fonts.turkish, 'Türkçe glifler yazı dosyasından').toBe(true);
  expect(fonts.loaded).toEqual(expect.arrayContaining(['Sofia Sans Variable', 'Sofia Sans Condensed Variable']));
  expect(fonts.bodyFamily).toContain('Sofia Sans Variable');
  expect(fonts.plateFamily).toContain('Sofia Sans Condensed Variable');
  expect(fonts.cellNumeric).toContain('tabular-nums');
  expect(fonts.cellAlign).toBe('right');
  expect(Math.abs(fonts.tabularOnes - fonts.tabularZeros), 'tnum: 1 ve 0 aynı genişlikte').toBeLessThan(0.1);
  expect(fonts.tabularOnes).not.toBe(fonts.fallbackOnes);
  test.info().annotations.push({
    type: 'fonts',
    description: `tnum ${fonts.tabularOnes}/${fonts.tabularZeros}, pnum ${fonts.proportionalOnes}/${fonts.proportionalZeros}, ₺ in font: ${fonts.lira}`,
  });
});

test('mal kabul formu ve stok bakiyeleri üç genişlikte', async ({ page, browser }) => {
  const buyer = await openSession(browser, 'satinalma.onay');
  const order: CreatedOrder = await createApprovedPurchaseOrder(buyer.page, [
    { sku: 'KBL-NYM-3X15', quantity: 15, unitCost: 3150 },
    { sku: 'AMP-LED-9W', quantity: 400, unitCost: 24.9 },
    { sku: 'PNO-SVA-12', quantity: 20, unitCost: 371.25 },
  ]);
  await buyer.context.close();

  await login(page, 'depo1');
  for (const size of WIDTHS) {
    await page.setViewportSize(size);
    await page.goto(`/depo/kabul/${order.id}`);
    await expect(page.getByRole('heading', { level: 1 })).toContainText(order.number);
    await page.getByLabel('Tedarikçi irsaliye numarası').fill('E2E-GORSEL-0001');
    await page.getByLabel('KBL-NYM-3X15 için bu kabulün miktarı').fill('10');
    // One line above its open quantity, so the error state is part of what is reviewed.
    await page.getByLabel('PNO-SVA-12 için bu kabulün miktarı').fill('25');
    await page.getByRole('button', { name: 'Mal kabulü kaydet' }).click();
    await expect(page.getByRole('group', { name: /Mal kabul kaydedilmedi: 1 alanı düzeltin/ })).toBeFocused();
    await expect(page.getByRole('main')).not.toContainText('₺');
    await check(page, 'mal-kabul', size.width, page.getByRole('button', { name: 'Mal kabulü kaydet' }));

    await page.goto('/depo/stok');
    await expect(page.getByRole('heading', { name: 'Stok bakiyeleri', level: 1 })).toBeVisible();
    await expect(page.getByRole('table')).toBeVisible();
    // Low stock is a glyph + word, not a colour.
    await expect(page.getByText('Düşük stok').first()).toBeVisible();
    await page.getByRole('link', { name: 'BNT-IZO-SYH' }).click();
    await expect(page.getByRole('img', { name: /^Eldeki [\d.]+ adet: rezerve/ })).toBeVisible();
    await check(page, 'stok-bakiyeleri', size.width, page.getByRole('button', { name: 'Süz', exact: true }));
  }
});

test('talep + yardım paneli (yanıtlı) ve kaynak yok durumu üç genişlikte', async ({ page }) => {
  await login(page, 'satis1');
  const tickets = await apiGet<{ items: { id: string; number: string }[] }>(page, '/api/support/tickets?mine=true&limit=50');
  const ticket = tickets.items[tickets.items.length - 1];
  expect(ticket, 'satis1’in bir talebi').toBeDefined();

  for (const size of WIDTHS) {
    await page.setViewportSize(size);
    await page.goto(`/destek/talepler/${ticket?.id ?? ''}`);
    await expect(page.getByRole('heading', { level: 1 })).toContainText(ticket?.number ?? '');
    await page.getByRole('button', { name: 'Bu talep için Kaynaklı Yardım' }).click();
    const panel = assistPanel(page);
    await expect(panel.getByText(DEMO_LABEL)).toBeVisible();
    expect((await ask(page, 'STK-409 hatası ne anlama geliyor ve ne yapmalıyım?')).status()).toBe(200);
    await expect(panel.getByRole('region', { name: 'Yanıt', exact: true })).toBeVisible();
    await expect(panel.getByRole('button', { name: 'Talebe öneri olarak kaydet…' })).toBeVisible();
    // The panel never replaces the page: a side column when wide, a sheet over the page when narrow.
    if (size.width >= 1200) {
      await expect(page.getByRole('complementary', { name: 'Kaynaklı Yardım' })).toBeVisible();
      await expect(page.getByRole('main')).not.toHaveAttribute('inert', '');
    } else {
      await expect(page.getByRole('dialog', { name: 'Kaynaklı Yardım' })).toBeVisible();
      const box = await panel.boundingBox();
      expect((box?.x ?? -1) >= 0 && (box?.x ?? 0) + (box?.width ?? 0) <= size.width + 1, 'panel görünüm içinde').toBe(true);
    }

    await check(page, 'talep-yardim', size.width, panel.getByRole('button', { name: 'Kaynaklarda ara ve yanıtla' }));

    expect((await ask(page, 'Öğle yemeği menüsünde bugün ne var?')).status()).toBe(200);
    await expect(panel.getByRole('region', { name: 'Yeterli yetkili kaynak yok' })).toBeVisible();
    await expect(panel.getByRole('alert')).toHaveCount(0);
    await check(page, 'yardim-kaynak-yok', size.width, panel.getByRole('button', { name: 'Kaynaklarda ara ve yanıtla' }));
  }
});
