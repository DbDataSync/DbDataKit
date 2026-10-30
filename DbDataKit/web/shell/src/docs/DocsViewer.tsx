import { useEffect } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useLocation } from 'react-router-dom'
import { Link, NavLink } from '../module'
import { ErrorBanner } from '../components/ErrorBanner'
import { RichMarkdown } from '../components/RichMarkdown'
import type { DocPage, DocsSource } from './DocsSource'

/** What the viewer shows for one module's docs, with nothing fetched: the pure part, so it can be shown for any module (and tested for two). */
export interface DocsViewerViewProps {
  /** The module's name to a reader: `DbDataSync`. */
  title: string
  /** The route the docs are shown at: `/docs`, or `/docs/<module>` where several modules' docs share the console. A page is `<base>/<slug>`. */
  base: string
  pages: readonly DocPage[]
  /** The slug in the address, if any. */
  page: string | undefined
  /** The page's Markdown, once it is here. */
  text: string | undefined
  loading: boolean
  error: unknown
  /** The note above the docs saying which version they describe. */
  versionNote: string
  /** Where a picture written in the docs is served. */
  asset: (src: string) => string | null
}

/**
 * The docs viewer (plan 13a, U11): `/docs` is the index, `/docs/:page` one page of it. The pages are the files of the module's docs as they were when this build was made, so what is on screen is what is running, not whatever
 * GitHub's `main` says today. Readable by every role: there is nothing here to withhold.
 */
export function DocsViewerView({ title, base, pages, page, text, loading, error, versionNote, asset }: DocsViewerViewProps) {
  const known = pages.find((p) => p.slug === page)
  return (
    <div className="pane">
      <div className="page-head">
        {/* The document brings its own title (its first heading), so the header stays the section's — two of the same is noise. */}
        <h1 className="page-title">Documentation</h1>
        <span className="page-note" data-testid="docs-version">{versionNote}</span>
      </div>

      <div className="docs-layout">
        <nav className="docs-nav" aria-label="Documentation pages" data-testid="docs-nav">
          {pages.map((p) => (
            <NavLink key={p.slug} to={`${base}/${p.slug}`} className={({ isActive }) => `docs-nav-item ${isActive ? 'active' : ''}`}>
              {p.title}
            </NavLink>
          ))}
        </nav>

        <div className="docs-body">
          {!page && <DocsIndex title={title} base={base} pages={pages} />}
          {page && !known && !loading && <NoSuchPage slug={page} base={base} />}
          {known && <DocBody slugs={pages.map((p) => p.slug)} base={base} text={text} loading={loading} error={error} asset={asset} />}
        </div>
      </div>
    </div>
  )
}

function DocsIndex({ title, base, pages }: { title: string; base: string; pages: readonly DocPage[] }) {
  return (
    <div data-testid="docs-index">
      <p className="hint" style={{ marginTop: 0 }}>
        These pages describe the version of {title} you are running. They are part of the build, so they work with no
        network and cannot describe a different release.
      </p>
      <ul className="docs-index-list">
        {pages.map((p) => (
          <li key={p.slug}><Link to={`${base}/${p.slug}`}>{p.title}</Link></li>
        ))}
      </ul>
    </div>
  )
}

function NoSuchPage({ slug, base }: { slug: string; base: string }) {
  return (
    <div className="banner" data-testid="docs-not-found">
      <span className="mark">!</span>
      <span>
        There is no page called <span className="mono">{slug}</span> in the docs that shipped with this version.{' '}
        <Link to={base}>See the list of pages.</Link>
      </span>
    </div>
  )
}

function DocBody({ slugs, base, text, loading, error, asset }: { slugs: string[]; base: string; text: string | undefined; loading: boolean; error: unknown; asset: (src: string) => string | null }) {
  const { hash } = useLocation()

  // A link to `<base>/x#section` arrives before the page's text does, and the router does not scroll to a fragment on
  // its own — so once the text is there, go to the heading it named.
  useEffect(() => {
    if (!text || !hash) return
    document.getElementById(decodeURIComponent(hash.slice(1)))?.scrollIntoView()
  }, [text, hash])

  if (loading) return <span className="hint">Loading…</span>
  if (error) return <ErrorBanner error={error} />
  return <RichMarkdown text={text ?? ''} docSlugs={slugs} docLinkBase={base} resolveImage={asset} testId="docs-content" />
}

/**
 * One module's docs from its `DocsSource`. The frame around it (the module's rail, or the console's) is the host's; this is the content.
 * @param page the page in the address, if any
 * @param base the route the docs are shown at, which a link between pages is made under
 * @param versionNote which version the docs describe; the host knows (the server's, through the relay, or the console's own)
 */
export function DocsViewer({ source, page, base, versionNote }: { source: DocsSource; page: string | undefined; base: string; versionNote: string }) {
  // Baked into the build, so neither answer goes stale within a session; a failure is not retried (the page is there or it is not).
  const pages = useQuery({ queryKey: ['docs', source.id, 'pages'], queryFn: () => source.pages(), staleTime: Infinity, retry: false })
  const known = pages.data?.some((p) => p.slug === page) ?? false
  const text = useQuery({ queryKey: ['docs', source.id, 'page', page], queryFn: () => source.page(page!), enabled: known, staleTime: Infinity, retry: false })
  if (pages.error) return <div className="pane"><ErrorBanner error={pages.error} /></div>
  return (
    <DocsViewerView
      title={source.title}
      base={base}
      pages={pages.data ?? []}
      page={page}
      text={text.data}
      loading={pages.isLoading || (known && text.isLoading)}
      error={text.error}
      versionNote={versionNote}
      asset={(src) => source.asset(src)}
    />
  )
}
