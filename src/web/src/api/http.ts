import { ApiProblem, SupersededError, networkProblem, problemFromBody } from './problem';

export type QueryValue = string | number | boolean | undefined | null;

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT';
  query?: Record<string, QueryValue> | undefined;
  body?: unknown;
  /** One key per user intent; the same key is sent again when that intent is retried. */
  idempotencyKey?: string | undefined;
  /** ETag of the document as the user saw it. */
  ifMatch?: string | undefined;
  signal?: AbortSignal | undefined;
  /** The session probe and the login call handle 401 themselves. */
  quiet401?: boolean;
}

export interface ApiResult<T> {
  data: T;
  status: number;
  /** Value of the ETag response header, if any. */
  etag: string | null;
  /** True when the server replayed a stored response for a repeated Idempotency-Key. */
  replayed: boolean;
}

interface SessionListeners {
  onUnauthenticated?: () => void;
  onPasswordChangeRequired?: () => void;
}

// Module state only: nothing here is ever written to browser storage.
let csrfToken: string | null = null;
let csrfPending: Promise<string> | null = null;
let epoch = 0;
let sessionAbort = new AbortController();
let listeners: SessionListeners = {};

export function setSessionListeners(next: SessionListeners): void {
  listeners = next;
}

export function setCsrfToken(token: string | null): void {
  csrfToken = token;
  csrfPending = null;
}

export function currentEpoch(): number {
  return epoch;
}

/**
 * Ends the client side of a session: in-flight requests are aborted, the CSRF token is dropped and
 * any response that still arrives for the previous session is discarded instead of delivered.
 */
export function resetClientSession(): void {
  epoch += 1;
  sessionAbort.abort();
  sessionAbort = new AbortController();
  csrfToken = null;
  csrfPending = null;
}

function buildUrl(path: string, query: Record<string, QueryValue> | undefined): string {
  if (!query) {
    return path;
  }

  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === '') {
      continue;
    }

    params.set(key, String(value));
  }

  const text = params.toString();
  return text.length > 0 ? `${path}?${text}` : path;
}

function linkSignals(signals: (AbortSignal | undefined)[]): { signal: AbortSignal; dispose: () => void } {
  const controller = new AbortController();
  const abort = () => controller.abort();
  const active = signals.filter((s): s is AbortSignal => s !== undefined);
  for (const signal of active) {
    if (signal.aborted) {
      controller.abort();
    } else {
      signal.addEventListener('abort', abort, { once: true });
    }
  }

  return {
    signal: controller.signal,
    dispose: () => {
      for (const signal of active) {
        signal.removeEventListener('abort', abort);
      }
    },
  };
}

async function readJson(response: Response): Promise<unknown> {
  const text = await response.text();
  if (text.length === 0) {
    return undefined;
  }

  try {
    return JSON.parse(text) as unknown;
  } catch {
    return undefined;
  }
}

async function fetchCsrfToken(startEpoch: number): Promise<string> {
  let response: Response;
  try {
    response = await fetch('/api/auth/csrf', {
      method: 'GET',
      credentials: 'same-origin',
      cache: 'no-store',
      headers: { Accept: 'application/json' },
      signal: sessionAbort.signal,
    });
  } catch (error) {
    if (epoch !== startEpoch) {
      throw new SupersededError();
    }

    if (error instanceof Error && error.name === 'AbortError') {
      throw error;
    }

    throw networkProblem();
  }

  const body = await readJson(response);
  if (epoch !== startEpoch) {
    throw new SupersededError();
  }

  if (!response.ok) {
    throw problemFromBody(response.status, body, response.headers.get('Retry-After'));
  }

  const token = typeof body === 'object' && body !== null ? (body as { token?: unknown }).token : undefined;
  if (typeof token !== 'string' || token.length === 0) {
    throw new ApiProblem({ status: response.status, code: 'invalid_response', title: 'Sunucudan beklenmeyen bir yanıt alındı' });
  }

  csrfToken = token;
  return token;
}

async function ensureCsrf(startEpoch: number): Promise<string> {
  if (csrfToken !== null) {
    return csrfToken;
  }

  if (csrfPending === null) {
    const pending = fetchCsrfToken(startEpoch).finally(() => {
      if (csrfPending === pending) {
        csrfPending = null;
      }
    });
    csrfPending = pending;
  }

  return csrfPending;
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<ApiResult<T>> {
  const startEpoch = epoch;
  const method = options.method ?? 'GET';
  const url = buildUrl(path, options.query);
  const linked = linkSignals([sessionAbort.signal, options.signal]);

  try {
    for (let attempt = 0; ; attempt += 1) {
      const headers: Record<string, string> = { Accept: 'application/json' };
      if (method !== 'GET') {
        headers['X-CSRF-Token'] = await ensureCsrf(startEpoch);
      }

      if (options.body !== undefined) {
        headers['Content-Type'] = 'application/json';
      }

      if (options.idempotencyKey !== undefined) {
        headers['Idempotency-Key'] = options.idempotencyKey;
      }

      if (options.ifMatch !== undefined) {
        headers['If-Match'] = options.ifMatch;
      }

      let response: Response;
      try {
        const init: RequestInit = {
          method,
          headers,
          credentials: 'same-origin',
          cache: 'no-store',
          signal: linked.signal,
        };
        if (options.body !== undefined) {
          init.body = JSON.stringify(options.body);
        }

        response = await fetch(url, init);
      } catch (error) {
        if (epoch !== startEpoch) {
          throw new SupersededError();
        }

        if (linked.signal.aborted || (error instanceof Error && error.name === 'AbortError')) {
          throw new DOMException('İstek iptal edildi', 'AbortError');
        }

        throw networkProblem();
      }

      const body = await readJson(response);

      // A response for a session that has ended (logout, 401, user switch) is never delivered.
      if (epoch !== startEpoch) {
        throw new SupersededError();
      }

      if (response.ok) {
        return {
          data: body as T,
          status: response.status,
          etag: response.headers.get('ETag'),
          replayed: response.headers.get('Idempotent-Replay') === 'true',
        };
      }

      const problem = problemFromBody(response.status, body, response.headers.get('Retry-After'));

      // The session CSRF secret can rotate (for example after a password change): refresh once.
      if (problem.code === 'csrf_failed' && attempt === 0 && method !== 'GET') {
        csrfToken = null;
        continue;
      }

      if (problem.status === 401 && options.quiet401 !== true) {
        listeners.onUnauthenticated?.();
      } else if (problem.status === 403 && problem.code === 'password_change_required') {
        listeners.onPasswordChangeRequired?.();
      }

      throw problem;
    }
  } finally {
    linked.dispose();
  }
}

export async function getJson<T>(path: string, query?: Record<string, QueryValue>, signal?: AbortSignal): Promise<T> {
  const result = await request<T>(path, { query, signal });
  return result.data;
}
