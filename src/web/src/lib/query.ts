import { QueryClient, useQuery, useQueryClient } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import { useCallback } from 'react';
import { ApiProblem } from '../api/problem';
import { useUserKey } from '../auth/session';

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // Stock and document figures must be current: data is refetched whenever a view mounts.
        staleTime: 0,
        gcTime: 5 * 60 * 1000,
        refetchOnWindowFocus: true,
        // Only failures that cannot have a definite cause are retried, and only once.
        retry: (failureCount, error) => error instanceof ApiProblem && error.isRetryable && failureCount < 1,
      },
      mutations: { retry: false },
    },
  });
}

/** A query whose key is scoped to the signed-in user and whose request is cancelled with the view. */
export function useApiQuery<T>(
  keyParts: readonly unknown[],
  fetcher: (signal: AbortSignal) => Promise<T>,
  options: { enabled?: boolean; refetchInterval?: number } = {},
): UseQueryResult<T, Error> {
  const userKey = useUserKey();
  return useQuery<T, Error>({
    queryKey: userKey(...keyParts),
    queryFn: ({ signal }) => fetcher(signal),
    enabled: options.enabled ?? true,
    ...(options.refetchInterval !== undefined ? { refetchInterval: options.refetchInterval } : {}),
  });
}

/** Invalidates every cached query of the signed-in user whose key starts with the given parts. */
export function useInvalidate(): (...keyParts: readonly unknown[]) => Promise<void> {
  const client = useQueryClient();
  const userKey = useUserKey();
  return useCallback((...keyParts: readonly unknown[]) => client.invalidateQueries({ queryKey: userKey(...keyParts) }), [client, userKey]);
}

/** Writes a document the server just returned into the cache, so the page shows the confirmed state at once. */
export function useSetCached(): <T>(keyParts: readonly unknown[], value: T) => void {
  const client = useQueryClient();
  const userKey = useUserKey();
  return useCallback(<T,>(keyParts: readonly unknown[], value: T) => void client.setQueryData(userKey(...keyParts), value), [client, userKey]);
}
