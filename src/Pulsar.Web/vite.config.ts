import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

// Panel URL used by the dev server proxy. Override with PULSAR_PANEL_URL.
const panelUrl = process.env.PULSAR_PANEL_URL ?? 'http://localhost:5180';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    // The panel serves the SPA from its wwwroot as static web assets.
    outDir: fileURLToPath(new URL('../Pulsar.Api/wwwroot', import.meta.url)),
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': { target: panelUrl, changeOrigin: false },
      '/hubs': { target: panelUrl, changeOrigin: false, ws: true },
      '/agent': { target: panelUrl, changeOrigin: false, ws: true },
      '/health': { target: panelUrl, changeOrigin: false },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
