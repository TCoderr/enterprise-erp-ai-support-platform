import { readFileSync } from 'node:fs';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { expect } from '@playwright/test';
import type { APIResponse, Browser, BrowserContext, Locator, Page } from '@playwright/test';

// Shared helpers of the end-to-end suite. Everything goes through the real API of the stack under test.

export type DemoUser =
  | 'satis1'
  | 'satis2'
  | 'satis.mudur'
  | 'satinalma1'
  | 'satinalma.onay'
  | 'depo1'
  | 'depo.sorumlu'
  | 'depo.izmir'
  | 'destek1'
  | 'destek.mudur'
  | 'admin';

const SEED_DIR =
  process.env.KANTAR_E2E_SEED_DIR ?? 'D:\\Enterprise ERP & AI Support Platform\\enterprise-erp-ai-support-platform\\.local-e2e\\seed';

export const ARTIFACT_DIR = path.resolve(process.env.KANTAR_E2E_ARTIFACT_DIR ?? 'test-results/screens');

const passwords = new Map<string, string>();

/** Reads a demo password from the seed directory. The value is never logged, printed or asserted on. */
function passwordOf(user: DemoUser): string {
  let value = passwords.get(user);
  if (value === undefined) {
    value = readFileSync(path.join(SEED_DIR, `Seed__Passwords__${user}`), 'utf8').trim();
    passwords.set(user, value);
  }

  return value;
}

/** A reference that is unique across runs and tests: the suite never depends on a clean database. */
let counter = 0;
export function uniqueRef(prefix: string): string {
  counter += 1;
  return `${prefix}-${Date.now().toString(36).toUpperCase()}-${counter}`;
}

export const WIDTHS = [
  { width: 360, height: 800 },
  { width: 768, height: 1024 },
  { width: 1440, height: 900 },
] as const;

// ---------------------------------------------------------------- session
export async function loginWith(page: Page, userName: string, password: string): Promise<void> {
  const passwordBox = page.getByLabel('Parola', { exact: true });
  try {
    await page.getByLabel('Kullanıcı adı').fill(userName);
    await passwordBox.fill(password);
    await page.getByRole('button', { name: 'Giriş yap' }).click();
    await expect(page.getByRole('button', { name: /Kullanıcı menüsü/ }).or(page.getByRole('heading', { name: 'Parolanızı değiştirin' }))).toBeVisible();
  } catch (error) {
    // A failure artefact (screenshot, page snapshot) must never contain a password.
    await passwordBox.fill('').catch(() => undefined);
    throw error;
  }
}

/** Signs in through the login screen and waits for the shell. */
export async function login(page: Page, user: DemoUser, startAt = '/'): Promise<void> {
  await page.goto(startAt);
  await expect(page.getByRole('heading', { name: 'Giriş', level: 1 })).toBeVisible();
  await loginWith(page, user, passwordOf(user));
}

/** Types a demo user's password with the keyboard into the focused field (keyboard-only flows). */
export async function typePassword(page: Page, user: DemoUser): Promise<void> {
  await page.keyboard.type(passwordOf(user));
}

/** Signs in on the login screen that is already showing, without reloading the page. */
export async function loginHere(page: Page, user: DemoUser): Promise<void> {
  await expect(page.getByRole('heading', { name: 'Giriş', level: 1 })).toBeVisible();
  await loginWith(page, user, passwordOf(user));
}

export async function logout(page: Page): Promise<void> {
  await page.getByRole('button', { name: /Kullanıcı menüsü/ }).click();
  await page.getByRole('button', { name: 'Çıkış yap' }).click();
  await expect(page.getByRole('heading', { name: 'Giriş', level: 1 })).toBeVisible();
}

export interface Session {
  context: BrowserContext;
  page: Page;
}

/** A separate browser context (its own cookie jar) signed in as another user. Close it when done. */
export async function openSession(browser: Browser, user: DemoUser, startAt = '/'): Promise<Session> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await login(page, user, startAt);
  return { context, page };
}

// ---------------------------------------------------------------- API access with the page's session
async function csrfToken(page: Page): Promise<string> {
  const response = await page.request.get('/api/auth/csrf');
  expect(response.status(), 'CSRF belirteci').toBe(200);
  return ((await response.json()) as { token: string }).token;
}

export async function apiGet<T>(page: Page, url: string): Promise<T> {
  const response = await page.request.get(url);
  expect(response.status(), `GET ${url}`).toBe(200);
  return (await response.json()) as T;
}

/** A command sent with the page's own session (cookies, CSRF token) — used for test set-up only. */
export async function apiSend(page: Page, method: 'POST' | 'PUT', url: string, body?: unknown, ifMatch?: string): Promise<APIResponse> {
  const headers: Record<string, string> = {
    'X-CSRF-Token': await csrfToken(page),
    'Idempotency-Key': `e2e-${Date.now()}-${Math.random().toString(36).slice(2, 12)}`,
  };
  if (ifMatch !== undefined) {
    headers['If-Match'] = ifMatch;
  }

  const options = body === undefined ? { headers } : { headers, data: body };
  return method === 'POST' ? page.request.post(url, options) : page.request.put(url, options);
}

export interface Me {
  id: string;
  displayName: string;
  warehouses: { id: string; code: string; name: string; level: string }[];
}

export function me(page: Page): Promise<Me> {
  return apiGet<Me>(page, '/api/auth/me');
}

export async function warehouseId(page: Page, code: string): Promise<string> {
  const list = await apiGet<{ id: string; code: string }[]>(page, '/api/warehouses');
  const found = list.find((w) => w.code === code);
  expect(found, `depo ${code}`).toBeDefined();
  return (found as { id: string }).id;
}

export interface Balance {
  productId: string;
  sku: string;
  onHand: number;
  reserved: number;
  available: number;
}

/** Current stock figures of one product in one warehouse, read with the page's session. */
export async function balanceOf(page: Page, warehouseCode: string, sku: string): Promise<Balance> {
  const id = await warehouseId(page, warehouseCode);
  const result = await apiGet<{ items: Balance[] }>(page, `/api/inventory/balances?warehouseId=${id}&search=${encodeURIComponent(sku)}&limit=50`);
  const found = result.items.find((b) => b.sku === sku);
  expect(found, `${warehouseCode} deposunda ${sku} bakiyesi`).toBeDefined();
  return found as Balance;
}

export interface ProductRef {
  id: string;
  sku: string;
}

export async function productBySku(page: Page, sku: string): Promise<ProductRef> {
  const result = await apiGet<{ items: ProductRef[] }>(page, `/api/products?search=${encodeURIComponent(sku)}&limit=50`);
  const found = result.items.find((p) => p.sku === sku);
  expect(found, `ürün ${sku}`).toBeDefined();
  return found as ProductRef;
}

// ---------------------------------------------------------------- UI building blocks
/** Picks a warehouse in the shell selector when the user has more than one. */
export async function selectWarehouse(page: Page, code: string): Promise<void> {
  const selector = page.getByRole('combobox', { name: 'Depo', exact: true });
  if ((await selector.count()) > 0) {
    await selector.selectOption({ value: code });
  }
}

/** Adds one product line through the picker of a draft, transfer or adjustment form. */
export async function addLine(page: Page, sku: string): Promise<void> {
  const search = page.getByRole('searchbox', { name: /Ürün ekle/ });
  await search.fill(sku);
  await search.press('Enter');
  await page.getByRole('list', { name: 'Arama sonuçları' }).getByRole('button', { name: new RegExp(`^${sku}`) }).click();
}

export interface DraftLine {
  sku: string;
  quantity: number;
  /** Purchase orders only. */
  unitCost?: string;
}

export interface CreatedOrder {
  id: string;
  number: string;
}

const DRAFT = {
  sales: { path: '/satis/siparisler/yeni', heading: 'Yeni satış siparişi', party: 'Müşteri', warehouse: 'Sevk deposu', api: '/api/sales/orders' },
  purchase: { path: '/satin-alma/siparisler/yeni', heading: 'Yeni satın alma siparişi', party: 'Tedarikçi', warehouse: 'Teslim deposu', api: '/api/purchasing/orders' },
} as const;

/** Fills the draft form (party, warehouse, lines) without submitting it. */
export async function fillDraft(page: Page, kind: 'sales' | 'purchase', party: RegExp, lines: DraftLine[], warehouseCode = 'IST-01'): Promise<void> {
  const d = DRAFT[kind];
  await page.goto(d.path);
  await expect(page.getByRole('heading', { name: d.heading, level: 1 })).toBeVisible();
  const partySelect = page.getByLabel(d.party, { exact: true });
  await partySelect.selectOption((await partySelect.getByRole('option', { name: party }).getAttribute('value')) ?? '');
  const warehouse = page.getByLabel(d.warehouse, { exact: true });
  await warehouse.selectOption((await warehouse.getByRole('option', { name: new RegExp(`^${warehouseCode}`) }).getAttribute('value')) ?? '');
  for (const line of lines) {
    await addLine(page, line.sku);
    await page.getByLabel(`${line.sku} miktarı`).fill(String(line.quantity));
    if (line.unitCost !== undefined) {
      await page.getByLabel(`${line.sku} birim maliyeti (TRY)`).fill(line.unitCost);
    }
  }
}

/** Creates a draft sales or purchase order through the form and returns its id and number. */
export async function createDraft(page: Page, kind: 'sales' | 'purchase', party: RegExp, lines: DraftLine[], warehouseCode = 'IST-01'): Promise<CreatedOrder> {
  await fillDraft(page, kind, party, lines, warehouseCode);
  const created = page.waitForResponse((r) => r.url().endsWith(DRAFT[kind].api) && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Taslak siparişi oluştur' }).click();
  const response = await created;
  expect(response.status(), 'taslak sipariş oluşturma').toBe(201);
  const body = (await response.json()) as CreatedOrder;
  await expect(page.getByRole('heading', { level: 1 })).toContainText(body.number);
  return body;
}

/** Clicks a control and returns the response of the API call it triggers. */
export async function clickAndWait(page: Page, control: Locator, method: string, urlPart: string | RegExp) {
  const pending = page.waitForResponse((r) => r.request().method() === method && (typeof urlPart === 'string' ? r.url().includes(urlPart) : urlPart.test(r.url())));
  await control.click();
  return pending;
}

/** The terazi şeridi of a document: the bar is an image whose accessible name spells out the identity. */
export function balanceStrip(page: Page): Locator {
  return page.getByRole('img', { name: /^(Sipariş|Eldeki) [\d.]+ adet:/ });
}

/** The page head inside the content area (document number, title, status tag, meta). */
export function pageHead(page: Page): Locator {
  return page.getByRole('main').locator('header');
}

/**
 * Set-up shortcut: an approved purchase order for IST-01, created and approved through the API with
 * the given page's session (use satinalma.onay, whose approval limit covers it).
 */
export async function createApprovedPurchaseOrder(page: Page, lines: { sku: string; quantity: number; unitCost: number }[]): Promise<CreatedOrder> {
  const suppliers = await apiGet<{ id: string; code: string }[]>(page, '/api/suppliers?active=true');
  const supplier = suppliers.find((s) => s.code === 'TED-ANADOLU') ?? suppliers[0];
  expect(supplier, 'aktif tedarikçi').toBeDefined();
  const body = {
    supplierId: supplier?.id,
    warehouseId: await warehouseId(page, 'IST-01'),
    lines: await Promise.all(lines.map(async (line) => ({ productId: (await productBySku(page, line.sku)).id, quantity: line.quantity, unitCost: line.unitCost }))),
  };
  const created = await apiSend(page, 'POST', '/api/purchasing/orders', body);
  expect(created.status(), 'satın alma taslağı').toBe(201);
  const draft = (await created.json()) as CreatedOrder & { eTag: string };
  const approved = await apiSend(page, 'POST', `/api/purchasing/orders/${draft.id}/approve`, undefined, draft.eTag);
  expect(approved.status(), 'satın alma onayı').toBe(200);
  return { id: draft.id, number: draft.number };
}

/** Presses Tab until the given control has focus (keyboard-only navigation). */
export async function tabTo(page: Page, target: Locator, maxPresses = 80): Promise<void> {
  for (let i = 0; i < maxPresses; i += 1) {
    if ((await target.count()) > 0 && (await target.first().evaluate((el) => el === document.activeElement))) {
      return;
    }

    await page.keyboard.press('Tab');
  }

  throw new Error('Klavyeyle hedef denetime ulaşılamadı');
}

// ---------------------------------------------------------------- screenshots
/** Full-page screenshot with a stable name: <screen>-<width>.png in the artifact directory. */
export async function capture(page: Page, screen: string, width: number): Promise<void> {
  mkdirSync(ARTIFACT_DIR, { recursive: true });
  const mask = { mask: [page.locator('input[type="password"]')], maskColor: '#7C8A97', animations: 'disabled' as const };
  if (width <= 360) {
    // What the phone actually shows first, with the sticky action bar where it really sits.
    await page.screenshot({ path: path.join(ARTIFACT_DIR, `${screen}-${width}-ilk-ekran.png`), ...mask });
  }

  // In a full-page image a sticky bar would be painted across the middle of the page and hide what is
  // under it, so it is laid out in the normal flow for the capture (through the CSSOM; no style injection).
  const setSticky = (position: string) =>
    page.evaluate((value) => {
      for (const bar of document.querySelectorAll<HTMLElement>('.actions--sticky')) {
        bar.style.position = value;
      }
    }, position);
  await setSticky('static');
  await page.screenshot({ path: path.join(ARTIFACT_DIR, `${screen}-${width}.png`), fullPage: true, ...mask });
  await setSticky('');
}

/** No horizontal scrolling of the page itself at the current viewport. */
export async function expectNoPageOverflow(page: Page): Promise<void> {
  const sizes = await page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, client: document.documentElement.clientWidth }));
  expect(sizes.scroll, `yatay taşma: scrollWidth ${sizes.scroll} > clientWidth ${sizes.client}`).toBeLessThanOrEqual(sizes.client + 1);
}
