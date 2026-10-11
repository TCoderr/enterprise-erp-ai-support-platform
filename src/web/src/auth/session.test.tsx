import { QueryClientProvider } from '@tanstack/react-query';
import type { QueryClient } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { AppRoot } from '../App';
import type { Me, TicketListItem } from '../api/types';
import { deferred, json, makeMe, mockApi, noContent, problem, testQueryClient } from '../test/helpers';
import type { ApiDouble } from '../test/helpers';

const AYSE = makeMe({ id: 'aaaaaaaa-0000-7000-8000-00000000000a', userName: 'ayse.kaya', displayName: 'Ayşe Kaya', roles: [], warehouses: [] });
const BURAK = makeMe({ id: 'bbbbbbbb-0000-7000-8000-00000000000b', userName: 'burak.demir', displayName: 'Burak Demir', roles: [], warehouses: [] });

function ticket(owner: Me, title: string): TicketListItem {
  return {
    id: `t-${owner.userName}`,
    number: owner === AYSE ? 'TK-00000101' : 'TK-00000202',
    status: 'Open',
    priority: 'Normal',
    module: 'General',
    title,
    requesterUserId: owner.id,
    createdAt: '2026-10-09T11:12:00Z',
    updatedAt: '2026-10-09T11:12:00Z',
  };
}

const AYSE_TICKET = 'Ayşe’nin gizli talebi';
const BURAK_TICKET = 'Burak’ın talebi';

interface Server {
  api: ApiDouble;
  /** Who the session cookie belongs to on the "server"; null means no session. */
  current: Me | null;
}

function startServer(initial: Me | null): Server {
  const server: Server = { api: mockApi(), current: initial };
  const { api } = server;
  api.on('GET', '/api/auth/me', () => (server.current !== null ? json(server.current) : problem(401, 'unauthenticated')));
  api.on('GET', '/api/notifications', () => (server.current !== null ? json([]) : problem(401, 'unauthenticated')));
  api.on('GET', '/api/support/tickets', () => {
    if (server.current === null) {
      return problem(401, 'unauthenticated');
    }

    return json({ items: [server.current === AYSE ? ticket(AYSE, AYSE_TICKET) : ticket(BURAK, BURAK_TICKET)] });
  });
  api.on('POST', '/api/auth/logout', () => {
    server.current = null;
    return noContent();
  });
  return server;
}

function mount(url: string): QueryClient {
  const queryClient = testQueryClient();
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[url]}>
        <AppRoot queryClient={queryClient} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return queryClient;
}

function cachedUserIds(queryClient: QueryClient): unknown[] {
  return [...new Set(queryClient.getQueryCache().getAll().map((query) => query.queryKey[0]))];
}

async function signIn(user: ReturnType<typeof userEvent.setup>, userName: string) {
  await user.type(await screen.findByLabelText('Kullanıcı adı'), userName);
  await user.type(screen.getByLabelText('Parola'), 'Doğru-Parola-2026');
  await user.click(screen.getByRole('button', { name: 'Giriş yap' }));
}

describe('oturum ve önbellek yalıtımı', () => {
  it('çıkıştan sonra A kullanıcısının verisi B kullanıcısına gösterilmez', async () => {
    const user = userEvent.setup();
    const server = startServer(AYSE);
    server.api.on('POST', '/api/auth/login', (call) => {
      expect(call.body).toEqual({ userName: 'burak.demir', password: 'Doğru-Parola-2026' });
      server.current = BURAK;
      return json({ user: BURAK, csrfToken: 'csrf-burak' });
    });
    const queryClient = mount('/destek/talepler');

    expect(await screen.findByText(AYSE_TICKET)).toBeInTheDocument();
    expect(cachedUserIds(queryClient)).toEqual([AYSE.id]);

    await user.click(screen.getByRole('button', { name: /Kullanıcı menüsü: Ayşe Kaya/ }));
    await user.click(screen.getByRole('button', { name: 'Çıkış yap' }));

    expect(await screen.findByRole('heading', { name: 'Giriş' })).toBeInTheDocument();
    expect(screen.queryByText(AYSE_TICKET)).toBeNull();
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);

    await signIn(user, 'burak.demir');

    expect(await screen.findByText(BURAK_TICKET)).toBeInTheDocument();
    expect(screen.queryByText(AYSE_TICKET)).toBeNull();
    expect(cachedUserIds(queryClient)).toEqual([BURAK.id]);

    // The anonymous token is used for the login request only; afterwards the session token applies.
    expect(server.api.callsTo('POST', '/api/auth/login')[0]?.headers['x-csrf-token']).toBe('csrf-anon');
    await user.click(screen.getByRole('button', { name: /Kullanıcı menüsü: Burak Demir/ }));
    await user.click(screen.getByRole('button', { name: 'Çıkış yap' }));
    await screen.findByRole('heading', { name: 'Giriş' });
    expect(server.api.callsTo('POST', '/api/auth/logout')[1]?.headers['x-csrf-token']).toBe('csrf-burak');
  });

  it('kullanıcı değiştiğinde A için uçuşta olan yanıt gelse bile gösterilmez', async () => {
    const server = startServer(AYSE);
    const slow = deferred<Response>();
    let ticketCalls = 0;
    server.api.on('GET', '/api/support/tickets', () => {
      ticketCalls += 1;
      if (ticketCalls === 1) {
        return slow.promise; // Ayşe's list, still on the wire
      }

      return json({ items: [ticket(BURAK, BURAK_TICKET)] });
    });
    // The late response cannot be stopped by aborting; only the session counter keeps it out.
    server.api.ignoreAbort = true;
    const queryClient = mount('/destek/talepler');

    await waitFor(() => expect(ticketCalls).toBe(1));
    expect(screen.queryByText(AYSE_TICKET)).toBeNull();

    // Another colleague signs in on the same terminal (same cookie jar, another tab).
    server.current = BURAK;
    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'));
    });

    expect(await screen.findByText(BURAK_TICKET)).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: /Kullanıcı menüsü: Burak Demir/ })).toBeInTheDocument();

    await act(async () => {
      slow.resolve(json({ items: [ticket(AYSE, AYSE_TICKET)] }));
      await new Promise((resolve) => setTimeout(resolve, 20));
    });

    expect(screen.queryByText(AYSE_TICKET)).toBeNull();
    expect(screen.getByText(BURAK_TICKET)).toBeInTheDocument();
    expect(cachedUserIds(queryClient)).toEqual([BURAK.id]);
    expect(JSON.stringify(queryClient.getQueryCache().getAll().map((q) => q.state.data))).not.toContain('gizli');
  });

  it('herhangi bir istekte 401 gelince istemci durumunu temizler, girişe götürür ve sonra kalınan sayfaya döner', async () => {
    const user = userEvent.setup();
    const server = startServer(AYSE);
    let expired = false;
    server.api.on('GET', '/api/kb/articles', () =>
      expired
        ? problem(401, 'unauthenticated')
        : json([{ slug: 'hata-stk-409', title: 'STK-409 — Yetersiz stok', module: 'Inventory', visibility: 'General', versionNo: 3, updatedAt: '2026-10-01T08:00:00Z' }]),
    );
    server.api.on('POST', '/api/auth/login', () => {
      server.current = AYSE;
      expired = false;
      return json({ user: AYSE, csrfToken: 'csrf-ayse-2' });
    });
    const queryClient = mount('/bilgi-bankasi?ara=stok');

    expect(await screen.findByRole('link', { name: 'STK-409 — Yetersiz stok' })).toBeInTheDocument();

    // The session expires on the server; the next request anywhere answers 401.
    expired = true;
    server.current = null;
    await act(async () => {
      await queryClient.invalidateQueries();
    });

    expect(await screen.findByText('Oturumunuz sona erdi')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Giriş' })).toBeInTheDocument();
    expect(screen.queryByText('STK-409 — Yetersiz stok')).toBeNull();
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);

    await signIn(user, 'ayse.kaya');

    // Back on the page the user was on (path and query), not on the role's home page.
    expect(await screen.findByRole('heading', { name: 'Bilgi bankası' })).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'STK-409 — Yetersiz stok' })).toBeInTheDocument();
    expect(screen.getByLabelText('Başlıkta veya metinde ara')).toHaveValue('stok');
  });

  it('oturum yokken giriş ekranını gösterir ve hatalı girişi alan ayrımı yapmadan bildirir', async () => {
    const user = userEvent.setup();
    const server = startServer(null);
    server.api.on('POST', '/api/auth/login', () => problem(401, 'unauthenticated', { title: 'Oturum açmanız gerekiyor', detail: 'Kullanıcı adı veya parola hatalı.' }));
    mount('/satis/siparisler');

    await signIn(user, 'olmayan.kullanici');

    const summary = await screen.findByRole('group', { name: 'Giriş yapılamadı' });
    expect(summary).toHaveTextContent('Kullanıcı adı veya parola hatalı.');
    expect(summary).toHaveFocus();
    expect(screen.getByRole('button', { name: 'Giriş yap' })).not.toHaveAttribute('aria-busy');
  });

  it('geçici parolayla girişte yalnız parola değiştirme ekranını gösterir', async () => {
    const server = startServer({ ...AYSE, mustChangePassword: true });
    mount('/destek/talepler');

    expect(await screen.findByRole('heading', { name: 'Parolanızı değiştirin' })).toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Ana gezinme' })).toBeNull();
    expect(server.api.callsTo('GET', '/api/support/tickets')).toHaveLength(0);
  });

  it('çıkış isteği sunucuya ulaşmazsa bunu açıkça söyler', async () => {
    const user = userEvent.setup();
    const server = startServer(AYSE);
    server.api.on('POST', '/api/auth/logout', () => Promise.reject(new TypeError('Failed to fetch')));
    mount('/destek/talepler');

    await screen.findByText(AYSE_TICKET);
    await user.click(screen.getByRole('button', { name: /Kullanıcı menüsü/ }));
    await user.click(screen.getByRole('button', { name: 'Çıkış yap' }));

    expect(await screen.findByText('Çıkış sunucuda doğrulanamadı')).toBeInTheDocument();
    expect(screen.queryByText(AYSE_TICKET)).toBeNull();
  });
});
