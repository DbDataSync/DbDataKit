import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { ApiError, ErrorBanner, useModule } from '@dbdatakit/shell'
import { workspaceApi, type AdoptionRequest } from '../api'
import { adoptionRequest, catalogAdoptionSentence, refreshButton, refreshedLine, refreshLabel } from '../model'
import { shapePreview } from '../catalogPreview'
import { catalogReadApi, refreshOutcome, workspaceServerOf, type GatewayReadAnswer, type GatewayReadRequest, type RunnerChoice } from '../catalogRefresh'
import { BuildImportPanel } from './BuildImportPanel'
import { CatalogRefresh, RefreshDifference } from './CatalogRefresh'
import { CATALOG_CACHE_FILE, readCatalogCache, readLine, tableLabel, type CacheRead, type CacheTable, type CatalogCache } from '../catalogCache'
import { useDraftFile, useDraftOrNull } from '../draft'

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
        There is no catalog cache in this draft. It is one committed file, <code>{CATALOG_CACHE_FILE}</code> at the project's root, that a paired server writes: the databases, schemas, tables and columns of the project's connections and where each was read, never a row and never a secret.
        The Workspace never connects to a database itself, so mapping columns and previews come from this file.
      </div>
    )
  }
  return (
    <div data-testid="wsp-catalog">
      <div className="row" style={{ gap: 10, alignItems: 'baseline', flexWrap: 'wrap' }}>
        <button type="button" className="btn-link mono" data-testid="wsp-catalog-open" onClick={() => onOpen(CATALOG_CACHE_FILE)}>{CATALOG_CACHE_FILE}</button>
        {read?.cache && <span className="hint" data-testid="wsp-catalog-source">written {read.cache.generatedAt}</span>}
      </div>
      {read && read.findings.map((f, i) => (
        <div key={i} className="banner error" role="alert" style={{ marginTop: 6 }} data-testid="wsp-catalog-finding" data-code={f.code}><span className="mark">!</span><span><code>{f.path || 'file'}</code> {f.message}</span></div>
      ))}
      {read?.cache && Object.entries(read.cache.connections).map(([name, c]) => (
        <div key={name} data-testid="wsp-catalog-connection" data-connection={name}>
          <div className="hint mono" style={{ margin: '10px 0 2px' }}>{name} ({c.engine}), {c.tables.length} {c.tables.length === 1 ? 'table' : 'tables'}</div>
          {c.read && <div className="hint" data-testid="wsp-catalog-read">{readLine(c.read)}</div>}
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
 * **Refresh from a Gateway**: a Gateway the console has, which holds the data source's login, answers a read-only question (the tables and columns of a data source: names, types, keys, never a row), asked by the console as the
 * signed-in person. A data source made by hand is asked for only after the confirmation `adoption` shows (the Gateway records who and when). It sends nothing itself: `onRefresh` gets the request, so the screen and its tests say the same.
 */
export function RefreshForm({ canWrite, gateways, connections, busy, onRefresh, adoption = null, onCancelAdoption, initialGateway = '', initialConnection = '', initialDataSource = '' }: {
  canWrite: boolean; gateways: readonly RunnerChoice[]; connections: readonly string[]; busy: boolean; onRefresh: (request: GatewayReadRequest) => void
  adoption?: AdoptionRequest | null; onCancelAdoption?: () => void; initialGateway?: string; initialConnection?: string; initialDataSource?: string
}) {
  const [picked, setGateway] = useState(initialGateway)
  const [named, setConnection] = useState(initialConnection)
  const [dataSource, setDataSource] = useState(initialDataSource)
  const gateway = picked || gateways[0]?.id || ''
  const connection = named || connections[0] || ''
  const state = refreshButton({ canWrite, gateway, connection, busy, gateways })
  const request = (adopt: boolean): GatewayReadRequest => ({ gateway, connection: connection.trim(), ...(dataSource.trim() !== '' ? { data_source: dataSource.trim() } : {}), ...(adopt ? { adopt: true } : {}) })
  return (
    <form className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', margin: '6px 0 10px' }} data-testid="wsp-catalog-refresh" onSubmit={(e) => { e.preventDefault(); if (state.enabled) onRefresh(request(false)) }}>
      <select className="input" aria-label="Gateway to read the catalog from" value={gateway} onChange={(e) => setGateway(e.target.value)} data-testid="wsp-catalog-refresh-gateway">
        {gateways.length === 0 && <option value="">no Gateway is paired</option>}
        {gateways.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
      </select>
      <select className="input mono" aria-label="Connection of the project" value={connection} onChange={(e) => setConnection(e.target.value)} data-testid="wsp-catalog-refresh-connection">
        {connections.length === 0 && <option value="">the draft has no connection</option>}
        {connections.map((c) => <option key={c} value={c}>{c}</option>)}
      </select>
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

/** The names of a draft's connections, from its files: `config/connections/<name>.yaml` (Sync) or `connections/<name>.yaml` (Build). */
export const draftConnections = (paths: readonly string[]): string[] =>
  [...new Set(paths.map((p) => /^(?:config\/)?connections\/([^/]+)\.ya?ml$/i.exec(p)?.[1]).filter((n): n is string => !!n))].sort()

/** What to read again: one connection whole, or one of its cached tables (`schema.name`). */
export function RefreshTarget({ connections, cache, connection, onConnection, table, onTable }: {
  connections: readonly string[]; cache: CatalogCache | null; connection: string; onConnection: (name: string) => void; table: string; onTable: (table: string) => void
}) {
  const tables = cache?.connections[connection]?.tables ?? []
  return (
    <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginBottom: 6 }}>
      <label className="hint" htmlFor="wsp-runner-refresh-connection">Connection</label>
      <select id="wsp-runner-refresh-connection" className="input mono" value={connection} onChange={(e) => onConnection(e.target.value)} data-testid="wsp-runner-refresh-connection">
        {connections.length === 0 && <option value="">the draft has no connection</option>}
        {connections.map((c) => <option key={c} value={c}>{c}</option>)}
      </select>
      <label className="hint" htmlFor="wsp-runner-refresh-table">Read</label>
      <select id="wsp-runner-refresh-table" className="input mono" value={table} onChange={(e) => onTable(e.target.value)} data-testid="wsp-runner-refresh-table">
        <option value="">the whole connection</option>
        {tables.filter((t) => t.schema !== '').map((t) => <option key={tableLabel(t)} value={`${t.schema}.${t.name}`}>{t.schema}.{t.name}</option>)}
      </select>
    </div>
  )
}

/**
 * **Refresh from a Runner** on the Catalog tab: the shape of a connection, or of one of its tables, read in a channel of a Runner through the console
 * (`CatalogRefresh`), against the cache the draft shows with its unsaved edit; accepted, the new cache is an unsaved edit of the draft, as any typed change is.
 */
function RunnerRefresh({ cache, canWrite, project }: { cache: CatalogCache | null; canWrite: boolean; project: string }) {
  const draft = useDraftOrNull()!
  const file = useDraftFile(CATALOG_CACHE_FILE)
  const connections = draftConnections(draft.paths)
  const [connection, setConnection] = useState('')
  const [table, setTable] = useState('')
  const chosen = connection || connections[0] || ''
  const dot = table.indexOf('.')
  const target = table !== '' && dot > 0 ? { schema: table.slice(0, dot), name: table.slice(dot + 1) } : undefined
  return (
    <div className="card" style={{ margin: '6px 0 10px' }} data-testid="wsp-runner-refresh">
      <div className="card-body">
        <div className="hint" style={{ marginBottom: 6 }}>Read the shape again from a Runner channel that binds the connection, with the login bound there, or test that login. Only names and types are read, never a row.</div>
        <RefreshTarget connections={connections} cache={cache} connection={chosen} onConnection={(c) => { setConnection(c); setTable('') }} table={table} onTable={setTable} />
        {chosen !== '' && <CatalogRefresh key={`${chosen}/${table}`} project={project} connection={chosen} table={target} current={file.text} canEdit={canWrite && draft.canEdit} onAccept={(text) => draft.edit(CATALOG_CACHE_FILE, text)} offerTest />}
      </div>
    </div>
  )
}

/**
 * **Refresh from a Gateway** on the Catalog tab: the console asks a Gateway it has for the shape of one of its data sources, as the signed-in person; the
 * Workspace server is not asked. The difference with the cache the draft shows (its unsaved edit included) is shown first; accepted, the new cache is an
 * unsaved edit of the draft, as with a Runner's read.
 */
function GatewayRefresh({ canWrite, project }: { canWrite: boolean; project: string }) {
  const draft = useDraftOrNull()!
  const file = useDraftFile(CATALOG_CACHE_FILE)
  const workspace = workspaceServerOf(useModule().apiBase)
  const gateways = useQuery({ queryKey: ['console', 'gateways'], queryFn: catalogReadApi.gateways, enabled: workspace !== null, retry: false })
  const [settled, setSettled] = useState(false)
  const read = useMutation<GatewayReadAnswer, Error, GatewayReadRequest>({ mutationFn: (body) => catalogReadApi.gatewayRead(workspace!, project, body), onMutate: () => setSettled(false) })
  const answer = read.data
  const outcome = answer && !settled ? refreshOutcome(file.text, answer.cache, answer.connection) : undefined
  if (workspace === null) return <div className="hint" data-testid="wsp-gateway-refresh-unavailable">A read from a Gateway is asked through the console: open this draft from the console's Workspace screens.</div>
  return (
    <div className="card" style={{ margin: '6px 0 10px' }} data-testid="wsp-gateway-refresh">
      <div className="card-body">
        <div className="hint" style={{ marginBottom: 6 }}>Read the shape of a Gateway's data source, with the login the Gateway holds. Only names and types are read, never a row.</div>
        <RefreshForm canWrite={canWrite && draft.canEdit} gateways={gateways.data ?? []} connections={draftConnections(draft.paths)} busy={read.isPending} onRefresh={(request) => read.mutate(request)}
          adoption={read.isError && read.error instanceof ApiError ? adoptionRequest(read.error.body) : null} onCancelAdoption={read.reset} />
        <ErrorBanner error={gateways.error} />
        {read.isPending && <div className="hint" data-testid="wsp-catalog-refresh-progress">Asking the Gateway for the tables and columns of the data source...</div>}
        {read.isError && <div data-testid="wsp-catalog-refresh-refused"><ErrorBanner error={read.error} /></div>}
        {outcome && !outcome.ok && <div className="banner error" role="alert" data-testid="wsp-refresh-refused"><span className="mark">!</span><span>{outcome.problem}</span></div>}
        {outcome?.ok && answer && (
          <>
            <div className="hint" data-testid="wsp-catalog-refreshed">{refreshedLine(answer)}</div>
            <RefreshDifference difference={outcome.difference} canEdit={canWrite && draft.canEdit}
              onAccept={() => { draft.edit(CATALOG_CACHE_FILE, outcome.text); setSettled(true) }} onDismiss={() => setSettled(true)} />
          </>
        )}
      </div>
    </div>
  )
}

/**
 * The cache of a draft, read through the file route and checked by the twin reader, or the unsaved edit of it when there is one; with the refresh from
 * a Runner and the refresh from a Gateway above it.
 */
export function CatalogPanel({ project, id, onOpen, canWrite = true, kind }: { project: string; id: string; onOpen: (path: string) => void; canWrite?: boolean; kind?: string }) {
  const saved = useCatalogRead(project, id)
  const draft = useDraftOrNull()
  const unsaved = draft?.dirtyPaths.includes(CATALOG_CACHE_FILE) ? draft.textOf(CATALOG_CACHE_FILE) : undefined
  const read = unsaved !== undefined ? readCatalogCache(unsaved) : saved.read
  const present = saved.present || unsaved !== undefined
  const error = saved.error
  const [preview, setPreview] = useState<string | null>(null)
  return (
    <>
      {draft && <RunnerRefresh cache={read?.cache ?? null} canWrite={canWrite} project={project} />}
      {draft && <GatewayRefresh canWrite={canWrite} project={project} />}
      {unsaved !== undefined && <div className="hint" data-testid="wsp-catalog-unsaved">The cache below is an unsaved edit of the draft: save it to keep it.</div>}
      <ErrorBanner error={error} />
      <CatalogView read={read} present={present} onOpen={onOpen} preview={preview} onPreview={setPreview} />
      {kind === 'build' && read?.cache && Object.keys(read.cache.connections).length > 0 && <BuildImportPanel project={project} id={id} connections={read.cache.connections} canWrite={canWrite} />}
    </>
  )
}
