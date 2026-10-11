import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import type { RefObject } from 'react';

/** Tracks a CSS media query. Returns false where matchMedia is unavailable. */
export function useMediaQuery(query: string): boolean {
  const subscribe = useCallback(
    (onChange: () => void) => {
      if (typeof window.matchMedia !== 'function') {
        return () => undefined;
      }

      const list = window.matchMedia(query);
      list.addEventListener('change', onChange);
      return () => list.removeEventListener('change', onChange);
    },
    [query],
  );
  const read = useCallback(() => (typeof window.matchMedia === 'function' ? window.matchMedia(query).matches : false), [query]);
  return useSyncExternalStore(subscribe, read, () => false);
}

/** Calls the handler when Escape is pressed while `active` is true. */
export function useEscape(active: boolean, onEscape: () => void): void {
  useEffect(() => {
    if (!active) {
      return undefined;
    }

    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onEscape();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [active, onEscape]);
}

export interface Popover {
  open: boolean;
  anchorRef: RefObject<HTMLDivElement | null>;
  buttonRef: RefObject<HTMLButtonElement | null>;
  toggle: () => void;
  close: () => void;
}

/** A small non-modal layer (menu, notification list): closes on Escape, outside press and focus leaving. */
export function usePopover(): Popover {
  const [open, setOpen] = useState(false);
  const anchorRef = useRef<HTMLDivElement | null>(null);
  const buttonRef = useRef<HTMLButtonElement | null>(null);

  const close = useCallback(() => setOpen(false), []);
  const closeAndReturn = useCallback(() => {
    setOpen(false);
    buttonRef.current?.focus();
  }, []);

  useEscape(open, closeAndReturn);

  useEffect(() => {
    if (!open) {
      return undefined;
    }

    const onPointer = (event: PointerEvent) => {
      if (anchorRef.current !== null && event.target instanceof Node && !anchorRef.current.contains(event.target)) {
        setOpen(false);
      }
    };
    const onFocus = (event: FocusEvent) => {
      if (anchorRef.current !== null && event.target instanceof Node && !anchorRef.current.contains(event.target)) {
        setOpen(false);
      }
    };
    document.addEventListener('pointerdown', onPointer);
    document.addEventListener('focusin', onFocus);
    return () => {
      document.removeEventListener('pointerdown', onPointer);
      document.removeEventListener('focusin', onFocus);
    };
  }, [open]);

  return { open, anchorRef, buttonRef, toggle: () => setOpen((value) => !value), close };
}
