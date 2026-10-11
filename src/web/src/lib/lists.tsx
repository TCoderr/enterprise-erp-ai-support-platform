import { useInfiniteQuery } from '@tanstack/react-query';
import type { InfiniteData } from '@tanstack/react-query';
import { useCallback, useEffect } from 'react';
import type { ReactNode } from 'react';
import { useSearchParams } from 'react-router-dom';
import type { ApiProblem } from '../api/problem';
import { asProblem } from '../api/problem';
import type { Page } from '../api/types';
import { useUserKey } from '../auth/session';
import { ForbiddenState, LoadingRegion, NotFoundState, ProblemAlert } from '../components/states';
import { Button } from '../components/ui';

/** Filters, tabs and paging live in the URL query so a view can be reloaded, bookmarked and shared. */
export function useUrlState(): {
  params: URLSearchParams;
  /** Sets or (with null / empty) removes parameters. Changing a filter restarts paging. */
  update: (changes: Record<string, string | null>, options?: { replace?: boolean; keepPaging?: boolean }) => void;
} {
  const [params, setParams] = useSearchParams();
  const update = useCallback(
    (changes: Record<string, string | null>, options?: { replace?: boolean; keepPaging?: boolean }) => {
      setParams(
        (previous) => {
          const next = new URLSearchParams(previous);
          if (options?.keepPaging !== true) {
            for (const key of [...next.keys()]) {
              if (key.startsWith('sayfa')) {
                next.delete(key);
              }
            }
          }

          for (const [key, value] of Object.entries(changes)) {
            if (value === null || value === '') {
              next.delete(key);
            } else {
              next.set(key, value);
            }
          }

          return next;
        },
        { replace: options?.replace ?? false },
      );
    },
    [setParams],
  );
  return { params, update };
}

const MAX_PAGES = 40;

export interface CursorList<T> {
  items: T[];
  /** First page has not arrived yet. */
  isLoading: boolean;
  problem: ApiProblem | null;
  hasMore: boolean;
  loadingMore: boolean;
  loadMore: () => void;
  refetch: () => void;
  isFetching: boolean;
}

/**
 * Cursor-paged list with a "daha fazla" action. The number of loaded pages is mirrored in the URL
 * (`sayfa=3`), so a reload shows the same rows; the opaque cursor itself never leaves memory.
 */
export function useCursorList<T>(
  keyParts: readonly unknown[],
  fetchPage: (cursor: string | undefined, signal: AbortSignal) => Promise<Page<T>>,
  options: { enabled?: boolean; pageParam?: string } = {},
): CursorList<T> {
  const userKey = useUserKey();
  const { params, update } = useUrlState();
  const pageParam = options.pageParam ?? 'sayfa';
  const wanted = Math.min(MAX_PAGES, Math.max(1, Number.parseInt(params.get(pageParam) ?? '1', 10) || 1));

  const query = useInfiniteQuery<Page<T>, Error, InfiniteData<Page<T>, string | undefined>, readonly unknown[], string | undefined>({
    queryKey: userKey(...keyParts),
    queryFn: ({ pageParam: cursor, signal }) => fetchPage(cursor, signal),
    initialPageParam: undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: options.enabled ?? true,
  });

  const loaded = query.data?.pages.length ?? 0;
  const { hasNextPage, isFetching, fetchNextPage } = query;
  useEffect(() => {
    if (loaded > 0 && loaded < wanted && hasNextPage && !isFetching) {
      void fetchNextPage();
    }
  }, [fetchNextPage, hasNextPage, isFetching, loaded, wanted]);

  return {
    items: query.data?.pages.flatMap((page) => page.items) ?? [],
    isLoading: query.isPending && (options.enabled ?? true),
    problem: query.isError ? asProblem(query.error) : null,
    hasMore: hasNextPage,
    loadingMore: query.isFetchingNextPage,
    loadMore: () => update({ [pageParam]: String(loaded + 1) }, { replace: true, keepPaging: true }),
    refetch: () => void query.refetch(),
    isFetching,
  };
}

/** Footer of a paged list: how many rows are shown and the action that loads the next page. */
export function LoadMore<T>({ list, noun }: { list: CursorList<T>; noun: string }) {
  if (list.items.length === 0) {
    return null;
  }

  return (
    <div className="loadmore">
      <span aria-live="polite">
        {list.items.length} {noun} gösteriliyor{list.hasMore ? '' : ' · listenin sonu'}
      </span>
      {list.hasMore && (
        <Button size="small" onClick={list.loadMore} pending={list.loadingMore} pendingLabel="Yükleniyor…">
          Daha fazla {noun} yükle
        </Button>
      )}
    </div>
  );
}

interface ListRegionProps<T> {
  list: CursorList<T>;
  loadingLabel: string;
  /** Noun for the footer ("sipariş", "hareket"). */
  noun: string;
  /** What to show when the server returned no rows. */
  empty: ReactNode;
  /** Noun for the not-found state when the scope (for example the warehouse) is not accessible. */
  scopeNoun?: string;
  children: (items: T[]) => ReactNode;
}

/** The standard frame of a list: loading, failed, empty, rows + paging. */
export function ListRegion<T>({ list, loadingLabel, noun, empty, scopeNoun = 'Kayıt', children }: ListRegionProps<T>) {
  if (list.isLoading) {
    return <LoadingRegion label={loadingLabel} />;
  }

  if (list.problem !== null && list.items.length === 0) {
    if (list.problem.isNotFound) {
      return <NotFoundState what={scopeNoun} />;
    }

    if (list.problem.isForbidden) {
      return <ForbiddenState problem={list.problem} />;
    }

    return <ProblemAlert problem={list.problem} title="Liste yüklenemedi" onRetry={list.refetch} retryPending={list.isFetching} />;
  }

  if (list.items.length === 0) {
    return <>{empty}</>;
  }

  return (
    <>
      <section className="region tablewrap" aria-busy={list.isFetching || undefined}>
        {children(list.items)}
        <LoadMore list={list} noun={noun} />
      </section>
      {list.problem !== null && (
        <ProblemAlert problem={list.problem} title="Sonraki sayfa yüklenemedi" onRetry={list.refetch} retryPending={list.isFetching} />
      )}
    </>
  );
}
