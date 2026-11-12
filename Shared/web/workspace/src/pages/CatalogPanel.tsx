import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, ErrorBanner } from '@dbdatakit/shell'
import { workspaceApi, type AdoptionRequest, type CatalogRefreshAnswer, type CatalogRefreshRequest, type PublishTarget } from '../api'
import { adoptionRequest, catalogAdoptionSentence, refreshButton, refreshedLine, refreshLabel } from '../model'
import { shapePreview } from '../catalogPreview'
import { CATALOG_CACHE_FILE, readCatalogCache, tableLabel, type CacheRead, type CacheTable } from '../catalogCache'

/** The catalog cache without its query, so a test can draw it: what is cached per connection, or what is wrong with the file (the strict reader's words, with the key path). */
/** A table's shape: its columns with their types and a few rows made from the types. Labelled as generated, because the cache holds no row (D20). */
export function ShapePreview({ table }: { table: CacheTable }) {
  const { columns, rows } = shapePreview(table)
  return (
    <div className="card" style={{ margin: '6px 0', overflow: 'auto' }} data-testid="wsp-shape" data-table={tableLabel(table)}>
      <div className="card-body">
        <div className="hint" data-testid="wsp-shape-note">Generated from the column types, not rows of the database: the cache holds the shape of a table and never its rows.</div>
        <table className="table" data-testid="wsp-shape-table">
          <thead><tr>{columns.map((c) => <th key={c.name} title={`${c.type}${c.nullable ? '' : ', not null'}${c.key ? ', key' : ''}`}>{c.key ? `${c.name} (key)` : c.name}<div className="hint">{c.type}</div></th>)}</tr></thead>
          <tbody>{rows.map((r, i) => <tr key={i}>{r.map((v, j) => <td key={j} className={v === null ? 'hint' : 'mono'}>{v ?? 'null'}</td>)}</tr>)}</tbody>
        </table>
      </div>
    </div>
  )
}

export function CatalogView({ read, present, onOpen, preview = null, onPreview }: { read: CacheRead | null; present: boolean; onOpen: (path: string) => void; preview?: string | null; onPreview?: (key: string | null) => void }) {
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
              {onPreview && <button type="button" className="btn-link" data-testid="wsp-catalog-preview" onClick={() => onPreview(preview === `${name}/${tableLabel(t)}` ? null : `${name}/${tableLabel(t)}`)}>{preview === `${name}/${tableLabel(t)}` ? 'Hide the shape' : 'Preview the shape'}</button>}
              {preview === `${name}/${tableLabel(t)}` && <div style={{ flexBasis: '100%' }}><ShapePreview table={t} /></div>}
            </div>
          ))}
        </div>
      ))}
    </div>
  )
}

/** The cache of a draft as the screens read it: null when the draft has none (the files list says so), the strict reader's answer otherwise. Shared by the Catalog tab and the editors that pick from it (one query, so a save shows everywhere). */
export function useCatalogRead(project: string, id: string): { read: CacheRead | null; present: boolean; error: unknown } {
  const files = useQuery({ queryKey: ['workspace', 'files', project, id], queryFn: () => workspaceApi.files(project, id) })
  const present = files.data?.files.some((f) => f.path === CATALOG_CACHE_FILE) ?? false
  const text = useQuery({ queryKey: ['workspace', 'catalog', project, id], queryFn: () => workspaceApi.readFile(project, id, CATALOG_CACHE_FILE), enabled: present, staleTime: 0 })
  return { read: present && text.data !== undefined ? readCatalogCache(text.data) : null, present, error: text.error }
}

/**
 * **Refresh from a data source** (plan 33 SC20j, D20): the Workspace holds no login, so a paired Gateway, which does, answers a read-only question (the tables and columns of a data source: names, types, keys, never a row) and the answer is written into the
 * draft as `catalog-cache.yaml`. A data source made by hand is asked for only after the confirmation `adoption` shows (the Gateway records who and when). It sends nothing itself: `onRefresh` gets the request, so the screen and its tests say the same.
 */
export function RefreshForm({ canWrite, targets, busy, onRefresh, adoption = null, onCancelAdoption, initialTarget = '', initialConnection = '', initialDataSource = '' }: {
  canWrite: boolean; targets: readonly PublishTarget[]; busy: boolean; onRefresh: (request: CatalogRefreshRequest) => void
  adoption?: AdoptionRequest | null; onCancelAdoption?: () => void; initialTarget?: string; initialConnection?: string; initialDataSource?: string
}) {
  const [target, setTarget] = useState(initialTarget)
  const [connection, setConnection] = useState(initialConnection)
  const [dataSource, setDataSource] = useState(initialDataSource)
  const gateways = targets.filter((t) => t.product === 'gateway' && t.can_publish)
  const state = refreshButton({ canWrite, target, connection, busy, targets })
  const request = (adopt: boolean): CatalogRefreshRequest => ({ target: target.trim(), connection: connection.trim(), ...(dataSource.trim() !== '' ? { data_source: dataSource.trim() } : {}), ...(adopt ? { adopt: true } : {}) })
  return (
    <form className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', margin: '6px 0 10px' }} data-testid="wsp-catalog-refresh" onSubmit={(e) => { e.preventDefault(); if (state.enabled) onRefresh(request(false)) }}>
      <input className="input mono" list="wsp-catalog-targets" aria-label="Gateway to read the catalog from" placeholder="publisher key of a Gateway" value={target} onChange={(e) => setTarget(e.target.value)} data-testid="wsp-catalog-refresh-target" />
      <datalist id="wsp-catalog-targets">{gateways.map((t) => <option key={t.name} value={t.name}>{t.gateway ?? 'not paired'}</option>)}</datalist>
      <input className="input mono" aria-label="Connection of the project" placeholder="connection of the project" value={connection} onChange={(e) => setConnection(e.target.value)} data-testid="wsp-catalog-refresh-connection" />
      <input className="input mono" aria-label="Data source on the Gateway" placeholder="data source (blank: the connection's name)" value={dataSource} onChange={(e) => setDataSource(e.target.value)} data-testid="wsp-catalog-refresh-datasource" />
      <button type="submit" className="btn" disabled={!state.enabled} title={state.why ?? undefined} data-testid="wsp-catalog-refresh-go">{refreshLabel(connection, dataSource)}</button>
      {state.why && <span className="hint" data-testid="wsp-catalog-refresh-why">{state.why}</span>}
      {adoption && !busy && (
        <div className="callout" style={{ flexBasis: '100%' }} role="alertdialog" aria-label={`Adopt the data source ${adoption.data_source}`} data-testid="wsp-catalog-adopt" data-data-source={adoption.data_source} data-made-by={adoption.made_by}>
          <div data-testid="wsp-catalog-adopt-text">{catalogAdoptionSentence(adoption)}</div>
          <div className="row" style={{ gap: 8, marginTop: 8 }}>
            <button type="button" className="btn btn-primary" onClick={() => onRefresh(request(true))} data-testid="wsp-catalog-adopt-confirm">Confirm and read {adoption.data_source}</button>
            <button type="button" className="btn" onClick={() => onCancelAdoption?.()} data-testid="wsp-catalog-adopt-cancel">Do not read it</button>
          </div>
        </div>
      )}
    </form>
  )
}

/** The cache of a draft, read through the file route and checked by the twin reader; with the refresh from a Gateway above it. */
export function CatalogPanel({ project, id, onOpen, canWrite = true }: { project: string; id: string; onOpen: (path: string) => void; canWrite?: boolean }) {
  const { read, present, error } = useCatalogRead(project, id)
  const [preview, setPreview] = useState<string | null>(null)
  const client = useQueryClient()
  const targets = useQuery({ queryKey: ['workspace', 'targets', 'all'], queryFn: () => workspaceApi.publishTargets(), retry: false })
  const refresh = useMutation<CatalogRefreshAnswer, Error, CatalogRefreshRequest>({
    mutationFn: (request) => workspaceApi.refreshCatalog(project, id, request),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['workspace', 'files', project, id] })
      void client.invalidateQueries({ queryKey: ['workspace', 'catalog', project, id] })
    },
  })
  return (
    <>
      <RefreshForm canWrite={canWrite} targets={targets.data ?? []} busy={refresh.isPending} onRefresh={(request) => refresh.mutate(request)}
        adoption={refresh.isError && refresh.error instanceof ApiError ? adoptionRequest(refresh.error.body) : null} onCancelAdoption={refresh.reset} />
      {refresh.isPending && <div className="hint" data-testid="wsp-catalog-refresh-progress">Asking the Gateway for the tables and columns of the data source...</div>}
      {refresh.isError && <div data-testid="wsp-catalog-refresh-refused"><ErrorBanner error={refresh.error} /></div>}
      {refresh.isSuccess && <div className="empty" data-testid="wsp-catalog-refreshed">{refreshedLine(refresh.data)}</div>}
      <ErrorBanner error={error} />
      <CatalogView read={read} present={present} onOpen={onOpen} preview={preview} onPreview={setPreview} />
    </>
  )
}
