import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { AppRoot } from '../App';
import type { Me } from '../api/types';
import { ANKARA, ISTANBUL, json, makeMe, mockApi, testQueryClient } from '../test/helpers';

function mount(me: Me, url: string) {
  const api = mockApi([
    { method: 'GET', path: '/api/auth/me', reply: () => json(me) },
    { method: 'GET', path: '/api/notifications', reply: () => json([{ id: 'n1', kind: 'low_stock', text: 'ELK-KNL-25 düşük stokta (ANK-01).', createdAt: '2026-10-09T11:12:00Z', isRead: false }]) },
    { method: 'GET', path: '/api/admin/users', reply: () => json([]) },
    { method: 'GET', path: '/api/sales/orders', reply: () => json({ items: [] }) },
    { method: 'GET', path: '/api/assist/status', reply: () => json({ kind: 'demo', isDemo: true }) },
  ]);
  const queryClient = testQueryClient();
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[url]}>
        <AppRoot queryClient={queryClient} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return api;
}

async function navLinks(): Promise<string[]> {
  const nav = await screen.findByRole('navigation', { name: 'Ana gezinme' });
  return within(nav)
    .getAllByRole('link')
    .map((link) => link.textContent ?? '');
}

describe('kabuk', () => {
  it('sistem yöneticisine Satış, Satın Alma ve Depo bağlantılarını göstermez', async () => {
    mount(makeMe({ roles: ['SystemAdmin'], warehouses: [] }), '/');
    expect(await navLinks()).toEqual(['Taleplerim', 'Yeni talep', 'Makaleler', 'Kullanıcılar', 'Ana veri', 'Denetim kaydı', 'Operasyon']);
    // The landing page is the role's first task list, and the depot selector is absent without grants.
    expect(await screen.findByRole('heading', { level: 1, name: 'Kullanıcılar' })).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: 'Depo' })).toBeNull();
    await waitFor(() => expect(document.title).toBe('Kullanıcılar — Kantar'));
  });

  it('depo görevlisine yalnız Depo, Destek ve Bilgi Bankası bağlantılarını gösterir', async () => {
    mount(makeMe({ roles: ['WarehouseOperator'], warehouses: [ANKARA, ISTANBUL] }), '/sayfa-yok');
    expect(await navLinks()).toEqual([
      'Stok bakiyeleri',
      'Hareketler',
      'Rezervasyonlar',
      'Sevk bekleyenler',
      'Kabul bekleyenler',
      'Transfer',
      'Düzeltme',
      'Taleplerim',
      'Yeni talep',
      'Makaleler',
    ]);
    expect(await screen.findByRole('heading', { level: 1, name: 'Sayfa bulunamadı' })).toBeInTheDocument();
  });

  it('rolün kapsamadığı ekranın adresi yazılırsa ekranı açmaz', async () => {
    const api = mount(makeMe({ roles: ['SystemAdmin'], warehouses: [] }), '/satis/siparisler');
    expect(await screen.findByRole('heading', { level: 1, name: 'Bu ekran rolünüze açık değil' })).toBeInTheDocument();
    expect(api.callsTo('GET', '/api/sales/orders')).toHaveLength(0);
  });

  it('depo seçimini adres sorgusunda tutar ve gezinme bağlantılarına taşır', async () => {
    const user = userEvent.setup();
    mount(makeMe({ roles: ['SalesManager'], warehouses: [ANKARA, ISTANBUL] }), '/satis/siparisler');

    const select = await screen.findByRole('combobox', { name: 'Depo' });
    expect(select).toHaveValue('ANK-01');
    expect(within(select).getAllByRole('option').map((o) => o.textContent)).toEqual(['ANK-01 · Ankara Merkez Depo', 'IST-02 · İstanbul Tuzla Depo']);

    await user.selectOptions(select, 'IST-02');

    expect(select).toHaveValue('IST-02');
    const nav = screen.getByRole('navigation', { name: 'Ana gezinme' });
    expect(within(nav).getByRole('link', { name: 'Stok bakiyeleri' })).toHaveAttribute('href', '/depo/stok?depo=IST-02');
    expect(within(nav).getByRole('link', { name: 'Siparişler' })).toHaveAttribute('aria-current', 'page');
    expect(window.localStorage.length + window.sessionStorage.length).toBe(0);
  });

  it('atlama bağlantısı, etiketli simge düğmeleri ve bildirim listesi sunar', async () => {
    const user = userEvent.setup();
    const api = mount(makeMe({ roles: ['SalesManager'] }), '/satis/siparisler');
    api.on('POST', '/api/notifications/n1/read', () => new Response(null, { status: 204 }));

    expect(await screen.findByRole('link', { name: 'İçeriğe geç' })).toHaveAttribute('href', '#icerik');
    expect(screen.getByRole('main')).toHaveAttribute('id', 'icerik');

    const bell = await screen.findByRole('button', { name: 'Bildirimler, 1 okunmamış' });
    expect(bell).toHaveAttribute('aria-expanded', 'false');
    await user.click(bell);
    const list = screen.getByRole('region', { name: 'Bildirimler' });
    expect(list).toHaveTextContent('ELK-KNL-25 düşük stokta (ANK-01).');
    await user.click(within(list).getByRole('button', { name: 'Okundu olarak işaretle' }));
    expect(api.callsTo('POST', '/api/notifications/n1/read')).toHaveLength(1);

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('region', { name: 'Bildirimler' })).toBeNull();

    const assistButton = screen.getByRole('button', { name: 'Kaynaklı Yardım' });
    await user.click(assistButton);
    expect(await screen.findByRole('complementary', { name: 'Kaynaklı Yardım' })).toBeInTheDocument();
    expect(await screen.findByText('Demo sağlayıcı — dil modeli değil')).toBeInTheDocument();
  });
});
