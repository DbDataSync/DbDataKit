import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// Proxies API calls to the console host's dev port, so the app can call relative /api paths in dev without a CORS setup.
// The host serves HTTPS with a certificate of its own making, which the dev proxy accepts (secure: false) for this address only.
const apiTarget = process.env.DBDATAKIT_CONSOLE_URL ?? 'https://localhost:8480'

export default defineConfig({
  plugins: [react()],
  test: { environment: 'node', include: ['src/**/*.test.{ts,tsx}'], passWithNoTests: true },
  server: {
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true, secure: false },
    },
  },
})
