import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { purchasingApi } from '../api/endpoints';
import { IntentKeys } from '../api/intent';
import { json, mockApi, problem } from '../test/helpers';
import { useCommand } from './useCommand';

describe('IntentKeys', () => {
  it('aynı içerik için aynı anahtarı, değişen içerik için yeni anahtarı verir', () => {
    const keys = new IntentKeys();
    const first = keys.keyFor({ reference: 'A-1', lines: [1] });
    expect(first).toMatch(/^[A-Za-z0-9_-]{16,128}$/);
    expect(keys.keyFor({ reference: 'A-1', lines: [1] })).toBe(first);
    expect(keys.keyFor({ reference: 'A-2', lines: [1] })).not.toBe(first);
  });

  it('komut sonuçlandıktan sonra yeni anahtar üretir', () => {
    const keys = new IntentKeys();
    const first = keys.keyFor('x');
    keys.settle();
    expect(keys.keyFor('x')).not.toBe(first);
  });
});

describe('useCommand ve Idempotency-Key', () => {
  const receive = (lines: { orderLineId: string; quantity: number }[], key: string) => purchasingApi.receive('p1', 'AK-2026-12044', lines, key);
  const path = '/api/purchasing/orders/p1/receipts';
  const lines = [{ orderLineId: 'l1', quantity: 5 }];

  it('ağ hatasından sonra aynı niyet aynı anahtarla yeniden gönderilir; başarıdan sonra anahtar yenilenir', async () => {
    let attempt = 0;
    const api = mockApi([
      {
        method: 'POST',
        path,
        reply: () => {
          attempt += 1;
          return attempt === 1 ? Promise.reject(new TypeError('Failed to fetch')) : json({ id: `gr-${attempt}` }, { status: 201 });
        },
      },
    ]);
    const { result } = renderHook(() => useCommand(receive));

    await act(async () => {
      await result.current.run(lines);
    });
    expect(result.current.problem?.isNetwork).toBe(true);

    await act(async () => {
      await result.current.run(lines);
    });
    expect(result.current.problem).toBeNull();

    await act(async () => {
      await result.current.run(lines);
    });

    const keys = api.callsTo('POST', path).map((c) => c.headers['idempotency-key']);
    expect(keys).toHaveLength(3);
    expect(keys[0]).toMatch(/^[A-Za-z0-9_-]{16,128}$/);
    expect(keys[1]).toBe(keys[0]);
    expect(keys[2]).not.toBe(keys[0]);
  });

  it('503 sonrasında anahtarı korur, kesin retten (409) sonra yeniler', async () => {
    const replies = [() => problem(503, 'retry_later'), () => problem(409, 'exceeds_remaining'), () => json({ id: 'gr-1' }, { status: 201 })];
    let index = 0;
    const api = mockApi([{ method: 'POST', path, reply: () => (replies[index++] ?? replies[2]!)() }]);
    const { result } = renderHook(() => useCommand(receive));

    for (let i = 0; i < 3; i += 1) {
      await act(async () => {
        await result.current.run(lines);
      });
    }

    const keys = api.callsTo('POST', path).map((c) => c.headers['idempotency-key']);
    expect(keys[1]).toBe(keys[0]);
    expect(keys[2]).not.toBe(keys[1]);
  });

  it('form değiştiğinde (farklı içerik) yeni anahtar kullanır', async () => {
    const api = mockApi([{ method: 'POST', path, reply: () => problem(503, 'retry_later') }]);
    const { result } = renderHook(() => useCommand(receive));

    await act(async () => {
      await result.current.run(lines);
    });
    await act(async () => {
      await result.current.run([{ orderLineId: 'l1', quantity: 6 }]);
    });

    const keys = api.callsTo('POST', path).map((c) => c.headers['idempotency-key']);
    expect(keys[1]).not.toBe(keys[0]);
  });

  it('sonuç yalnız sunucu yanıtından sonra bildirilir ve istek sürerken ikinci gönderim yok sayılır', async () => {
    let release: (response: Response) => void = () => undefined;
    const api = mockApi([{ method: 'POST', path, reply: () => new Promise<Response>((resolve) => (release = resolve)) }]);
    const seen: string[] = [];
    const { result } = renderHook(() => useCommand(receive, { onSuccess: (receipt) => void seen.push(receipt.id) }));

    let first: Promise<unknown> = Promise.resolve();
    act(() => {
      first = result.current.run(lines);
    });
    expect(result.current.pending).toBe(true);

    await act(async () => {
      expect(await result.current.run(lines)).toBeUndefined();
    });
    expect(seen).toEqual([]);

    await act(async () => {
      // The CSRF token is fetched first, so the command request may not have started yet.
      await Promise.resolve();
      await new Promise((resolve) => setTimeout(resolve, 0));
      release(json({ id: 'gr-9' }, { status: 201 }));
      await first;
    });

    expect(api.callsTo('POST', path)).toHaveLength(1);
    expect(seen).toEqual(['gr-9']);
    expect(result.current.pending).toBe(false);
  });
});
