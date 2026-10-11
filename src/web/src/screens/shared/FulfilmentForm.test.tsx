import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { json, mockApi, problem } from '../../test/helpers';
import { FulfilmentForm } from './FulfilmentForm';
import type { FulfilmentRow } from './FulfilmentForm';

const ROWS: FulfilmentRow[] = [
  { id: 'l1', sku: 'ELK-NYM-3X25', productName: 'NYM Kablo 3×2,5 mm² (100 m top)', unit: 'TOP', orderedQty: 150, doneQty: 90, openQty: 60 },
  { id: 'l2', sku: 'ELK-NYA-1X15', productName: 'NYA Kablo 1×1,5 mm² (100 m top)', unit: 'TOP', orderedQty: 200, doneQty: 200, openQty: 0 },
  { id: 'l3', sku: 'ELK-KNL-25', productName: 'Kablo Kanalı 25×16 (2 m)', unit: 'ADET', orderedQty: 500, doneQty: 0, openQty: 500 },
];
const PATH = '/api/purchasing/orders/p1/receipts';

const RECEIPT = {
  id: 'gr1',
  number: 'GR-00000165',
  purchaseOrderId: 'p1',
  purchaseOrderNumber: 'PO-00000277',
  warehouseId: 'w1',
  externalReference: 'AK-2026-12044',
  createdBy: 'u1',
  createdAt: '2026-10-10T06:15:00Z',
  lines: [{ orderLineId: 'l3', productId: 'p3', sku: 'ELK-KNL-25', productName: 'Kablo Kanalı 25×16 (2 m)', unit: 'ADET', quantity: 300 }],
  purchaseOrderStatus: 'PartiallyReceived',
  purchaseOrderETag: '"AAAAAAAAB9k="',
};

function setup(rows: FulfilmentRow[] = ROWS) {
  const onPosted = vi.fn();
  const onReload = vi.fn();
  render(<FulfilmentForm kind="receipt" orderId="p1" rows={rows} onPosted={onPosted} onReload={onReload} />);
  return { onPosted, onReload, user: userEvent.setup() };
}

const reference = () => screen.getByLabelText('Tedarikçi irsaliye numarası');
const quantity = (sku: string) => screen.getByLabelText(`${sku} için bu kabulün miktarı`);
const submit = () => screen.getByRole('button', { name: 'Mal kabulü kaydet' });

describe('mal kabul formu', () => {
  it('açık miktarı aşan girişi istemci tarafında durdurur: istek gönderilmez, hata özeti odak alır ve alana bağlanır', async () => {
    const api = mockApi([{ method: 'POST', path: PATH, reply: () => json(RECEIPT, { status: 201 }) }]);
    const { user, onPosted } = setup();

    await user.type(reference(), 'ak-2026-12044');
    await user.type(quantity('ELK-NYM-3X25'), '70');
    await user.click(submit());

    const summary = await screen.findByRole('group', { name: /Mal kabul kaydedilmedi: 1 alanı düzeltin/ });
    expect(summary).toHaveFocus();
    expect(summary).toHaveTextContent('Hiçbir satır kaydedilmedi; stok değişmedi.');
    const link = within(summary).getByRole('link', { name: /ELK-NYM-3X25.*En fazla 60 girilebilir\./ });

    const input = quantity('ELK-NYM-3X25');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAccessibleDescription('En fazla 60 girilebilir.');
    await user.click(link);
    expect(input).toHaveFocus();

    expect(api.callsTo('POST', PATH)).toHaveLength(0);
    expect(onPosted).not.toHaveBeenCalled();
  });

  it('tamamı kabul edilmiş satıra giriş alanı vermez', () => {
    mockApi();
    setup();
    expect(screen.queryByLabelText('ELK-NYA-1X15 için bu kabulün miktarı')).toBeNull();
    expect(within(screen.getByRole('row', { name: /ELK-NYA-1X15/ })).getByText('Tamamı kabul edildi')).toBeInTheDocument();
  });

  it('kısmi kabule izin verir: yalnız doldurulan satırlar, tam sayı olarak ve If-Match olmadan gönderilir', async () => {
    const api = mockApi([{ method: 'POST', path: PATH, reply: () => json(RECEIPT, { status: 201, headers: { ETag: RECEIPT.purchaseOrderETag } }) }]);
    const { user, onPosted } = setup();

    await user.type(reference(), 'ak-2026-12044');
    await user.type(quantity('ELK-KNL-25'), '300');
    await user.click(submit());

    await waitFor(() => expect(onPosted).toHaveBeenCalledTimes(1));
    expect(onPosted.mock.calls[0]?.[0]).toMatchObject({ number: 'GR-00000165', purchaseOrderStatus: 'PartiallyReceived' });

    const call = api.callsTo('POST', PATH)[0];
    // The reference is upper-cased like the server does; the untouched line is not sent at all.
    expect(call?.body).toEqual({ externalReference: 'AK-2026-12044', lines: [{ orderLineId: 'l3', quantity: 300 }] });
    expect(JSON.stringify(call?.body)).toContain('"quantity":300');
    expect(call?.headers['if-match']).toBeUndefined();
    expect(call?.headers['idempotency-key']).toMatch(/^[A-Za-z0-9_-]{16,128}$/);

    // After the server confirmed, the form is ready for the next delivery.
    expect(reference()).toHaveValue('');
    expect(quantity('ELK-KNL-25')).toHaveValue('');
  });

  it('referans ve en az bir miktar olmadan göndermez', async () => {
    const api = mockApi();
    const { user } = setup();

    await user.click(submit());

    const summary = await screen.findByRole('group', { name: /2 alanı düzeltin/ });
    expect(summary).toHaveTextContent('Tedarikçi irsaliye numarası zorunludur.');
    expect(summary).toHaveTextContent('En az bir satıra miktar girin.');
    expect(api.callsTo('POST', PATH)).toHaveLength(0);
  });

  it('ondalık ya da sıfır miktarı kabul etmez', async () => {
    const api = mockApi();
    const { user } = setup();

    await user.type(reference(), 'AK-1');
    await user.type(quantity('ELK-NYM-3X25'), '1,5');
    await user.type(quantity('ELK-KNL-25'), '0');
    await user.click(submit());

    const summary = await screen.findByRole('group', { name: /2 alanı düzeltin/ });
    expect(within(summary).getAllByRole('link')).toHaveLength(2);
    expect(api.callsTo('POST', PATH)).toHaveLength(0);
  });

  it('sunucunun 409 exceeds_remaining yanıtını ilgili satıra eşler ve siparişi yeniden okutur', async () => {
    const api = mockApi([
      {
        method: 'POST',
        path: PATH,
        reply: () =>
          problem(409, 'exceeds_remaining', {
            title: 'Miktar kalan miktarı aşıyor',
            userCode: 'MKB-409',
            // Index 1 of the lines that were SENT is ELK-KNL-25 (the fully received line is never sent).
            errors: [{ field: 'lines[1].quantity', code: 'exceeds_remaining', detail: 'İstenen 500, kalan 200' }],
          }),
      },
    ]);
    const { user, onPosted, onReload } = setup();

    await user.type(reference(), 'AK-2026-12044');
    await user.type(quantity('ELK-NYM-3X25'), '60');
    await user.type(quantity('ELK-KNL-25'), '500');
    await user.click(submit());

    const summary = await screen.findByRole('group', { name: /Miktar kalan miktarı aşıyor: 1 alanı düzeltin/ });
    expect(summary).toHaveFocus();
    expect(within(summary).getByRole('link', { name: /ELK-KNL-25.*İstenen 500, kalan 200/ })).toBeInTheDocument();
    expect(summary).toHaveTextContent('MKB-409');
    expect(summary).toHaveTextContent('0192f0c1a2b37c4d8e9f000000000001');
    expect(summary).toHaveTextContent('Hiçbir satır kaydedilmedi; stok değişmedi.');

    expect(quantity('ELK-KNL-25')).toHaveAccessibleDescription('İstenen 500, kalan 200');
    expect(quantity('ELK-NYM-3X25')).not.toHaveAttribute('aria-invalid');
    expect(onReload).toHaveBeenCalledTimes(1);
    expect(onPosted).not.toHaveBeenCalled();
    // The user's input is still there to correct.
    expect(quantity('ELK-KNL-25')).toHaveValue('500');
    expect(reference()).toHaveValue('AK-2026-12044');
    expect(api.callsTo('POST', PATH)).toHaveLength(1);
  });

  it('yinelenen irsaliye numarasında (409) hiçbir şeyin iki kez uygulanmadığını söyler', async () => {
    mockApi([
      {
        method: 'POST',
        path: PATH,
        reply: () =>
          problem(409, 'duplicate_business_reference', {
            title: 'Bu iş referansı daha önce kullanıldı',
            userCode: 'REF-409',
            detail: 'Bu tedarikçi irsaliye numarası daha önce kabul edildi; stok ikinci kez artırılmadı.',
          }),
      },
    ]);
    const { user, onPosted } = setup();

    await user.type(reference(), 'AK-2026-11873');
    await user.type(quantity('ELK-KNL-25'), '10');
    await user.click(submit());

    const summary = await screen.findByRole('group', { name: /Bu iş referansı daha önce kullanıldı/ });
    expect(summary).toHaveTextContent('stok ikinci kez artırılmadı');
    expect(summary).toHaveTextContent('REF-409');
    expect(reference()).toHaveAttribute('aria-invalid', 'true');
    expect(onPosted).not.toHaveBeenCalled();
  });

  it('ağ hatasında sonucu bilinmiyor der ve aynı anahtarla yeniden denemeyi sunar', async () => {
    let attempt = 0;
    const api = mockApi([
      { method: 'POST', path: PATH, reply: () => ((attempt += 1) === 1 ? Promise.reject(new TypeError('Failed to fetch')) : json(RECEIPT, { status: 201 })) },
    ]);
    const { user, onPosted } = setup();

    await user.type(reference(), 'AK-2026-12044');
    await user.type(quantity('ELK-KNL-25'), '300');
    await user.click(submit());

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Sunucuya ulaşılamadı');
    expect(alert).toHaveTextContent('aynı işlemi güvenle yeniden deneyebilirsiniz');
    expect(onPosted).not.toHaveBeenCalled();

    await user.click(within(alert).getByRole('button', { name: 'Yeniden dene' }));
    await waitFor(() => expect(onPosted).toHaveBeenCalledTimes(1));

    const keys = api.callsTo('POST', PATH).map((c) => c.headers['idempotency-key']);
    expect(keys).toHaveLength(2);
    expect(keys[1]).toBe(keys[0]);
  });

  it('sevkiyat kipinde sevk referansı alanını ve sevk uç noktasını kullanır', async () => {
    const api = mockApi([{ method: 'POST', path: '/api/sales/orders/o1/shipments', reply: () => json({ id: 's1', number: 'SH-00000319', lines: [] }, { status: 201 }) }]);
    const onPosted = vi.fn();
    const user = userEvent.setup();
    render(<FulfilmentForm kind="shipment" orderId="o1" rows={ROWS} onPosted={onPosted} onReload={vi.fn()} />);

    await user.type(screen.getByLabelText('Sevk referansı (irsaliye no)'), 'IRS-2026-0904');
    await user.click(screen.getByRole('button', { name: 'Açık miktarların tamamını doldur' }));
    expect(screen.getByLabelText('ELK-NYM-3X25 için bu sevkiyatın miktarı')).toHaveValue('60');
    await user.click(screen.getByRole('button', { name: 'Sevkiyatı kaydet' }));

    await waitFor(() => expect(onPosted).toHaveBeenCalledTimes(1));
    const call = api.callsTo('POST', '/api/sales/orders/o1/shipments')[0];
    expect(call?.body).toEqual({
      dispatchReference: 'IRS-2026-0904',
      lines: [
        { orderLineId: 'l1', quantity: 60 },
        { orderLineId: 'l3', quantity: 500 },
      ],
    });
    expect(call?.headers['if-match']).toBeUndefined();
  });
});
