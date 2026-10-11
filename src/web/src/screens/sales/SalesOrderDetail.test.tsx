import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { SalesOrder } from '../../api/types';
import { ANKARA, json, makeMe, mockApi, problem, renderScreen } from '../../test/helpers';
import { SalesOrderDetailScreen } from './SalesOrderDetail';

const OWNER = makeMe({ roles: ['SalesAgent'], warehouses: [{ ...ANKARA, level: 'View' }] });
const OPERATOR = makeMe({ id: 'cccccccc-0000-7000-8000-00000000000c', displayName: 'Emre Koç', roles: ['WarehouseOperator'], warehouses: [{ ...ANKARA, level: 'Operate' }] });
const PATH = '/api/sales/orders/o1';

function order(overrides: Partial<SalesOrder> = {}): SalesOrder {
  return {
    id: 'o1',
    number: 'SO-00001042',
    status: 'Draft',
    view: 'Full',
    customerId: 'c1',
    customerCode: 'M-0418',
    customerName: 'Yıldırım Elektrik Malzemeleri Ltd. Şti.',
    customerTaxNumber: '1234567890',
    shippingAddress: 'Ostim OSB 1203. Sokak No: 14, Yenimahalle / Ankara',
    warehouseId: ANKARA.id,
    ownerUserId: OWNER.id,
    ownerDisplayName: 'Deniz Aksoy',
    total: 199760,
    createdAt: '2026-10-05T13:20:00Z',
    lines: [
      { id: 'l1', lineNo: 1, productId: 'p1', sku: 'ELK-NYM-3X25', productName: 'NYM Kablo 3×2,5 mm² (100 m top)', unit: 'TOP', unitPrice: 4850, lineTotal: 194000, orderedQty: 40, shippedQty: 0, cancelledQty: 0, openQty: 40 },
      { id: 'l2', lineNo: 2, productId: 'p2', sku: 'HRD-DBL-8X40', productName: 'Çelik Dübel 8×40 (100’lü kutu)', unit: 'KOLI', unitPrice: 96, lineTotal: 5760, orderedQty: 60, shippedQty: 0, cancelledQty: 0, openQty: 60 },
    ],
    shipments: [],
    eTag: '"AAAAAAAAB9E="',
    ...overrides,
  };
}

function show(me = OWNER) {
  return renderScreen(<SalesOrderDetailScreen />, { me, path: '/satis/siparisler/:id', url: '/satis/siparisler/o1' });
}

describe('satış siparişi belgesi', () => {
  it('ticari görünümde plaka, durum, terazi şeridi, fiyatlar ve toplamı gösterir', async () => {
    mockApi([{ method: 'GET', path: PATH, reply: () => json(order()) }]);
    show();

    expect(await screen.findByRole('heading', { level: 1, name: /SO-00001042/ })).toBeInTheDocument();
    expect(screen.getByText('Taslak', { selector: '.tag' })).toBeInTheDocument();
    expect(screen.getByRole('img', { name: 'Sipariş 100 adet: sevk edilen 0, iptal 0, açık 100.' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Birim fiyat' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Tutar' })).toBeInTheDocument();
    const firstRow = screen.getByRole('row', { name: /ELK-NYM-3X25/ });
    expect(within(firstRow).getByText('₺4.850,00')).toBeInTheDocument();
    expect(screen.getByText('₺199.760,00')).toBeInTheDocument();
    expect(screen.getByText('Sahip').nextElementSibling).toHaveTextContent('Deniz Aksoy (siz)');
    expect(document.title).toBe('SO-00001042 · Satış siparişi — Kantar');
  });

  it('depo görünümünde fiyat sütunları ve toplam hiç yer almaz; sevkiyat girişi sunulur', async () => {
    const fulfilment = order({ status: 'Confirmed', view: 'Fulfilment', confirmedAt: '2026-10-05T13:42:00Z' });
    delete fulfilment.total;
    delete fulfilment.customerTaxNumber;
    fulfilment.lines = fulfilment.lines.map(({ unitPrice: _price, lineTotal: _total, ...line }) => line);
    mockApi([{ method: 'GET', path: PATH, reply: () => json(fulfilment) }]);
    show(OPERATOR);

    expect(await screen.findByRole('heading', { level: 1, name: /SO-00001042/ })).toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: 'Birim fiyat' })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: 'Tutar' })).toBeNull();
    expect(document.body.textContent).not.toContain('₺');
    expect(screen.queryByText('Vergi no')).toBeNull();
    expect(screen.getByRole('columnheader', { name: 'Açık' })).toBeInTheDocument();
    expect(screen.getByText(/Depo görünümü: fiyat ve ticari notlar gösterilmez/)).toBeInTheDocument();

    expect(screen.getByRole('link', { name: 'Sevkiyat gir' })).toHaveAttribute('href', '/depo/sevk/o1');
    expect(screen.queryByRole('button', { name: 'Siparişi onayla' })).toBeNull();
    expect(screen.queryByRole('button', { name: /iptal et/ })).toBeNull();
  });

  it('onay isteğinde belgenin ETag değerini If-Match olarak ve bir Idempotency-Key gönderir; sonucu sunucudan yazar', async () => {
    const user = userEvent.setup();
    const confirmed = order({ status: 'Confirmed', confirmedAt: '2026-10-05T13:42:00Z', eTag: '"AAAAAAAAB9I="' });
    const api = mockApi([
      { method: 'GET', path: PATH, reply: () => json(order()) },
      { method: 'POST', path: `${PATH}/confirm`, reply: () => json(confirmed, { headers: { ETag: confirmed.eTag } }) },
      { method: 'GET', path: '/api/sales/orders', reply: () => json({ items: [] }) },
    ]);
    show();

    await user.click(await screen.findByRole('button', { name: 'Siparişi onayla' }));

    expect(await screen.findByText('Sipariş onaylandı')).toBeInTheDocument();
    expect(screen.getByText('100 adet bu sipariş için rezerve edildi.')).toBeInTheDocument();
    expect(screen.getByText('Onaylandı', { selector: '.tag' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Siparişi onayla' })).toBeNull();

    const call = api.callsTo('POST', `${PATH}/confirm`)[0];
    expect(call?.headers['if-match']).toBe('"AAAAAAAAB9E="');
    expect(call?.headers['idempotency-key']).toMatch(/^[A-Za-z0-9_-]{16,128}$/);
    expect(call?.headers['x-csrf-token']).toBe('csrf-anon');
    expect(call?.body).toBeUndefined();
  });

  it('412 yanıtında "belge değişti" durumunu gösterir, yeniden yükler ve yazılan iptal gerekçesini korur', async () => {
    const user = userEvent.setup();
    let current = order({ status: 'Confirmed', confirmedAt: '2026-10-05T13:42:00Z' });
    let cancelCalls = 0;
    const api = mockApi([
      { method: 'GET', path: PATH, reply: () => json(current) },
      {
        method: 'POST',
        path: `${PATH}/cancel`,
        reply: () => {
          cancelCalls += 1;
          if (cancelCalls === 1) {
            // Someone shipped part of the order in the meantime.
            current = order({
              status: 'PartiallyShipped',
              confirmedAt: '2026-10-05T13:42:00Z',
              eTag: '"AAAAAAAAB9Q="',
              lines: current.lines.map((l) => (l.id === 'l1' ? { ...l, shippedQty: 25, openQty: 15 } : l)),
              shipments: [{ id: 's1', number: 'SH-00000311', dispatchReference: 'IRS-2026-0871', createdAt: '2026-10-06T08:27:00Z', totalQuantity: 25 }],
            });
            return problem(412, 'precondition_failed', { title: 'Kayıt siz görüntüledikten sonra değişti', userCode: 'SIP-412' });
          }

          return json(order({ status: 'Cancelled', eTag: '"AAAAAAAAB9U="' }));
        },
      },
      { method: 'GET', path: '/api/sales/orders', reply: () => json({ items: [] }) },
    ]);
    show();

    await user.click(await screen.findByRole('button', { name: 'Siparişi iptal et…' }));
    const reason = screen.getByLabelText('İptal gerekçesi');
    expect(reason).toHaveFocus();
    await user.type(reason, 'Müşteri siparişten vazgeçti');
    await user.click(screen.getByRole('button', { name: 'Siparişi iptal et' }));

    const stale = await screen.findByRole('alert');
    expect(within(stale).getByRole('heading', { name: 'Bu sipariş siz görüntüledikten sonra değişti' })).toBeInTheDocument();
    expect(stale).toHaveTextContent('İşleminiz uygulanmadı');
    expect(stale).toHaveTextContent('SIP-412');
    expect(api.callsTo('GET', PATH)).toHaveLength(1);

    await user.click(within(stale).getByRole('button', { name: /Güncel sipariş bilgisini yükle/ }));

    // The reloaded document is shown, and the unsent reason is still in the form.
    expect(await screen.findByText('Kısmen sevk edildi', { selector: '.tag' })).toBeInTheDocument();
    expect(api.callsTo('GET', PATH)).toHaveLength(2);
    expect(screen.queryByRole('heading', { name: 'Bu sipariş siz görüntüledikten sonra değişti' })).toBeNull();
    expect(screen.getByLabelText('İptal gerekçesi')).toHaveValue('Müşteri siparişten vazgeçti');
    expect(screen.getByText(/Kalan 75 adet iptal edilsin mi\?/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Kalanı iptal et' }));
    await waitFor(() => expect(api.callsTo('POST', `${PATH}/cancel`)).toHaveLength(2));
    const [first, second] = api.callsTo('POST', `${PATH}/cancel`);
    expect(first?.headers['if-match']).toBe('"AAAAAAAAB9E="');
    expect(second?.headers['if-match']).toBe('"AAAAAAAAB9Q="');
    expect(second?.body).toEqual({ reason: 'Müşteri siparişten vazgeçti' });
  });

  it('409 insufficient_available yanıtını satırlara eşler: istenen ve kullanılabilir miktar ilgili satırda görünür', async () => {
    const user = userEvent.setup();
    mockApi([
      { method: 'GET', path: PATH, reply: () => json(order()) },
      {
        method: 'POST',
        path: `${PATH}/confirm`,
        reply: () =>
          problem(409, 'insufficient_available', {
            title: 'Yeterli kullanılabilir stok yok',
            userCode: 'STK-409',
            // The server addresses the line by its zero-based position: lines[1] is the second line.
            errors: [{ field: 'lines[1]', code: 'insufficient_available', detail: 'İstenen 60, kullanılabilir 12' }],
          }),
      },
    ]);
    show();

    await user.click(await screen.findByRole('button', { name: 'Siparişi onayla' }));

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByRole('heading', { name: 'Sipariş onaylanamadı: 1 satırda stok yetersiz' })).toBeInTheDocument();
    expect(alert).toHaveTextContent('HRD-DBL-8X40');
    expect(alert).toHaveTextContent('İstenen 60, kullanılabilir 12');
    expect(alert).toHaveTextContent('Hiçbir satır rezerve edilmedi');
    expect(alert).toHaveTextContent('STK-409');
    expect(alert).toHaveTextContent('0192f0c1a2b37c4d8e9f000000000001');
    expect(within(alert).getByRole('link', { name: 'Taslağı düzenle' })).toHaveAttribute('href', '/satis/siparisler/o1/duzenle');

    const shortRow = screen.getByRole('row', { name: /HRD-DBL-8X40/ });
    expect(within(shortRow).getByText(/Stok yetersiz: İstenen 60, kullanılabilir 12/)).toBeInTheDocument();
    const fineRow = screen.getByRole('row', { name: /ELK-NYM-3X25/ });
    expect(within(fineRow).queryByText(/Stok yetersiz/)).toBeNull();

    // Still a draft: no success state was shown and the action is available again.
    expect(screen.getByText('Taslak', { selector: '.tag' })).toBeInTheDocument();
    expect(screen.queryByText('Sipariş onaylandı')).toBeNull();
    expect(screen.getByRole('button', { name: 'Siparişi onayla' })).not.toHaveAttribute('aria-busy');
  });

  it('kapsam dışı ya da olmayan siparişi ayırt etmeden "bulunamadı veya erişiminiz yok" olarak gösterir', async () => {
    mockApi([{ method: 'GET', path: PATH, reply: () => problem(404, 'not_found', { title: 'Kayıt bulunamadı' }) }]);
    show();

    expect(await screen.findByRole('heading', { name: 'Sipariş bulunamadı veya erişiminiz yok' })).toBeInTheDocument();
    expect(screen.queryByText('SO-00001042')).toBeNull();
    expect(screen.getByRole('link', { name: 'Siparişlere dön' })).toHaveAttribute('href', '/satis/siparisler');
    expect(document.title).toBe('Sipariş bulunamadı — Kantar');
  });

  it('yüklenirken meşgul durumu, sunucu hatasında ayrıntı ve yeniden deneme sunar', async () => {
    const user = userEvent.setup();
    let attempt = 0;
    mockApi([{ method: 'GET', path: PATH, reply: () => ((attempt += 1) === 1 ? problem(503, 'retry_later', { title: 'İşlem şu anda tamamlanamadı' }) : json(order())) }]);
    show();

    expect(screen.getByRole('status', { name: 'Sipariş yükleniyor…' })).toHaveAttribute('aria-busy', 'true');
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Veri yüklenemedi');
    expect(alert).toHaveTextContent('Korelasyon kimliği');

    await user.click(within(alert).getByRole('button', { name: 'Yeniden dene' }));
    expect(await screen.findByRole('heading', { level: 1, name: /SO-00001042/ })).toBeInTheDocument();
  });
});
