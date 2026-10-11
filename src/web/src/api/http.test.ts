import { describe, expect, it, vi } from 'vitest';
import { deferred, json, mockApi, noContent, problem } from '../test/helpers';
import { kbApi, purchasingApi, salesApi, supportApi } from './endpoints';
import { getJson, request, resetClientSession, setCsrfToken, setSessionListeners } from './http';
import { ApiProblem, SupersededError } from './problem';

describe('CSRF belirteci', () => {
  it('ilk değişiklik isteğinden önce bir kez alınır ve her GET dışı istekte gönderilir', async () => {
    const api = mockApi([
      { method: 'GET', path: '/api/auth/csrf', reply: () => json({ token: 'tok-1' }) },
      { method: 'POST', path: '/api/notifications/n1/read', reply: () => noContent() },
      { method: 'GET', path: '/api/notifications', reply: () => json([]) },
    ]);

    await request('/api/notifications/n1/read', { method: 'POST' });
    await request('/api/notifications/n1/read', { method: 'POST' });
    await getJson('/api/notifications');

    expect(api.callsTo('GET', '/api/auth/csrf')).toHaveLength(1);
    const posts = api.callsTo('POST', '/api/notifications/n1/read');
    expect(posts).toHaveLength(2);
    expect(posts.map((c) => c.headers['x-csrf-token'])).toEqual(['tok-1', 'tok-1']);
    expect(api.callsTo('GET', '/api/notifications')[0]?.headers['x-csrf-token']).toBeUndefined();
  });

  it('eşzamanlı iki istek tek bir belirteç isteğini paylaşır', async () => {
    const api = mockApi([{ method: 'POST', path: '/api/x', reply: () => noContent() }]);
    await Promise.all([request('/api/x', { method: 'POST' }), request('/api/x', { method: 'POST' })]);
    expect(api.callsTo('GET', '/api/auth/csrf')).toHaveLength(1);
  });

  it('giriş yanıtındaki belirteç bellekteki belirtecin yerine geçer', async () => {
    const api = mockApi([{ method: 'POST', path: '/api/x', reply: () => noContent() }]);
    await request('/api/x', { method: 'POST' });
    setCsrfToken('oturum-belirteci');
    await request('/api/x', { method: 'POST' });

    expect(api.callsTo('POST', '/api/x').map((c) => c.headers['x-csrf-token'])).toEqual(['csrf-anon', 'oturum-belirteci']);
    expect(api.callsTo('GET', '/api/auth/csrf')).toHaveLength(1);
  });

  it('csrf_failed yanıtında belirteci yeniler ve isteği bir kez yeniden dener', async () => {
    let csrfCalls = 0;
    let posts = 0;
    const api = mockApi([
      { method: 'GET', path: '/api/auth/csrf', reply: () => json({ token: `tok-${(csrfCalls += 1)}` }) },
      { method: 'POST', path: '/api/x', reply: () => ((posts += 1) === 1 ? problem(403, 'csrf_failed') : json({ ok: true })) },
    ]);

    const result = await request<{ ok: boolean }>('/api/x', { method: 'POST', body: { a: 1 } });

    expect(result.data).toEqual({ ok: true });
    expect(api.callsTo('POST', '/api/x').map((c) => c.headers['x-csrf-token'])).toEqual(['tok-1', 'tok-2']);
  });

  it('çerezleri yalnız aynı kaynağa gönderir ve gövdeyi JSON olarak yazar', async () => {
    const api = mockApi([{ method: 'POST', path: '/api/x', reply: () => noContent() }]);
    await request('/api/x', { method: 'POST', body: { quantity: 3 } });
    const call = api.callsTo('POST', '/api/x')[0];
    expect(call?.credentials).toBe('same-origin');
    expect(call?.headers['content-type']).toBe('application/json');
    expect(call?.body).toEqual({ quantity: 3 });
  });

  it('belirteç ve kullanıcı verisi için tarayıcı deposuna hiçbir şey yazmaz', async () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    mockApi([{ method: 'POST', path: '/api/x', reply: () => json({ secret: 'veri' }) }]);
    await request('/api/x', { method: 'POST', body: { a: 1 } });
    setCsrfToken('oturum-belirteci');
    expect(setItem).not.toHaveBeenCalled();
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
    expect(document.cookie).toBe('');
  });
});

describe('sorun yanıtları', () => {
  it('problem+json gövdesini alan hataları, kullanıcı kodu ve korelasyon kimliğiyle ApiProblem olarak verir', async () => {
    mockApi([
      {
        method: 'POST',
        path: '/api/sales/orders/o1/confirm',
        reply: () =>
          problem(409, 'insufficient_available', {
            title: 'Yeterli kullanılabilir stok yok',
            userCode: 'STK-409',
            errors: [{ field: 'lines[1]', code: 'insufficient_available', detail: 'İstenen 40, kullanılabilir 12' }],
          }),
      },
    ]);

    const error = await salesApi.confirm('o1', '"AAAAAAAAB9E="', 'key-0000000000000001').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiProblem);
    const failure = error as ApiProblem;
    expect(failure.status).toBe(409);
    expect(failure.code).toBe('insufficient_available');
    expect(failure.title).toBe('Yeterli kullanılabilir stok yok');
    expect(failure.userCode).toBe('STK-409');
    expect(failure.correlationId).toBe('0192f0c1a2b37c4d8e9f000000000001');
    expect(failure.errors).toEqual([{ field: 'lines[1]', code: 'insufficient_available', detail: 'İstenen 40, kullanılabilir 12' }]);
    expect(failure.isRetryable).toBe(false);
  });

  it('429 için Retry-After süresini okur', async () => {
    mockApi([{ method: 'POST', path: '/api/assist/ask', reply: () => problem(429, 'rate_limited', { headers: { 'Retry-After': '60' } }) }]);
    const error = (await request('/api/assist/ask', { method: 'POST', body: { question: 'abc' } }).catch((e: unknown) => e)) as ApiProblem;
    expect(error.isRateLimited).toBe(true);
    expect(error.retryAfterSeconds).toBe(60);
  });

  it('ağ hatasını ve 503 yanıtını ayrı, yeniden denenebilir durumlar olarak verir', async () => {
    const api = mockApi([
      { method: 'GET', path: '/api/down', reply: () => Promise.reject(new TypeError('Failed to fetch')) },
      { method: 'GET', path: '/api/busy', reply: () => problem(503, 'retry_later') },
    ]);

    const network = (await getJson('/api/down').catch((e: unknown) => e)) as ApiProblem;
    const busy = (await getJson('/api/busy').catch((e: unknown) => e)) as ApiProblem;

    expect(network).toBeInstanceOf(ApiProblem);
    expect(network.isNetwork).toBe(true);
    expect(network.isUnavailable).toBe(false);
    expect(network.isRetryable).toBe(true);
    expect(busy.isNetwork).toBe(false);
    expect(busy.isUnavailable).toBe(true);
    expect(busy.isRetryable).toBe(true);
    expect(busy.correlationId).toBeDefined();
    expect(api.calls).toHaveLength(2);
  });

  it('gövdesi JSON olmayan hata yanıtını da durum koduyla sorun olarak verir', async () => {
    mockApi([{ method: 'GET', path: '/api/proxy', reply: () => new Response('<html>Bad Gateway</html>', { status: 502 }) }]);
    const error = (await getJson('/api/proxy').catch((e: unknown) => e)) as ApiProblem;
    expect(error.status).toBe(502);
    expect(error.code).toBe('http_502');
    expect(error.isRetryable).toBe(true);
  });
});

describe('önkoşul ve idempotency başlıkları', () => {
  const order = { id: 'o1', eTag: '"AAAAAAAAB9I="' };

  it('onay, iptal, atama, durum ve yayınlama komutlarında If-Match gönderir', async () => {
    const api = mockApi([{ method: 'POST', path: /^\/api\//, reply: () => json(order) }]);

    await salesApi.confirm('o1', '"etag-1"', 'idem-key-000000001');
    await salesApi.cancel('o1', 'müşteri vazgeçti', '"etag-2"', 'idem-key-000000002');
    await purchasingApi.approve('p1', '"etag-3"', 'idem-key-000000003');
    await supportApi.assign('t1', 'u1', '"etag-4"');
    await supportApi.setStatus('t1', 'Resolved', '"etag-5"');
    await kbApi.publish('d1', 2, '"etag-6"', 'idem-key-000000006');

    expect(api.calls.filter((c) => c.method === 'POST').map((c) => [c.path, c.headers['if-match']])).toEqual([
      ['/api/sales/orders/o1/confirm', '"etag-1"'],
      ['/api/sales/orders/o1/cancel', '"etag-2"'],
      ['/api/purchasing/orders/p1/approve', '"etag-3"'],
      ['/api/support/tickets/t1/assign', '"etag-4"'],
      ['/api/support/tickets/t1/status', '"etag-5"'],
      ['/api/kb/manage/documents/d1/publish', '"etag-6"'],
    ]);
    expect(api.callsTo('POST', '/api/sales/orders/o1/confirm')[0]?.headers['idempotency-key']).toBe('idem-key-000000001');
    // Assign and status are not idempotency-keyed by contract.
    expect(api.callsTo('POST', '/api/support/tickets/t1/assign')[0]?.headers['idempotency-key']).toBeUndefined();
  });

  it('sevkiyat ve mal kabulde If-Match göndermez, Idempotency-Key gönderir', async () => {
    const api = mockApi([{ method: 'POST', path: /^\/api\//, reply: () => json({ id: 's1' }, { status: 201 }) }]);

    await salesApi.ship('o1', 'IRS-2026-0904', [{ orderLineId: 'l1', quantity: 5 }], 'idem-key-000000010');
    await purchasingApi.receive('p1', 'AK-2026-12044', [{ orderLineId: 'l1', quantity: 5 }], 'idem-key-000000011');

    const ship = api.callsTo('POST', '/api/sales/orders/o1/shipments')[0];
    const receive = api.callsTo('POST', '/api/purchasing/orders/p1/receipts')[0];
    expect(ship?.headers['if-match']).toBeUndefined();
    expect(receive?.headers['if-match']).toBeUndefined();
    expect(ship?.headers['idempotency-key']).toBe('idem-key-000000010');
    expect(receive?.headers['idempotency-key']).toBe('idem-key-000000011');
    expect(ship?.body).toEqual({ dispatchReference: 'IRS-2026-0904', lines: [{ orderLineId: 'l1', quantity: 5 }] });
  });

  it('isteğe bağlı alanı boşken gövdeye hiç yazmaz (sunucu bilinmeyen/null alanı reddeder)', async () => {
    const api = mockApi([{ method: 'POST', path: '/api/sales/orders', reply: () => json(order, { status: 201 }) }]);
    await salesApi.create({ customerId: 'c1', warehouseId: 'w1', note: undefined, lines: [{ productId: 'p1', quantity: 2 }] }, 'idem-key-000000020');
    expect(api.callsTo('POST', '/api/sales/orders')[0]?.body).toEqual({ customerId: 'c1', warehouseId: 'w1', lines: [{ productId: 'p1', quantity: 2 }] });
  });

  it('Idempotent-Replay yanıtını başarı sayar ve ETag başlığını verir', async () => {
    mockApi([
      {
        method: 'POST',
        path: '/api/sales/orders/o1/confirm',
        reply: () => json(order, { headers: { 'Idempotent-Replay': 'true', ETag: '"AAAAAAAAB9I="' } }),
      },
    ]);
    const result = await request<typeof order>('/api/sales/orders/o1/confirm', { method: 'POST', idempotencyKey: 'idem-key-000000030', ifMatch: '"x"' });
    expect(result.replayed).toBe(true);
    expect(result.etag).toBe('"AAAAAAAAB9I="');
    expect(result.data).toEqual(order);
  });
});

describe('oturum olayları', () => {
  it('401 yanıtında oturum dinleyicisini çağırır; oturum yoklamasında çağırmaz', async () => {
    const onUnauthenticated = vi.fn();
    setSessionListeners({ onUnauthenticated });
    mockApi([{ method: 'GET', path: /^\/api\//, reply: () => problem(401, 'unauthenticated') }]);

    const quiet = (await request('/api/auth/me', { quiet401: true }).catch((e: unknown) => e)) as ApiProblem;
    expect(quiet.status).toBe(401);
    expect(onUnauthenticated).not.toHaveBeenCalled();

    const loud = (await getJson('/api/sales/orders').catch((e: unknown) => e)) as ApiProblem;
    expect(loud.status).toBe(401);
    expect(onUnauthenticated).toHaveBeenCalledTimes(1);
  });

  it('403 password_change_required yanıtında parola dinleyicisini çağırır', async () => {
    const onPasswordChangeRequired = vi.fn();
    setSessionListeners({ onPasswordChangeRequired });
    mockApi([
      { method: 'GET', path: '/api/a', reply: () => problem(403, 'password_change_required') },
      { method: 'GET', path: '/api/b', reply: () => problem(403, 'forbidden') },
    ]);

    await getJson('/api/b').catch(() => undefined);
    expect(onPasswordChangeRequired).not.toHaveBeenCalled();
    await getJson('/api/a').catch(() => undefined);
    expect(onPasswordChangeRequired).toHaveBeenCalledTimes(1);
  });

  it('oturum sona erdikten sonra gelen yanıtı teslim etmez', async () => {
    const pending = deferred<Response>();
    const api = mockApi([{ method: 'GET', path: '/api/sales/orders', reply: () => pending.promise }]);
    // The response is already on the wire: the abort does not stop it, only the session counter does.
    api.ignoreAbort = true;

    const inFlight = getJson('/api/sales/orders').then(
      (value) => ({ delivered: value }),
      (error: unknown) => ({ error }),
    );
    resetClientSession();
    pending.resolve(json({ items: [{ number: 'SO-00000001' }] }));

    const outcome = await inFlight;
    expect(outcome).not.toHaveProperty('delivered');
    expect((outcome as { error: unknown }).error).toBeInstanceOf(SupersededError);
  });
});
