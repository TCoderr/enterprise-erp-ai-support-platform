import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach, vi } from 'vitest';
import { resetClientSession, setSessionListeners } from '../api/http';
import { takeIntendedPath } from '../auth/session';

afterEach(() => {
  cleanup();
  resetClientSession();
  setSessionListeners({});
  takeIntendedPath();
  vi.unstubAllGlobals();
});

// jsdom ships window.scrollTo as a "not implemented" stub that logs; the shell only needs it to exist.
Object.defineProperty(window, 'scrollTo', { value: () => undefined, writable: true });
