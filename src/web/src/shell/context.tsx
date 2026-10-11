import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Link, NavLink, useNavigate, useSearchParams } from 'react-router-dom';
import type { LinkProps, NavLinkProps } from 'react-router-dom';
import type { WarehouseAccess } from '../api/types';
import { useMe } from '../auth/session';

// Two pieces of view state live in the URL query and travel with every in-app link:
//   depo   – code of the selected warehouse (scopes list screens)
//   yardim – "1" while the Kaynaklı Yardım panel is open
// Nothing is written to browser storage.
const DEPOT_PARAM = 'depo';
const ASSIST_PARAM = 'yardim';

function carry(to: string, current: URLSearchParams, depot: string | null): string {
  const [pathAndQuery = '', hash] = to.split('#');
  const [path = '', query] = pathAndQuery.split('?');
  const params = new URLSearchParams(query ?? '');
  if (depot !== null && !params.has(DEPOT_PARAM)) {
    params.set(DEPOT_PARAM, depot);
  }

  if (current.get(ASSIST_PARAM) === '1' && !params.has(ASSIST_PARAM)) {
    params.set(ASSIST_PARAM, '1');
  }

  const text = params.toString();
  return `${path}${text.length > 0 ? `?${text}` : ''}${hash !== undefined ? `#${hash}` : ''}`;
}

// ---------------------------------------------------------------- warehouse scope
interface WarehouseValue {
  warehouses: WarehouseAccess[];
  /** The warehouse list screens are scoped to; null when the user has no warehouse grant. */
  selected: WarehouseAccess | null;
  select: (code: string) => void;
  /** Adds the sticky view parameters to an in-app path. */
  href: (to: string) => string;
  /** Name of a warehouse the user has a grant for; falls back to a neutral label. */
  nameOf: (warehouseId: string) => string;
  codeOf: (warehouseId: string) => string | undefined;
}

const WarehouseContext = createContext<WarehouseValue | null>(null);

export function WarehouseProvider({ children }: { children: ReactNode }) {
  const me = useMe();
  const [params, setParams] = useSearchParams();
  const requested = params.get(DEPOT_PARAM);
  const selected = me.warehouses.find((w) => w.code === requested) ?? me.warehouses[0] ?? null;
  // The parameter is only worth carrying when there is a choice to remember.
  const sticky = me.warehouses.length > 1 && selected !== null ? selected.code : null;

  const select = useCallback(
    (code: string) => {
      setParams((previous) => {
        const next = new URLSearchParams(previous);
        next.set(DEPOT_PARAM, code);
        // A different warehouse is a different list: paging and row selections start over.
        for (const key of [...next.keys()]) {
          if (key.startsWith('sayfa') || key === 'urun') {
            next.delete(key);
          }
        }

        return next;
      });
    },
    [setParams],
  );

  const value = useMemo<WarehouseValue>(
    () => ({
      warehouses: me.warehouses,
      selected,
      select,
      href: (to: string) => carry(to, params, sticky),
      nameOf: (warehouseId: string) => me.warehouses.find((w) => w.id === warehouseId)?.name ?? 'Yetkiniz olmayan depo',
      codeOf: (warehouseId: string) => me.warehouses.find((w) => w.id === warehouseId)?.code,
    }),
    [me.warehouses, params, select, selected, sticky],
  );

  return <WarehouseContext.Provider value={value}>{children}</WarehouseContext.Provider>;
}

export function useWarehouse(): WarehouseValue {
  const value = useContext(WarehouseContext);
  if (value === null) {
    throw new Error('useWarehouse, WarehouseProvider içinde kullanılmalıdır');
  }

  return value;
}

/** In-app link that keeps the selected warehouse and the open panel. Ctrl/middle click work as usual. */
export function KLink({ to, ...rest }: Omit<LinkProps, 'to'> & { to: string }) {
  const { href } = useWarehouse();
  return <Link {...rest} to={href(to)} />;
}

export function KNavLink({ to, ...rest }: Omit<NavLinkProps, 'to'> & { to: string }) {
  const { href } = useWarehouse();
  return <NavLink {...rest} to={href(to)} />;
}

export function useGo(): (to: string, options?: { replace?: boolean }) => void {
  const navigate = useNavigate();
  const { href } = useWarehouse();
  return useCallback((to: string, options?: { replace?: boolean }) => void navigate(href(to), options), [href, navigate]);
}

// ---------------------------------------------------------------- Kaynaklı Yardım panel
export interface AssistTicket {
  id: string;
  number: string;
}

export interface AssistRequest {
  /** The ticket the panel was opened from: its module, error code and title hint the search. */
  ticket?: AssistTicket | undefined;
  /** Question text to start with (for example "STK-409 ne anlama geliyor?"). */
  prefill?: string | undefined;
}

interface AssistValue {
  isOpen: boolean;
  request: AssistRequest;
  /** Changes whenever the panel is opened anew, so its state starts clean. */
  session: number;
  open: (request?: AssistRequest) => void;
  close: () => void;
  clearTicket: () => void;
}

const AssistContext = createContext<AssistValue | null>(null);

export function AssistProvider({ children }: { children: ReactNode }) {
  const [params, setParams] = useSearchParams();
  const [request, setRequest] = useState<AssistRequest>({});
  const [session, setSession] = useState(0);
  const opener = useRef<HTMLElement | null>(null);
  const restore = useRef(false);
  const isOpen = params.get(ASSIST_PARAM) === '1';

  const open = useCallback(
    (next: AssistRequest = {}) => {
      opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      setRequest(next);
      setSession((n) => n + 1);
      setParams(
        (previous) => {
          const updated = new URLSearchParams(previous);
          updated.set(ASSIST_PARAM, '1');
          return updated;
        },
        { replace: true },
      );
    },
    [setParams],
  );

  const close = useCallback(() => {
    setRequest({});
    setParams(
      (previous) => {
        const updated = new URLSearchParams(previous);
        updated.delete(ASSIST_PARAM);
        return updated;
      },
      { replace: true },
    );
    restore.current = true;
  }, [setParams]);

  // Focus returns to the control that opened the panel — after the commit that closes it, because
  // until then that control may still sit inside the inert page behind the modal sheet.
  useEffect(() => {
    if (!isOpen && restore.current) {
      restore.current = false;
      const target = opener.current;
      opener.current = null;
      if (target !== null && target.isConnected) {
        target.focus();
      }
    }
  }, [isOpen]);

  const clearTicket = useCallback(() => setRequest((previous) => ({ prefill: previous.prefill })), []);

  const value = useMemo<AssistValue>(
    () => ({ isOpen, request, session, open, close, clearTicket }),
    [clearTicket, close, isOpen, open, request, session],
  );
  return <AssistContext.Provider value={value}>{children}</AssistContext.Provider>;
}

export function useAssist(): AssistValue {
  const value = useContext(AssistContext);
  if (value === null) {
    throw new Error('useAssist, AssistProvider içinde kullanılmalıdır');
  }

  return value;
}

/** For shared components that also render outside the shell (login screen). */
export function useAssistOptional(): AssistValue | null {
  return useContext(AssistContext);
}
