import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { AssistAnswer, AssistSource, ProviderInfo } from '../api/types';
import { useAssist } from '../shell/context';
import type { AssistRequest } from '../shell/context';
import { json, mockApi, problem, renderScreen } from '../test/helpers';
import type { ApiDouble } from '../test/helpers';
import { AssistPanel, providerLabel } from './AssistPanel';

const DEMO: ProviderInfo = { kind: 'demo', isDemo: true };
const LOCAL: ProviderInfo = { kind: 'local-model', model: 'llama3.1:8b', isDemo: false };
const DISABLED: ProviderInfo = { kind: 'disabled', isDemo: false };

const SOURCES: AssistSource[] = [
  {
    documentSlug: 'hata-stk-409',
    title: 'Hata kodları: STK-409 — Yetersiz veya aşılan miktar',
    versionNo: 3,
    sectionNo: 2,
    heading: 'Sevkiyatta açık miktarı aşma',
    snippet: 'Sevk miktarı, satırın açık miktarından büyük olamaz. Sistem isteğin tamamını reddeder.',
  },
  {
    documentSlug: 'kismi-sevkiyat',
    title: 'Kısmi sevkiyat prosedürü',
    versionNo: 5,
    sectionNo: 4,
    heading: 'Kalan miktarın takibi',
    snippet: 'Her sevkiyattan sonra kalan miktar siparişte açık olarak görünür.',
  },
];

const citations = SOURCES.map(({ snippet: _snippet, ...citation }) => citation);

function answered(overrides: Partial<AssistAnswer> = {}): AssistAnswer {
  return {
    id: 'a1b2c3d4-0000-7000-8000-0000000000aa',
    status: 'Answered',
    answer: '**STK-409**, sevk miktarının satırın `açık` miktarını aştığını gösterir.\n\n- Açık sütununu kontrol edin',
    citations,
    sources: SOURCES,
    limitations: 'Yanıt iki kaynaktan derlendi.',
    provider: DEMO,
    expiresAt: '2026-10-10T09:36:00Z',
    ...overrides,
  };
}

function Harness({ request }: { request?: AssistRequest }) {
  const assist = useAssist();
  return (
    <>
      <button type="button" onClick={() => assist.open(request)}>
        paneli aç
      </button>
      {assist.isOpen && <AssistPanel key={assist.session} modal={false} />}
    </>
  );
}

const TICKET = { id: 't1', number: 'TK-00000093' };
const SAVE_PATH = '/api/support/tickets/t1/comments/from-assist';

async function openPanel(api: ApiDouble, request?: AssistRequest, provider: ProviderInfo = DEMO) {
  api.on('GET', '/api/assist/status', () => json(provider));
  const user = userEvent.setup();
  renderScreen(<Harness {...(request !== undefined ? { request } : {})} />);
  await user.click(screen.getByRole('button', { name: 'paneli aç' }));
  const panel = await screen.findByRole('complementary', { name: 'Kaynaklı Yardım' });
  return { user, panel };
}

async function ask(user: ReturnType<typeof userEvent.setup>, question = 'STK-409 hatası ne anlama geliyor?') {
  const box = screen.getByLabelText('Sorunuz');
  await user.clear(box);
  await user.type(box, question);
  await user.click(screen.getByRole('button', { name: 'Kaynaklarda ara ve yanıtla' }));
}

describe('sağlayıcı etiketi', () => {
  it('demo sağlayıcıyı tam olarak "Demo sağlayıcı — dil modeli değil" diye, soru sorulmadan da gösterir', async () => {
    const api = mockApi();
    const { panel } = await openPanel(api);
    expect(await within(panel).findByText('Demo sağlayıcı — dil modeli değil')).toBeInTheDocument();
    expect(providerLabel(DEMO)).toBe('Demo sağlayıcı — dil modeli değil');
  });

  it('yerel modelde model adını gösterir', async () => {
    const api = mockApi();
    const { panel } = await openPanel(api, undefined, LOCAL);
    expect(await within(panel).findByText('Yerel model: llama3.1:8b')).toBeInTheDocument();
    expect(within(panel).queryByText(/Demo sağlayıcı/)).toBeNull();
  });

  it('kapalı sağlayıcıda bunu açıkça söyler; kaynak araması yine çalışır', async () => {
    const api = mockApi([{ method: 'POST', path: '/api/assist/search', reply: () => json(SOURCES) }]);
    const { user, panel } = await openPanel(api, undefined, DISABLED);

    expect(await within(panel).findByText('Yanıt üretimi kapalı — yalnız kaynak araması')).toBeInTheDocument();
    expect(within(panel).getByText(/Yanıt üretimi bu kurulumda kapalı/)).toBeInTheDocument();

    await user.type(screen.getByLabelText('Sorunuz'), 'kısmi sevkiyat');
    await user.click(screen.getByRole('button', { name: 'Yalnız kaynak ara' }));

    expect(await within(panel).findByRole('heading', { name: 'Kaynak araması' })).toBeInTheDocument();
    expect(within(panel).getByRole('link', { name: 'Kısmi sevkiyat prosedürü' })).toHaveAttribute('href', '/bilgi-bankasi/makale/kismi-sevkiyat?yardim=1');
    expect(api.callsTo('POST', '/api/assist/search')[0]?.body).toEqual({ query: 'kısmi sevkiyat' });
    expect(api.callsTo('POST', '/api/assist/ask')).toHaveLength(0);
  });
});

describe('yanıt durumları', () => {
  it('Answered: yanıtı, sağlayıcı etiketini ve her atfı belge · sürüm · bölüm — başlık ve alıntısıyla gösterir', async () => {
    const api = mockApi([{ method: 'POST', path: '/api/assist/ask', reply: () => json(answered()) }]);
    const { user, panel } = await openPanel(api);
    await ask(user);

    const answer = await within(panel).findByRole('region', { name: 'Yanıt' });
    expect(within(answer).getByText('Demo sağlayıcı — dil modeli değil')).toBeInTheDocument();
    expect(within(answer).getByText('STK-409', { selector: 'strong' })).toBeInTheDocument();
    expect(within(answer).getByText('açık', { selector: 'code' })).toBeInTheDocument();

    const items = within(answer).getAllByRole('listitem').filter((li) => li.closest('.sources') !== null);
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('Hata kodları: STK-409 — Yetersiz veya aşılan miktar');
    expect(items[0]).toHaveTextContent('sürüm 3 · bölüm 2 — Sevkiyatta açık miktarı aşma');
    expect(items[0]).toHaveTextContent('Sevk miktarı, satırın açık miktarından büyük olamaz.');
    expect(items[1]).toHaveTextContent('sürüm 5 · bölüm 4 — Kalan miktarın takibi');
    expect(within(answer).getByText(/Yanıt iki kaynaktan derlendi\./)).toBeInTheDocument();

    expect(api.callsTo('POST', '/api/assist/ask')[0]?.body).toEqual({ question: 'STK-409 hatası ne anlama geliyor?' });
  });

  it('güven puanı, yüzde ya da yıldız göstermez', async () => {
    const api = mockApi([{ method: 'POST', path: '/api/assist/ask', reply: () => json({ ...answered(), confidence: 0.93, score: 93 }) }]);
    const { user, panel } = await openPanel(api);
    await ask(user);

    const answer = await within(panel).findByRole('region', { name: 'Yanıt' });
    expect(answer.textContent).not.toMatch(/%|güven|confidence|puan|skor|0[.,]93|\b93\b|★/i);
    expect(within(panel).queryByRole('meter')).toBeNull();
    expect(within(panel).queryByRole('progressbar')).toBeNull();
  });

  it('yanıttaki HTML ve bağlantıyı çalıştırmaz, düz metin olarak gösterir', async () => {
    const hostile = answered({ answer: 'Bakın <script>alert(1)</script> ve [tıklayın](javascript:alert(2)) ya da https://evil.test/x' });
    const api = mockApi([{ method: 'POST', path: '/api/assist/ask', reply: () => json(hostile) }]);
    const { user, panel } = await openPanel(api);
    await ask(user);

    const answer = await within(panel).findByRole('region', { name: 'Yanıt' });
    const text = answer.querySelector('.answer__text');
    expect(text?.querySelector('script, a, img')).toBeNull();
    expect(text).toHaveTextContent('<script>alert(1)</script>');
    expect(text).toHaveTextContent('tıklayın (javascript:alert(2)');
    expect(text).toHaveTextContent('https://evil.test/x');
  });

  it('NoSource: "Yeterli yetkili kaynak yok" durumunu hata biçimi olmadan gösterir', async () => {
    const noSource: AssistAnswer = {
      status: 'NoSource',
      citations: [],
      sources: [],
      limitations: 'Bu soruyu yanıtlamak için erişebildiğiniz yayınlı kaynaklarda yeterli bilgi yok.',
      provider: DEMO,
    };
    const api = mockApi([{ method: 'POST', path: '/api/assist/ask', reply: () => json(noSource) }]);
    const { user, panel } = await openPanel(api);
    await ask(user, 'Kargo firması hangisi?');

    const region = await within(panel).findByRole('region', { name: 'Yeterli yetkili kaynak yok' });
    expect(region).toHaveTextContent('erişebildiğiniz yayınlı kaynaklarda yeterli bilgi yok');
    expect(region).toHaveTextContent('Yanıt üretilmedi');
    expect(within(panel).queryByRole('alert')).toBeNull();
    expect(panel.querySelector('.alert--danger')).toBeNull();
    expect(region.querySelector('.alert--info')).not.toBeNull();
    expect(within(region).getByRole('link', { name: 'Destek talebi aç' })).toBeInTheDocument();
    expect(within(panel).queryByRole('button', { name: /Talebe öneri olarak kaydet/ })).toBeNull();
  });

  it('ProviderUnavailable: nedeni açıklar ve dönen kaynakları okunabilsin diye listeler', async () => {
    const unavailable: AssistAnswer = {
      status: 'ProviderUnavailable',
      citations: [],
      sources: SOURCES,
      limitations: 'Yanıt üretici şu anda kullanılamıyor. Aşağıdaki kaynakları doğrudan okuyabilirsiniz.',
      provider: LOCAL,
    };
    const api = mockApi([{ method: 'POST', path: '/api/assist/ask', reply: () => json(unavailable) }]);
    const { user, panel } = await openPanel(api, undefined, LOCAL);
    await ask(user);

    const region = await within(panel).findByRole('region', { name: 'Yanıt şu anda üretilemiyor' });
    expect(region).toHaveTextContent('Yanıt üretici şu anda kullanılamıyor.');
    expect(region).toHaveTextContent('Sorunuzla eşleşen 2 kaynak bulundu');
    expect(region).toHaveTextContent('Sipariş, stok ve destek işlemleri bundan etkilenmez.');
    expect(within(region).getByRole('link', { name: 'Hata kodları: STK-409 — Yetersiz veya aşılan miktar' })).toBeInTheDocument();
    expect(within(region).getByText(/Her sevkiyattan sonra kalan miktar/)).toBeInTheDocument();
    expect(panel.querySelector('.alert--danger')).toBeNull();
  });

  it('RejectedUnsupported: doğrulanamayan metni göstermez', async () => {
    const rejected: AssistAnswer = {
      status: 'RejectedUnsupported',
      citations: [],
      sources: [],
      limitations: 'Üretilen yanıt kaynaklarla doğrulanamadığı için gösterilmiyor.',
      provider: LOCAL,
    };
    const api = mockApi([{ method: 'POST', path: '/api/assist/ask', reply: () => json(rejected) }]);
    const { user, panel } = await openPanel(api, undefined, LOCAL);
    await ask(user);

    const region = await within(panel).findByRole('region', { name: 'Yanıt gösterilmiyor' });
    expect(region).toHaveTextContent('Üretilen yanıt kaynaklarla doğrulanamadığı için gösterilmiyor.');
    expect(region.querySelector('.answer__text')).toBeNull();
    expect(region.querySelector('.sources')).toBeNull();
  });

  it('AccessChanged: erişim değiştiği için metnin teslim edilmediğini söyler', async () => {
    const changed: AssistAnswer = {
      status: 'AccessChanged',
      citations: [],
      sources: [],
      limitations: 'Yanıt hazırlanırken erişim yetkiniz veya kaynağın yayın durumu değişti; yanıt gösterilmiyor.',
      provider: DEMO,
    };
    const api = mockApi([{ method: 'POST', path: '/api/assist/ask', reply: () => json(changed) }]);
    const { user, panel } = await openPanel(api);
    await ask(user);

    const region = await within(panel).findByRole('region', { name: 'Erişim değişti' });
    expect(region).toHaveTextContent('erişim yetkiniz veya kaynağın yayın durumu değişti');
    expect(region).toHaveTextContent('Üretilen metin teslim edilmedi');
    expect(region.querySelector('.answer__text')).toBeNull();
  });
});

describe('soru alanı', () => {
  it('3 karakterden kısa soruyu göndermez ve alanı işaretler', async () => {
    const api = mockApi();
    const { user } = await openPanel(api);
    await ask(user, 'ab');

    const box = screen.getByLabelText('Sorunuz');
    expect(box).toHaveAttribute('aria-invalid', 'true');
    expect(box).toHaveAccessibleDescription(/Soru 3–1000 karakter olmalıdır\./);
    expect(box).toHaveFocus();
    expect(api.callsTo('POST', '/api/assist/ask')).toHaveLength(0);
  });

  it('kişisel veri ve saklanmama notunu ve karakter sayacını gösterir; hata kodundan açıldığında soruyu hazır getirir', async () => {
    const api = mockApi();
    const { panel } = await openPanel(api, { prefill: 'STK-409 ne anlama geliyor?' });
    const box = within(panel).getByLabelText('Sorunuz');
    expect(box).toHaveValue('STK-409 ne anlama geliyor?');
    expect(box).toHaveAttribute('maxlength', '1000');
    expect(box).toHaveAccessibleDescription(/Sorular kişisel veri içerebilir; soru metni saklanmaz\./);
    expect(within(panel).getByText('26 / 1000')).toBeInTheDocument();
  });

  it('hız sınırında (429) bekleme süresini söyler', async () => {
    const api = mockApi([{ method: 'POST', path: '/api/assist/ask', reply: () => problem(429, 'rate_limited', { title: 'Çok fazla istek gönderildi', headers: { 'Retry-After': '60' } }) }]);
    const { user, panel } = await openPanel(api);
    await ask(user);

    const alert = await within(panel).findByRole('alert');
    expect(alert).toHaveTextContent('Soru yanıtlanamadı');
    expect(alert).toHaveTextContent('60 saniye sonra yeniden deneyin.');
  });

  it('panel kapanınca yanıtı ve soruyu bırakır', async () => {
    const api = mockApi([{ method: 'POST', path: '/api/assist/ask', reply: () => json(answered()) }]);
    const { user, panel } = await openPanel(api);
    await ask(user);
    await within(panel).findByRole('region', { name: 'Yanıt' });

    await user.click(within(panel).getByRole('button', { name: 'Kaynaklı Yardım panelini kapat' }));
    expect(screen.queryByRole('complementary', { name: 'Kaynaklı Yardım' })).toBeNull();

    await user.click(screen.getByRole('button', { name: 'paneli aç' }));
    const reopened = await screen.findByRole('complementary', { name: 'Kaynaklı Yardım' });
    expect(within(reopened).queryByRole('region', { name: 'Yanıt' })).toBeNull();
    expect(within(reopened).getByLabelText('Sorunuz')).toHaveValue('');
    expect(window.localStorage.length + window.sessionStorage.length).toBe(0);
  });
});

describe('talebe öneri olarak kaydetme', () => {
  it('talep bağlamı yokken kaydetme düğmesini göstermez', async () => {
    const api = mockApi([{ method: 'POST', path: '/api/assist/ask', reply: () => json(answered()) }]);
    const { user, panel } = await openPanel(api);
    await ask(user);
    await within(panel).findByRole('region', { name: 'Yanıt' });
    expect(within(panel).queryByRole('button', { name: /Talebe öneri olarak kaydet/ })).toBeNull();
  });

  it('kimliği olmayan yanıtta, talep bağlamı olsa bile kaydetme düğmesini göstermez', async () => {
    const withoutId = answered();
    delete withoutId.id;
    const api = mockApi([{ method: 'POST', path: '/api/assist/ask', reply: () => json(withoutId) }]);
    const { user, panel } = await openPanel(api, { ticket: TICKET });
    await ask(user);
    await within(panel).findByRole('region', { name: 'Yanıt' });
    expect(within(panel).queryByRole('button', { name: /Talebe öneri olarak kaydet/ })).toBeNull();
  });

  it('açık onay adımından sonra yalnız yanıt kimliğini gönderir ve sonucu sunucudan yazar', async () => {
    const api = mockApi([
      { method: 'POST', path: '/api/assist/ask', reply: () => json(answered()) },
      { method: 'POST', path: SAVE_PATH, reply: () => json({ id: 't1', number: 'TK-00000093', comments: [] }, { status: 201 }) },
    ]);
    const { user, panel } = await openPanel(api, { ticket: TICKET });

    expect(within(panel).getByText(/Talep bağlamı:/)).toHaveTextContent('TK-00000093');
    await ask(user);
    expect(api.callsTo('POST', '/api/assist/ask')[0]?.body).toEqual({ question: 'STK-409 hatası ne anlama geliyor?', ticketId: 't1' });

    await user.click(await within(panel).findByRole('button', { name: 'Talebe öneri olarak kaydet…' }));

    // Step one only explains what will happen; nothing has been sent.
    const confirm = within(panel).getByText(/numaralı talebe/).closest('form');
    expect(confirm).not.toBeNull();
    expect(confirm).toHaveTextContent('Bu yanıt ve 2 kaynağı, TK-00000093 numaralı talebe');
    expect(confirm).toHaveTextContent('AI önerisi · Demo sağlayıcı — dil modeli değil');
    expect(api.callsTo('POST', SAVE_PATH)).toHaveLength(0);

    await user.click(within(confirm as HTMLElement).getByRole('button', { name: 'Öneriyi talebe kaydet' }));

    expect(await within(panel).findByText('Yanıt talebe öneri olarak kaydedildi')).toBeInTheDocument();
    const call = api.callsTo('POST', SAVE_PATH)[0];
    expect(call?.body).toEqual({ answerId: 'a1b2c3d4-0000-7000-8000-0000000000aa' });
    expect(call?.headers['idempotency-key']).toMatch(/^[A-Za-z0-9_-]{16,128}$/);
    expect(call?.headers['if-match']).toBeUndefined();
    expect(within(panel).queryByRole('button', { name: 'Talebe öneri olarak kaydet…' })).toBeNull();
  });

  it('onay adımında vazgeçilirse hiçbir şey göndermez', async () => {
    const api = mockApi([{ method: 'POST', path: '/api/assist/ask', reply: () => json(answered()) }]);
    const { user, panel } = await openPanel(api, { ticket: TICKET });
    await ask(user);

    const trigger = await within(panel).findByRole('button', { name: 'Talebe öneri olarak kaydet…' });
    await user.click(trigger);
    await user.click(within(panel).getByRole('button', { name: 'Vazgeç' }));

    expect(within(panel).queryByRole('button', { name: 'Öneriyi talebe kaydet' })).toBeNull();
    expect(trigger).toHaveFocus();
    expect(api.callsTo('POST', SAVE_PATH)).toHaveLength(0);
  });

  it('409 source_no_longer_available yanıtında kaydedilmediğini ve nedenini söyler', async () => {
    const api = mockApi([
      { method: 'POST', path: '/api/assist/ask', reply: () => json(answered()) },
      {
        method: 'POST',
        path: SAVE_PATH,
        reply: () =>
          problem(409, 'source_no_longer_available', {
            title: 'Kaynak artık kullanılamıyor',
            detail: 'Yanıtın dayandığı kaynaklardan en az biri artık yayında değil veya erişiminiz kalmadı.',
          }),
      },
    ]);
    const { user, panel } = await openPanel(api, { ticket: TICKET });
    await ask(user);
    await user.click(await within(panel).findByRole('button', { name: 'Talebe öneri olarak kaydet…' }));
    await user.click(within(panel).getByRole('button', { name: 'Öneriyi talebe kaydet' }));

    expect(await within(panel).findByRole('heading', { name: 'Kaydedilemedi: kaynak artık kullanılamıyor' })).toBeInTheDocument();
    expect(panel).toHaveTextContent('Talebe hiçbir şey eklenmedi.');
    expect(panel).toHaveTextContent('0192f0c1a2b37c4d8e9f000000000001');
    expect(within(panel).queryByText('Yanıt talebe öneri olarak kaydedildi')).toBeNull();
    await waitFor(() => expect(api.callsTo('POST', SAVE_PATH)).toHaveLength(1));
  });
});
