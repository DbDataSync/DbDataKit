import path from 'node:path'
import { generateAllDocs } from './moduleDocs.mjs'

const TYPES = { '.md': 'text/markdown; charset=utf-8', '.json': 'application/json', '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.gif': 'image/gif', '.webp': 'image/webp' }

/**
 * Puts every module's generated docs (see `moduleDocs.mjs`) under `/docs/` in the console's build, and serves them from the dev server at the same addresses, so the console shows them whichever way it runs.
 * A module's docs that are not sound fail the build with the problems named.
 */
export function moduleDocs(webRoot) {
  const generate = () => {
    const { files, problems } = generateAllDocs(webRoot)
    if (problems.length > 0) throw new Error(`The docs of the modules are not sound:\n  ${problems.join('\n  ')}`)
    return files
  }
  return {
    name: 'dbdatakit-module-docs',
    generateBundle() {
      for (const [file, source] of generate()) this.emitFile({ type: 'asset', fileName: `docs/${file}`, source })
    },
    configureServer(server) {
      server.middlewares.use((request, response, next) => {
        const url = decodeURIComponent((request.url ?? '').split('?')[0])
        if (!url.startsWith('/docs/')) return next()
        const content = generate().get(url.slice('/docs/'.length))
        if (content === undefined) return next()
        response.setHeader('Content-Type', TYPES[path.extname(url)] ?? 'application/octet-stream')
        response.end(content)
      })
    },
  }
}
