import { ApiError } from '../api/http'

/** One page of a module's docs, in the order the module's index lists it. */
export interface DocPage {
  slug: string
  title: string
}

/**
 * Where a module's docs come from (plan 13a, U11). The viewer (`DocsViewer`) knows nothing but this: the pages in order, a page's Markdown, and where a picture a page shows is served. A module brings
 * its own source, so Sync's docs, Build's, Proxy's and any later product's are shown by the one viewer.
 *
 * `bundledDocs` is the source for docs that ship with the web app (the build generates a manifest from the module's docs folder and its ordering file). A source that reads a paired server's docs through
 * the console's relay is another implementation of this interface; nothing in the viewer changes for it.
 */
export interface DocsSource {
  /** The module's id, which keys its route and its cache: `sync`. */
  readonly id: string
  /** What the module is called to a reader: `DbDataSync`. */
  readonly title: string
  /** The pages, in the order the index lists them. */
  pages(): Promise<readonly DocPage[]>
  /** One page as Markdown. A page that is not there rejects with an `ApiError` naming the status. */
  page(slug: string): Promise<string>
  /** Where a picture written as `src` in this module's docs is served, or null when it is not one of the module's own pictures. */
  asset(src: string): string | null
}

/** What the build writes next to a module's pages: the index the viewer reads first. */
export interface DocsManifest {
  id: string
  title: string
  pages: readonly DocPage[]
}

/** A picture in a module's docs is written `../screenshots/<folder>/<file>`, a path that works where the docs are read as files, and the build copies each one beside the pages. Raster formats, one folder deep, no separators in the name. */
const ASSET = /^\.\.\/(screenshots\/[a-z0-9-]+\/[A-Za-z0-9._-]+\.(?:png|jpe?g|gif|webp))$/i

/** Where the web app serves a module's bundled docs, under the module's id. */
export const bundledDocsBase = (id: string) => `/docs/${id}`

export interface BundledDocsOptions {
  id: string
  title: string
  /** Where the module's generated files are served; `/docs/<id>` unless the host serves them elsewhere. */
  base?: string
  /** For a test. */
  fetcher?: typeof fetch
}

/** The docs the web app was built with: `<base>/manifest.json`, `<base>/<slug>.md`, `<base>/assets/...`, all static files, so they work offline and describe the build that is running. */
export function bundledDocs({ id, title, base = bundledDocsBase(id), fetcher }: BundledDocsOptions): DocsSource {
  const root = base.replace(/\/+$/, '')
  const get = (url: string) => (fetcher ?? fetch)(url, { credentials: 'same-origin' })
  return {
    id,
    title,
    async pages() {
      const response = await get(`${root}/manifest.json`)
      if (!response.ok) throw new ApiError(response.status, response.status === 404 ? `${title} has no docs in this build.` : response.statusText)
      const manifest = (await response.json()) as DocsManifest
      return manifest.pages
    },
    // Not `request`: the answer is a document, not JSON. A page that is not there is a 404, but the content type is checked as well, so that anything other than Markdown
    // (an HTML error page from a proxy in front of the app, or the app itself answering an address it does not have) is refused rather than shown as a document.
    async page(slug) {
      const response = await get(`${root}/${encodeURIComponent(slug)}.md`)
      if (!response.ok) throw new ApiError(response.status, response.status === 404 ? 'That page is not part of this build.' : response.statusText)
      if (!response.headers.get('content-type')?.toLowerCase().startsWith('text/markdown'))
        throw new ApiError(response.status, 'The server did not answer with a document.')
      return response.text()
    },
    asset(src) {
      const match = ASSET.exec(src)
      return match ? `${root}/assets/${match[1]}` : null
    },
  }
}
