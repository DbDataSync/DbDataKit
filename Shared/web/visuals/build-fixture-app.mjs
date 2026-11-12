// Builds the fixture app of the SDK (views-sdk/fixture) into dist/fixture-app: index.html and app.js (plan 32 DBR32's exporter tests load it as an app's built files).
//   npm run build:fixture-app -w @dbdatakit/visuals [outDir]
import { build } from 'vite'
import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const out = resolve(process.argv[2] ?? join(here, 'dist', 'fixture-app'))
await build({
  configFile: false,
  root: here,
  logLevel: 'warn',
  define: { 'process.env.NODE_ENV': '"production"' },
  oxc: { jsx: { runtime: 'automatic' } },
  build: {
    outDir: out,
    emptyOutDir: true,
    target: 'chrome120',
    minify: true,
    copyPublicDir: false,
    lib: { entry: join(here, '..', 'views-sdk', 'fixture', 'main.tsx'), name: 'FixtureApp', formats: ['iife'], fileName: () => 'app.js' },
    rolldownOptions: { output: { codeSplitting: false } },
  },
})
mkdirSync(out, { recursive: true })
writeFileSync(join(out, 'index.html'), '<!doctype html><html lang="en"><head><meta charset="utf-8"><title>fixture app</title></head><body><script src="app.js"></script></body></html>\n')
console.log(`fixture app built into ${out}`)
