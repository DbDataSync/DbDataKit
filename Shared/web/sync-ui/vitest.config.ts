import path from 'node:path'
import { defineConfig } from 'vitest/config'

// The Designer's tests draw it inside the Workspace's frame, which reads the engine registry's descriptors as text from Shared/src; a test in a
// browser-like environment is served by Vite's dev server, which refuses a file outside the allowed folders, so the npm workspaces and that folder
// are allowed.
export default defineConfig({
  server: { fs: { allow: [path.resolve(import.meta.dirname, '..'), path.resolve(import.meta.dirname, '../../src/DbDataKit.Connections.Descriptor/engines')] } },
})
