import type { QueryClient } from '@tanstack/react-query';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { authApi } from '../api/endpoints';
import { resetClientSession, setCsrfToken, setSessionListeners } from '../api/http';
import type { ApiProblem } from '../api/problem';
import { asProblem, isAbort } from '../api/problem';
import type { Me } from '../api/types';

export type AnonymousNotice = 'expired' | 'logout-unconfirmed' | 'signed-out';

export type SessionState =
  | { status: 'loading' }
  | { status: 'unreachable'; problem: ApiProblem }
  | { status: 'anonymous'; notice?: AnonymousNotice }
  | { status: 'authenticated'; me: Me };

interface SessionValue {
  state: SessionState;
  /** Rejects with an ApiProblem when the credentials are refused or the server cannot be reached. */
  login: (userName: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  /** Re-reads the caller's identity, roles and warehouse grants from the server. */
  refresh: () => Promise<void>;
  retry: () => void;
}

const SessionContext = createContext<SessionValue | null>(null);

// Where the user was heading when the session ended. Memory only; never written to storage.
let intendedPath: string | null = null;

export function rememberIntendedPath(path: string): void {
  if (path !== '/giris' && path !== '/') {
    intendedPath = path;
  }
}

export function takeIntendedPath(): string | null {
  const path = intendedPath;
  intendedPath = null;
  return path;
}

interface ProviderProps {
  queryClient: QueryClient;
  /** Current in-app location (path + query), used to return there after signing in again. */
  currentPath: () => string;
  children: ReactNode;
}

export function SessionProvider({ queryClient, currentPath, children }: ProviderProps) {
  const [state, setState] = useState<SessionState>({ status: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const userId = useRef<string | null>(null);

  /** Drops everything that belonged to the signed-in user: requests, cache and CSRF token. */
  const wipe = useCallback(() => {
    resetClientSession();
    void queryClient.cancelQueries();
    queryClient.clear();
  }, [queryClient]);

  const end = useCallback(
    (notice: AnonymousNotice) => {
      if (notice === 'expired' && userId.current !== null) {
        rememberIntendedPath(currentPath());
      }

      userId.current = null;
      wipe();
      setState({ status: 'anonymous', notice });
    },
    [currentPath, wipe],
  );

  const adopt = useCallback(
    (me: Me) => {
      // Another user on the same terminal (or in another tab): nothing cached may carry over.
      if (userId.current !== null && userId.current !== me.id) {
        wipe();
      }

      userId.current = me.id;
      setState({ status: 'authenticated', me });
    },
    [wipe],
  );

  const refresh = useCallback(async () => {
    try {
      adopt(await authApi.me());
    } catch (error) {
      if (isAbort(error)) {
        return;
      }

      if (asProblem(error).status === 401) {
        end('expired');
      }
    }
  }, [adopt, end]);

  // Session bootstrap.
  useEffect(() => {
    let cancelled = false;
    authApi
      .me()
      .then((me) => {
        if (!cancelled) {
          adopt(me);
        }
      })
      .catch((error: unknown) => {
        if (cancelled || isAbort(error)) {
          return;
        }

        const problem = asProblem(error);
        if (problem.status === 401) {
          rememberIntendedPath(currentPath());
          setState({ status: 'anonymous' });
        } else {
          setState({ status: 'unreachable', problem });
        }
      });
    return () => {
      cancelled = true;
    };
    // currentPath is read once at start-up on purpose.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [adopt, attempt]);

  useEffect(() => {
    setSessionListeners({
      onUnauthenticated: () => end('expired'),
      onPasswordChangeRequired: () => {
        setState((previous) =>
          previous.status === 'authenticated' && !previous.me.mustChangePassword
            ? { status: 'authenticated', me: { ...previous.me, mustChangePassword: true } }
            : previous,
        );
      },
    });
    return () => setSessionListeners({});
  }, [end]);

  // A shared terminal can change hands in another tab; check who we are when the tab is shown again.
  useEffect(() => {
    const onVisible = () => {
      if (document.visibilityState === 'visible' && userId.current !== null) {
        void refresh();
      }
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => document.removeEventListener('visibilitychange', onVisible);
  }, [refresh]);

  const login = useCallback(
    async (userName: string, password: string) => {
      const result = await authApi.login(userName, password);
      queryClient.clear();
      setCsrfToken(result.csrfToken);
      adopt(result.user);
    },
    [adopt, queryClient],
  );

  const logout = useCallback(async () => {
    let confirmed = true;
    try {
      await authApi.logout();
    } catch (error) {
      if (isAbort(error)) {
        return;
      }

      // 401 means the session was already gone, which is what the user asked for.
      confirmed = asProblem(error).status === 401;
    }

    end(confirmed ? 'signed-out' : 'logout-unconfirmed');
  }, [end]);

  const retry = useCallback(() => {
    setState({ status: 'loading' });
    setAttempt((n) => n + 1);
  }, []);

  const value = useMemo<SessionValue>(() => ({ state, login, logout, refresh, retry }), [state, login, logout, refresh, retry]);
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionValue {
  const value = useContext(SessionContext);
  if (value === null) {
    throw new Error('useSession, SessionProvider içinde kullanılmalıdır');
  }

  return value;
}

const MeContext = createContext<Me | null>(null);

export function MeProvider({ me, children }: { me: Me; children: ReactNode }) {
  return <MeContext.Provider value={me}>{children}</MeContext.Provider>;
}

/** The signed-in user. Only available below the authenticated shell. */
export function useMe(): Me {
  const me = useContext(MeContext);
  if (me === null) {
    throw new Error('useMe, oturum açılmış kabuk içinde kullanılmalıdır');
  }

  return me;
}

/**
 * Query keys always start with the signed-in user's id, so data cached for one user can never be
 * served to another even before the cache is cleared.
 */
export function useUserKey(): (...parts: readonly unknown[]) => readonly unknown[] {
  const me = useMe();
  return useCallback((...parts: readonly unknown[]) => [me.id, ...parts], [me.id]);
}
