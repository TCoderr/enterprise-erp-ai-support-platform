import { expect, test } from '@playwright/test';
import { apiGet, clickAndWait, login, openSession } from './support/app';
import { DEMO_LABEL, ask, assistPanel, expectNoConfidence } from './support/assist';

// E. Destek ve Kaynaklı Yardım: talep → kaynaklı yanıt → açık onayla talebe kaydet → iç not → bilgi bankası erişimi.

interface Ticket {
  id: string;
  number: string;
  comments: { isInternal: boolean; source: string; body: string }[];
}

test('talepten sorulan soru kaynaklarıyla yanıtlanır ve yalnız açık onayla talebe öneri olarak kaydedilir', async ({ page, browser }) => {
  await login(page, 'satis1', '/destek/talepler/yeni');
  const title = `E2E ${Date.now().toString(36)}: sipariş onayında STK-409 hatası`;
  await page.getByLabel('Başlık').fill(title);
  await page.getByLabel('Açıklama').fill('Siparişi onaylamak istiyorum ama stok yetersiz diyor. Stok listesinde ürün görünüyor.');
  await page.getByLabel('Modül').selectOption('Sales');
  await page.getByLabel('Hata kodu (isteğe bağlı)').fill('stk-409');
  const created = await clickAndWait(page, page.getByRole('button', { name: 'Talebi aç' }), 'POST', '/api/support/tickets');
  expect(created.status()).toBe(201);
  const ticket = (await created.json()) as Ticket;
  await expect(page.getByRole('heading', { level: 1 })).toContainText(ticket.number);
  await expect(page.getByRole('main')).toContainText('STK-409');
  const thread = page.getByRole('region', { name: 'Yazışma' });
  await expect(thread).toContainText('Henüz yanıt yok.');

  // --- Kaynaklı Yardım, opened from the ticket.
  await page.getByRole('button', { name: 'Bu talep için Kaynaklı Yardım' }).click();
  const panel = assistPanel(page);
  await expect(panel).toBeVisible();
  await expect(panel.getByText(DEMO_LABEL)).toBeVisible();
  await expect(panel.getByText(/Talep bağlamı:/)).toContainText(ticket.number);
  await expect(panel.getByText(/Sorular kişisel veri içerebilir; soru metni saklanmaz\./)).toBeVisible();

  const response = await ask(page, 'STK-409 hatası ne anlama geliyor, ne yapmalıyım?');
  expect(response.status()).toBe(200);
  expect(response.request().postDataJSON()).toEqual({ question: 'STK-409 hatası ne anlama geliyor, ne yapmalıyım?', ticketId: ticket.id });
  const answerBody = (await response.json()) as { status: string; id?: string; citations: { title: string; versionNo: number; sectionNo: number; heading: string }[] };
  expect(answerBody.status).toBe('Answered');
  expect(answerBody).not.toHaveProperty('confidence');
  const citation = answerBody.citations[0];
  expect(citation).toBeDefined();

  const answer = panel.getByRole('region', { name: 'Yanıt', exact: true });
  await expect(answer).toBeVisible();
  // The provider label is part of the answer, next to its sources.
  await expect(answer.getByText(DEMO_LABEL)).toBeVisible();
  const sources = answer.getByRole('listitem').filter({ has: page.getByRole('link') });
  await expect(sources.first()).toContainText(citation?.title ?? '');
  await expect(sources.first()).toContainText(`sürüm ${citation?.versionNo} · bölüm ${citation?.sectionNo} — ${citation?.heading}`);
  // The retrieved passage is shown so the reader can compare it with the answer.
  await expect(sources.first().getByRole('blockquote')).toContainText(/STK-409/);
  await expectNoConfidence(answer);

  // --- Saving is a separate, explicit, two-step action.
  await panel.getByRole('button', { name: 'Talebe öneri olarak kaydet…' }).click();
  await expect(panel.getByText(/numaralı talebe/)).toContainText(ticket.number);
  await expect(panel.getByText(/numaralı talebe/)).toContainText(`AI önerisi · ${DEMO_LABEL}`);
  // Nothing is added before the confirmation.
  expect((await apiGet<Ticket>(page, `/api/support/tickets/${ticket.id}`)).comments).toHaveLength(0);
  await expect(thread).toContainText('Henüz yanıt yok.');

  const saved = await clickAndWait(page, panel.getByRole('button', { name: 'Öneriyi talebe kaydet' }), 'POST', `/api/support/tickets/${ticket.id}/comments/from-assist`);
  expect(saved.status()).toBe(201);
  expect(saved.request().postDataJSON()).toEqual({ answerId: answerBody.id });
  await expect(panel.getByText('Yanıt talebe öneri olarak kaydedildi')).toBeVisible();

  await expect(thread.getByText(`AI önerisi · ${DEMO_LABEL}`)).toBeVisible();
  await expect(thread.getByRole('list', { name: 'Kaynaklar' }).getByRole('link', { name: citation?.title ?? '' })).toBeVisible();
  await expect(thread).toContainText(`sürüm ${citation?.versionNo} · bölüm ${citation?.sectionNo}`);
  const stored = await apiGet<Ticket>(page, `/api/support/tickets/${ticket.id}`);
  expect(stored.comments.map((c) => c.source)).toEqual(['AiSuggestion']);

  // --- Support staff: the ticket is in the queue; an internal note stays internal.
  const support = await openSession(browser, 'destek1', '/destek/kuyruk');
  const sp = support.page;
  await expect(sp.getByRole('heading', { name: 'Talep kuyruğu', level: 1 })).toBeVisible();
  await sp.getByRole('link', { name: ticket.number, exact: true }).click();
  await expect(sp.getByRole('heading', { level: 1 })).toContainText(ticket.number);
  await expect(sp.getByRole('region', { name: 'Tanılama bilgisi' })).toContainText('STK-409');
  const internalNote = `E2E iç not ${Date.now().toString(36)}: müşteri temsilcisine stok rezervasyonu anlatılacak`;
  await sp.getByRole('textbox', { name: 'Yanıt yaz' }).fill(internalNote);
  await sp.getByLabel('İç not olarak kaydet (talep sahibi görmez)').check();
  const noted = await clickAndWait(sp, sp.getByRole('button', { name: 'İç notu kaydet' }), 'POST', `/api/support/tickets/${ticket.id}/comments`);
  expect(noted.status()).toBe(201);
  expect(noted.request().postDataJSON()).toEqual({ body: internalNote, isInternal: true });
  const staffThread = sp.getByRole('region', { name: 'Yazışma' });
  await expect(staffThread.getByText(internalNote)).toBeVisible();
  await expect(staffThread.getByText('İç not · yalnız destek görür')).toBeVisible();
  await support.context.close();

  // The requester never receives it — not in the page and not in the API response.
  await page.reload();
  await expect(page.getByRole('heading', { level: 1 })).toContainText(ticket.number);
  await expect(page.getByRole('region', { name: 'Yazışma' }).getByText(`AI önerisi · ${DEMO_LABEL}`)).toBeVisible();
  await expect(page.locator('body')).not.toContainText(internalNote);
  await expect(page.getByText('İç not · yalnız destek görür')).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Tanılama bilgisi' })).toHaveCount(0);
  const asRequester = await apiGet<Ticket>(page, `/api/support/tickets/${ticket.id}`);
  expect(asRequester.comments.some((c) => c.isInternal)).toBe(false);
  expect(JSON.stringify(asRequester)).not.toContain(internalNote);
});

test('bilgi bankası makaleleri görünürlük, taslak ve depo kapsamına göre açılır', async ({ page, browser }) => {
  const dialogs: string[] = [];
  page.on('dialog', (dialog) => {
    dialogs.push(dialog.message());
    void dialog.dismiss();
  });

  // satis1 reads a general article, rendered as text only.
  await login(page, 'satis1', '/bilgi-bankasi/makale/hata-stk-409');
  await expect(page.getByRole('heading', { name: 'STK-409: Yeterli kullanılabilir stok yok', level: 1 })).toBeVisible();
  const body = page.getByRole('region', { name: 'Makale metni' });
  await expect(body).toContainText('STK-409');
  await expect(body.getByRole('link')).toHaveCount(0);
  await expect(body.locator('script, iframe, img')).toHaveCount(0);

  for (const slug of ['destek-stok-farki-incelemesi', 'yonetici-kalan-iptal-kararlari', 'taslak-iade-sureci']) {
    const response = page.waitForResponse((r) => r.url().endsWith(`/api/kb/articles/${slug}`));
    await page.goto(`/bilgi-bankasi/makale/${slug}`);
    expect((await response).status(), slug).toBe(404);
    await expect(page.getByRole('heading', { name: 'Makale bulunamadı veya erişiminiz yok' })).toBeVisible();
  }

  // The list offers only what can be read.
  await page.goto('/bilgi-bankasi');
  await expect(page.getByRole('link', { name: 'STK-409: Yeterli kullanılabilir stok yok' })).toBeVisible();
  await expect(page.getByRole('main')).not.toContainText('Destek personeli:');
  await expect(page.getByRole('main')).not.toContainText('Yönetici notu');
  await expect(page.getByRole('main')).not.toContainText('Taslak:');

  // Support staff read the SupportOnly article; the draft is not an article even for them.
  const support = await openSession(browser, 'destek1', '/bilgi-bankasi/makale/destek-stok-farki-incelemesi');
  await expect(support.page.getByRole('heading', { name: /Destek personeli: stok farkı/, level: 1 })).toBeVisible();
  await support.page.goto('/bilgi-bankasi/makale/taslak-iade-sureci');
  await expect(support.page.getByRole('heading', { name: 'Makale bulunamadı veya erişiminiz yok' })).toBeVisible();
  await support.context.close();

  // Warehouse scope: the IST-01 article is readable with an IST-01 grant only.
  const izmir = await openSession(browser, 'depo.izmir', '/bilgi-bankasi/makale/ist-01-sevk-rampasi');
  await expect(izmir.page.getByRole('heading', { name: 'Makale bulunamadı veya erişiminiz yok' })).toBeVisible();
  expect((await izmir.page.request.get('/api/kb/articles/ist-01-sevk-rampasi')).status()).toBe(404);
  await izmir.context.close();
  const istanbul = await openSession(browser, 'depo1', '/bilgi-bankasi/makale/ist-01-sevk-rampasi');
  await expect(istanbul.page.getByRole('heading', { name: /İstanbul Merkez: sevk rampası/, level: 1 })).toBeVisible();
  await istanbul.context.close();

  expect(dialogs).toEqual([]);
});
