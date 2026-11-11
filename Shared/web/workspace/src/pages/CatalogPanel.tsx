import { useQuery } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { workspaceApi } from '../api'
import { CATALOG_CACHE_FILE, readCatalogCache, tableLabel, type CacheRead } from '../catalogCache'

/** The catalog cache without its query, so a test can draw it: what is cached per connection, or what is wrong with the file (the strict reader's words, with the key path). */
export function CatalogView({ read, present, onOpen }: { read: CacheRead | null; present: boolean; onOpen: (path: string) => void }) {
  if (!present) {
    return (
      <div className="empty" data-testid="wsp-catalog-none">
        There is no catalog cache in this draft. It is one committed file, <code>{CATALOG_CACHE_FILE}</code> at the project's root, that a paired server writes: the tables and columns of the project's connections, never a row and never a secret.
        The Workspace never connects to a database itself, so mapping columns and previews come from this file.
      </div>
    )
  }
  return (
    <div data-testid="wsp-catalog">
      <div className="row" style={{ gap: 10, alignItems: 'baseline', flexWrap: 'wrap' }}>
        <button type="button" className="btn-link mono" data-testid="wsp-catalog-open" onClick={() => onOpen(CATALOG_CACHE_FILE)}>{CATALOG_CACHE_FILE}</button>
        {read?.cache && <span className="hint" data-testid="wsp-catalog-source">from {read.cache.server} ({read.cache.product}), {read.cache.generatedAt}</span>}
      </div>
      {read && read.findings.map((f, i) => (
        <div key={i} className="banner error" role="alert" style={{ marginTop: 6 }} data-testid="wsp-catalog-finding" data-code={f.code}><span className="mark">!</span><span><code>{f.path || 'file'}</code> {f.message}</span></div>
      ))}
      {read?.cache && Object.entries(read.cache.connections).map(([name, c]) => (
        <div key={name} data-testid="wsp-catalog-connection" data-connection={name}>
          <div className="hint mono" style={{ margin: '10px 0 2px' }}>{name} ({c.engine}), {c.tables.length} {c.tables.length === 1 ? 'table' : 'tables'}</div>
          {c.tables.map((t) => (
            <div key={tableLabel(t)} className="grid-row" style={{ gap: 10, flexWrap: 'wrap' }} data-testid="wsp-catalog-table" data-table={tableLabel(t)}>
              <span className="mono">{tableLabel(t)}</span>
              <span className="hint">{t.kind}, {t.columns.length} {t.columns.length === 1 ? 'column' : 'columns'}</span>
            </div>
          ))}
        </div>
      ))}
    </div>
  )
}

/** The cache of a draft, read through the file route and checked by the twin reader. */
export function CatalogPanel({ project, id, paths, onOpen }: { project: string; id: string; paths: readonly string[]; onOpen: (path: string) => void }) {
  const present = paths.includes(CATALOG_CACHE_FILE)
  const text = useQuery({ queryKey: ['workspace', 'catalog', project, id, present], queryFn: () => workspaceApi.readFile(project, id, CATALOG_CACHE_FILE), enabled: present, staleTime: 0 })
  return (
    <>
      <ErrorBanner error={text.error} />
      <CatalogView read={text.data === undefined ? null : readCatalogCache(text.data)} present={present} onOpen={onOpen} />
    </>
  )
}
