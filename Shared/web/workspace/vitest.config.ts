import path from 'node:path'
import { defineConfig } from 'vitest/config'

// The engine registry's descriptors are read as text from Shared/src (syncEngines.ts); a test in a browser-like environment is served by Vite's
// dev server, which refuses a file outside the allowed folders, so the npm workspace and that folder are allowed.
export default defineConfig({
  server: { fs: { allow: [path.resolve(import.meta.dirname, '..'), path.resolve(import.meta.dirname, '../../src/DbDataKit.Connections.Descriptor/engines')] } },
})
