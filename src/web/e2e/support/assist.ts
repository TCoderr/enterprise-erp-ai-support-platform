import { expect } from '@playwright/test';
import type { Locator, Page, Response } from '@playwright/test';

/** The Kaynaklı Yardım panel: a side column on wide screens, a modal sheet on narrow ones. */
export function assistPanel(page: Page): Locator {
  return page.getByRole('complementary', { name: 'Kaynaklı Yardım' }).or(page.getByRole('dialog', { name: 'Kaynaklı Yardım' }));
}

export const DEMO_LABEL = 'Demo sağlayıcı — dil modeli değil';

/** Types a question and asks; resolves with the API response the page received. */
export async function ask(page: Page, question: string): Promise<Response> {
  const panel = assistPanel(page);
  await panel.getByLabel('Sorunuz').fill(question);
  const pending = page.waitForResponse((r) => r.url().endsWith('/api/assist/ask') && r.request().method() === 'POST');
  await panel.getByRole('button', { name: 'Kaynaklarda ara ve yanıtla' }).click();
  return pending;
}

/** Asserts the answer block carries no confidence figure of any kind. */
export async function expectNoConfidence(region: Locator): Promise<void> {
  const text = (await region.innerText()).toLocaleLowerCase('tr-TR');
  expect(text).not.toMatch(/%|güven|confidence|puan|skor|★/);
  await expect(region.getByRole('meter')).toHaveCount(0);
  await expect(region.getByRole('progressbar')).toHaveCount(0);
}
