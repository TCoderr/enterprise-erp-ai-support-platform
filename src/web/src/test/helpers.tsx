import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import type { RenderResult } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { vi } from 'vitest';
import type { Me, Role, WarehouseAccess } from '../api/types';
import { MeProvider } from '../auth/session';
import { AssistProvider, WarehouseProvider } from '../shell/context';

// ---------------------------------------------------------------- fetch double
export interface RecordedCall {
  method: string;
  /** Path with query string, e.g. "/api/sales/orders?status=Draft". */
  url: string;
  path: string;
  query: URLSearchParams;
  headers: Record<string, string>;
  body: unknown;
  credentials: RequestCredentials | undefined;
}

export type Reply = Response | Promise<Response>;

export interface RouteDef {
  method: string;
  path: string | RegExp;
  reply: (call: RecordedCall) => Reply;
}

export function json(body: unknown, init: { status?: number; headers?: Record<string, string> } = {}): Response {
  return new Response(JSON.stringify(body), {
    status: init.status ?? 200,
    headers: { 'Content-Type': 'application/json; charset=utf-8', ...init.headers },
  });
}

export function noContent(): Response {
  return new Response(null, { status: 204 });
}

interface ProblemExtra {
  title?: string;
  detail?: string;
  userCode?: string;
  errors?: { field: string; code: string; detail: string }[];
  headers?: Record<string, string>;
}

export function problem(status: number, code: string, extra: ProblemExtra = {}): Response {
  const body: Record<string, unknown> = {
    type: `https://kantar.invalid/problems/${code}`,
    title: extra.title ?? code,
    status,
    code,
    correlationId: '0192f0c1a2b37c4d8e9f000000000001',
  };
  if (extra.userCode !== undefined) {
    body.userCode = extra.userCode;
  }

  if (extra.detail !== undefined) {
    body.detail = extra.detail;
  }

  if (extra.errors !== undefined) {
    body.errors = extra.errors;
  }

  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/problem+json; charset=utf-8', ...extra.headers },
  });
}

export interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason: unknown) => void;
}

export function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

export interface ApiDouble {
  calls: RecordedCall[];
  /** Calls matching a method and path (string equality on the path without query, or a RegExp). */
  callsTo: (method: string, path: string | RegExp) => RecordedCall[];
  /** Routes added later win over earlier ones, so a test can change the server's behaviour mid-way. */
  on: (method: string, path: string | RegExp, reply: (call: RecordedCall) => Reply) => void;
  /** When true the double behaves like a response already on the wire: aborting does not stop it. */
  ignoreAbort: boolean;
}

function matches(route: RouteDef, method: string, path: string): boolean {
  if (route.method !== method) {
    return false;
  }

  return typeof route.path === 'string' ? route.path === path : route.path.test(path);
}

/**
 * Replaces global fetch with a routed double that records every request. An unmatched request fails
 * the test loudly, so no test passes by accident against an endpoint it did not declare.
 */
export function mockApi(initial: RouteDef[] = []): ApiDouble {
  const routes: RouteDef[] = [{ method: 'GET', path: '/api/auth/csrf', reply: () => json({ token: 'csrf-anon' }) }, ...initial];
  const calls: RecordedCall[] = [];
  const state = { ignoreAbort: false };

  const fetchDouble = async (input: RequestInfo | URL, init: RequestInit = {}): Promise<Response> => {
    const raw = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    const parsed = new URL(raw, 'https://kantar.test');
    const method = (init.method ?? 'GET').toUpperCase();
    const headers: Record<string, string> = {};
    new Headers(init.headers).forEach((value, key) => {
      headers[key.toLowerCase()] = value;
    });

    const call: RecordedCall = {
      method,
      url: `${parsed.pathname}${parsed.search}`,
      path: parsed.pathname,
      query: parsed.searchParams,
      headers,
      body: typeof init.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined,
      credentials: init.credentials,
    };
    calls.push(call);

    const signal = state.ignoreAbort ? undefined : (init.signal ?? undefined);
    const aborted = () => new DOMException('The operation was aborted.', 'AbortError');
    if (signal?.aborted === true) {
      throw aborted();
    }

    let route: RouteDef | undefined;
    for (let i = routes.length - 1; i >= 0; i -= 1) {
      const candidate = routes[i];
      if (candidate !== undefined && matches(candidate, method, parsed.pathname)) {
        route = candidate;
        break;
      }
    }

    if (route === undefined) {
      throw new Error(`Beklenmeyen istek: ${method} ${call.url}`);
    }

    const reply = route.reply(call);
    if (signal === undefined) {
      return reply;
    }

    return new Promise<Response>((resolve, reject) => {
      const onAbort = () => reject(aborted());
      signal.addEventListener('abort', onAbort, { once: true });
      Promise.resolve(reply).then(
        (response) => {
          signal.removeEventListener('abort', onAbort);
          resolve(response);
        },
        (error: unknown) => {
          signal.removeEventListener('abort', onAbort);
          reject(error instanceof Error ? error : new Error(String(error)));
        },
      );
    });
  };

  vi.stubGlobal('fetch', vi.fn(fetchDouble));

  return {
    calls,
    get ignoreAbort() {
      return state.ignoreAbort;
    },
    set ignoreAbort(value: boolean) {
      state.ignoreAbort = value;
    },
    callsTo: (method, path) => calls.filter((call) => matches({ method, path, reply: () => noContent() }, call.method, call.path)),
    on: (method, path, reply) => {
      routes.push({ method, path, reply });
    },
  };
}

// ---------------------------------------------------------------- fixtures
export const ANKARA: WarehouseAccess = { id: '11111111-1111-7111-8111-111111111111', code: 'ANK-01', name: 'Ankara Merkez Depo', level: 'Supervise' };
export const ISTANBUL: WarehouseAccess = { id: '22222222-2222-7222-8222-222222222222', code: 'IST-02', name: 'İstanbul Tuzla Depo', level: 'Operate' };

export function makeMe(overrides: Partial<Me> & { roles?: Role[] } = {}): Me {
  return {
    id: 'aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa',
    userName: 'deniz.aksoy',
    displayName: 'Deniz Aksoy',
    roles: ['SalesManager'],
    warehouses: [ANKARA],
    mustChangePassword: false,
    poApprovalLimit: 0,
    ...overrides,
  };
}

export function testQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity, refetchOnWindowFocus: false }, mutations: { retry: false } },
  });
}

interface ScreenOptions {
  me?: Me;
  /** Route pattern the element is mounted at. */
  path?: string;
  /** Initial URL. */
  url?: string;
  queryClient?: QueryClient;
}

/** Mounts a screen with everything the authenticated shell provides, minus the shell chrome. */
export function renderScreen(element: ReactElement, options: ScreenOptions = {}): RenderResult & { queryClient: QueryClient } {
  const queryClient = options.queryClient ?? testQueryClient();
  const me = options.me ?? makeMe();
  const result = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[options.url ?? '/']}>
        <MeProvider me={me}>
          <WarehouseProvider>
            <AssistProvider>
              <Routes>
                <Route path={options.path ?? '/'} element={element} />
                <Route path="*" element={<p>başka sayfa</p>} />
              </Routes>
            </AssistProvider>
          </WarehouseProvider>
        </MeProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return Object.assign(result, { queryClient });
}
