import react from '@vitejs/plugin-react'
import path from 'node:path'
import { defineConfig } from 'vitest/config'
import { moduleDocs } from './build/moduleDocsPlugin.mjs'

// Proxies API calls to the console host's dev port, so the app can call relative /api paths in dev without a CORS setup.
// The host serves HTTPS with a certificate of its own making, which the dev proxy accepts (secure: false) for this address only.
const apiTarget = process.env.DBDATAKIT_CONSOLE_URL ?? 'https://localhost:8480'

export default defineConfig({
  // Every module's docs, generated from the module's own docs folder into /docs/<module>/ (plan 13a, U11).
  plugins: [react(), moduleDocs(path.resolve(import.meta.dirname, '..'))],
  test: { environment: 'node', include: ['src/**/*.test.{ts,tsx}', 'build/**/*.test.mjs'], passWithNoTests: true },
  server: {
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true, secure: false },
    },
  },
})
