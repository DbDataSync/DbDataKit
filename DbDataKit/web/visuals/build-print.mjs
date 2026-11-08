// Builds the print page's two files, print.js and print.css, from src/print/main.tsx (plan 32 DBR15).
//   npm run build:print -w @dbdatakit/visuals [outDir]      (default: dist/print)
// The `views` module serves them from views.export.page_assets. One script (everything bundled, the chart library included) and one stylesheet; the page loads no outside address.
import { build } from 'vite'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const out = resolve(process.argv[2] ?? join(here, 'dist', 'print'))
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
    cssCodeSplit: false,
    copyPublicDir: false,
    lib: { entry: join(here, 'src', 'print', 'main.tsx'), name: 'DbDataKitPrint', formats: ['iife'], fileName: () => 'print.js', cssFileName: 'print' },
    rolldownOptions: { output: { codeSplitting: false } },
  },
})
console.log(`print page built into ${out}`)
