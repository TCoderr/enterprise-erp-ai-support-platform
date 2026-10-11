import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// The API serves the built app from the same origin under a strict CSP
// (script-src 'self'; style-src 'self'; font-src 'self'), so nothing may be inlined:
// assets are always emitted as files and index.html carries no inline script or style.
//
// KANTAR_DEV_API points the dev proxy at another running API (for example the e2e stack).
const apiTarget = process.env.KANTAR_DEV_API ?? 'https://localhost:8943';
const proxy = {
  '/api': { target: apiTarget, secure: false, changeOrigin: false },
  '/health': { target: apiTarget, secure: false, changeOrigin: false },
};

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5193,
    strictPort: true,
    proxy,
  },
  preview: {
    port: 5193,
    strictPort: true,
    proxy,
  },
  build: {
    outDir: 'dist',
    assetsInlineLimit: 0,
    sourcemap: false,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    css: false,
    pool: 'threads',
    maxWorkers: 1,
    fileParallelism: false,
    restoreMocks: true,
    testTimeout: 15000,
  },
});
