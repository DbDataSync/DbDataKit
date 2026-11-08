import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// One plain script and one stylesheet, named as the apps host serves them (`ViewsRuntime`): the sandboxed document loads
// `views-app.js` and `views-app.css` from its own origin and nothing else, and Vega is bundled in (no dynamic chunk to fetch).
export default defineConfig({
  plugins: [react()],
  define: { 'process.env.NODE_ENV': JSON.stringify('production') },
  build: {
    lib: { entry: 'src/main.tsx', formats: ['iife'], name: 'DbDataViewApp', fileName: () => 'views-app.js', cssFileName: 'views-app' },
    cssCodeSplit: false,
    chunkSizeWarningLimit: 4000,
  },
  test: { environment: 'node', include: ['src/**/*.test.{ts,tsx}'] },
})
