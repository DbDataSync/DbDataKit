import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ApiError, ErrorBanner } from '@dbdatakit/shell'
import { gatewayApi } from '../api'
import { GatewayShell } from '../GatewayShell'
import type { SavedQueryRow, SavedQueryVersionRow } from '../runTypes'
import { granteeLine, granteesText, merged, openLink, ownerLabel, parseGrantees, queryText, rowKey, saveBody } from '../savedQueries'

const wrap = { whiteSpace: 'normal', overflowWrap: 'anywhere' } as const
const when = (iso: string | undefined) => (iso ? new Date(iso).toLocaleString() : '-')

export interface ManagerProps {
  row: SavedQueryRow
  text: string
  description: string
  history: SavedQueryVersionRow[]
  shares: string
  notice: string | null
  problem: string | null
  confirmDelete: boolean
  onText: (t: string) => void
  onDescription: (t: string) => void
  onSave: () => void
  onRestore: (version: number) => void
  onShares: (t: string) => void
  onShare: () => void
  onDelete: () => void
  onClose: () => void
}

/** What a person does with one saved query of their own: edit (every save is a version), restore an earlier version, share with a person, a group or everyone, delete. A shared one is read here and run from the explorer. */
export function SavedQueryManager(p: ManagerProps) {
  const parsed = parseGrantees(p.shares)
  return (
    <div className="card" data-testid="sq-manager" style={{ marginTop: 12 }}>
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h2 className="card-title"><span className="mono">{p.row.model}</span> / <span className="mono" data-testid="sq-manager-name">{p.row.name}</span> <span className="hint">version {p.row.version ?? '-'}</span></h2>
        <button type="button" className="btn btn-sm" data-testid="sq-close" onClick={p.onClose}>Close</button>
      </div>
      <textarea aria-label="Statement" data-testid="sq-text" rows={6} spellCheck={false} style={{ width: '100%', fontFamily: 'var(--mono)', fontSize: 12 }} value={p.text} readOnly={!p.row.own} onChange={(e) => p.onText(e.target.value)} />
      <input aria-label="Description" data-testid="sq-description" style={{ width: '100%', margin: '6px 0' }} value={p.description} readOnly={!p.row.own} placeholder="what it answers" onChange={(e) => p.onDescription(e.target.value)} />
      {p.problem && <div role="alert" className="hint" style={wrap} data-testid="sq-problem">{p.problem}</div>}
      {p.notice && <div role="status" className="hint" data-testid="sq-notice">{p.notice}</div>}
      {p.row.own ? (
        <>
          <div className="row" style={{ gap: 8, marginBottom: 12 }}>
            <button type="button" className="btn btn-primary btn-sm" data-testid="sq-save" onClick={p.onSave}>Save a new version</button>
            {!p.confirmDelete && <button type="button" className="btn btn-sm" data-testid="sq-delete" onClick={p.onDelete}>Delete</button>}
            {p.confirmDelete && <button type="button" className="btn btn-sm" data-testid="sq-delete-confirm" onClick={p.onDelete}>Delete it for good</button>}
          </div>
          <h3 className="card-title">History</h3>
          {p.history.length === 0 && <div className="empty">No versions yet.</div>}
          {[...p.history].sort((a, b) => b.version - a.version).map((v) => (
            <div key={v.version} className="row" style={{ gap: 10, alignItems: 'center' }} data-testid={`sq-version-${v.version}`}>
              <span>{v.version}{v.version === p.row.version ? ' (current)' : ''}</span>
              <span className="hint">{v.saved_by} · {when(v.saved_at)}{v.description ? ` · ${v.description}` : ''}</span>
              {v.version !== p.row.version && <button type="button" className="btn btn-sm" data-testid={`sq-restore-${v.version}`} onClick={() => p.onRestore(v.version)}>Restore</button>}
            </div>
          ))}
          <h3 className="card-title" style={{ marginTop: 12 }}>Share</h3>
          <div className="hint">One per line: <span className="mono">user:&lt;email or UPN&gt;</span>, <span className="mono">group:&lt;name&gt;</span> or <span className="mono">*</span>. Sharing shows the text and shares no data: whoever runs it is checked as themselves.</div>
          <textarea aria-label="Shared with" data-testid="sq-shares" rows={3} style={{ width: '100%', fontFamily: 'var(--mono)', fontSize: 12 }} value={p.shares} onChange={(e) => p.onShares(e.target.value)} />
          {parsed.problems.map((m, i) => <div key={i} role="alert" className="hint" data-testid="sq-share-problem">{m}</div>)}
          <div className="row" style={{ gap: 8, alignItems: 'center' }}>
            <button type="button" className="btn btn-sm" data-testid="sq-share" disabled={parsed.problems.length > 0} onClick={p.onShare}>Save the sharing</button>
            <span className="hint" data-testid="sq-share-summary">{parsed.grantees.length === 0 ? 'Shared with no one.' : 'Shared with ' + parsed.grantees.map(granteeLine).join(', ') + '.'}</span>
          </div>
        </>
      ) : <div className="hint" data-testid="sq-readonly">Shared with you by {ownerLabel(p.row)}: read it here and run it from the explorer; it runs as you.</div>}
    </div>
  )
}

export interface SavedQueriesViewProps {
  rows: SavedQueryRow[] | undefined
  loading: boolean
  error: unknown
  model: string
  onModel: (m: string) => void
  selected: string | null
  onManage: (row: SavedQueryRow) => void
  manager?: ManagerProps
}

/** The saved queries of the person on this Gateway: their own and those shared with them, with the owner's email (and name beside it where known). */
export function SavedQueriesView(p: SavedQueriesViewProps) {
  return (
    <div className="pane" data-testid="saved-queries">
      <div className="page-head">
        <h1 className="page-title">Saved queries</h1>
        <span className="page-note">Yours, and what others shared with you. Owners are told apart by email or UPN (the name beside it is as of their last save); a sign-in the identity home does not know is keyed by its name, which is only as exact as the name.</span>
      </div>
      <div className="row" style={{ gap: 8, alignItems: 'center', margin: '8px 0' }}>
        <label htmlFor="sq-model">Also look at the model</label>
        <input id="sq-model" data-testid="sq-model" placeholder="sales_catalog" value={p.model} onChange={(e) => p.onModel(e.target.value)} />
        <span className="hint">(what others shared is listed per model)</span>
      </div>
      <ErrorBanner error={p.error} />
      {p.loading && <div className="empty">Loading…</div>}
      {p.rows && p.rows.length === 0 && <div className="empty" data-testid="sq-none">No saved query yet. Keep one from the explorer.</div>}
      {p.rows && p.rows.length > 0 && (
        <div className="card flush">
          <div className="grid-head" style={{ gridTemplateColumns: '1.1fr 1.2fr 2fr 2fr 1.6fr', gap: 12 }}><span>Model</span><span>Name</span><span>Statement</span><span>Owner</span><span /></div>
          {p.rows.map((r) => (
            <div key={rowKey(r)} className="grid-row auto" style={{ gridTemplateColumns: '1.1fr 1.2fr 2fr 2fr 1.6fr', gap: 12 }} data-testid={`sq-row-${r.model}-${r.name}-${r.own ? 'own' : 'shared'}`}>
              <span className="mono">{r.model}</span>
              <span className="mono">{r.name}{r.description && <div className="hint">{r.description}</div>}</span>
              <span className="mono" style={wrap}>{queryText(r).replace(/\s+/g, ' ').slice(0, 80)}</span>
              <span style={wrap} data-testid={`sq-owner-${r.model}-${r.name}-${r.own ? 'own' : 'shared'}`}>{r.own ? 'you' : ownerLabel(r)}{!r.own && <div className="hint">shared</div>}</span>
              <span className="row" style={{ gap: 6 }}>
                <Link className="btn btn-sm" to={openLink(r)} data-testid={`sq-run-${r.model}-${r.name}-${r.own ? 'own' : 'shared'}`}>Open and run</Link>
                <button type="button" className={`btn btn-sm${p.selected === rowKey(r) ? ' btn-primary' : ''}`} data-testid={`sq-manage-${r.model}-${r.name}-${r.own ? 'own' : 'shared'}`} onClick={() => p.onManage(r)}>{r.own ? 'Edit' : 'View'}</button>
              </span>
            </div>
          ))}
        </div>
      )}
      {p.manager && <SavedQueryManager {...p.manager} />}
    </div>
  )
}

const say = (e: unknown) => (e instanceof ApiError || e instanceof Error ? e.message : String(e))

/** `/saved-queries`: a person's saved queries on this Gateway (plan 14a SL18): list, edit with history and restore, share, delete, run through the explorer. */
export function SavedQueriesPage() {
  const client = useQueryClient()
  const [model, setModel] = useState('')
  const list = useQuery({
    queryKey: ['gateway', 'saved-queries', model.trim()],
    queryFn: async () => {
      const own = await gatewayApi.savedQueries.list()
      const models = [...new Set([...own.map((q) => q.model), ...(model.trim() ? [model.trim()] : [])])]
      return merged([own, ...(await Promise.all(models.map((m) => gatewayApi.savedQueries.list(m).catch(() => []))))])
    },
    refetchOnMount: 'always',
  })
  const [selected, setSelected] = useState<SavedQueryRow | null>(null)
  const [text, setText] = useState('')
  const [description, setDescription] = useState('')
  const [history, setHistory] = useState<SavedQueryVersionRow[]>([])
  const [shares, setShares] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)

  const load = async (row: SavedQueryRow) => {
    setSelected(row); setText(queryText(row)); setDescription(row.description ?? ''); setNotice(null); setProblem(null); setConfirmDelete(false); setHistory([]); setShares('')
    if (!row.own) return
    try {
      const [h, g] = await Promise.all([gatewayApi.savedQueries.history(row.model, row.name), gatewayApi.savedQueries.shares(row.model, row.name)])
      setHistory(h); setShares(granteesText(g))
    } catch (e) { setProblem(say(e)) }
  }
  const refresh = async (row?: SavedQueryRow) => { await client.invalidateQueries({ queryKey: ['gateway', 'saved-queries'] }); if (row) await load(row) }

  const manager: ManagerProps | undefined = selected ? {
    row: selected, text, description, history, shares, notice, problem, confirmDelete,
    onText: setText, onDescription: setDescription, onShares: setShares, onClose: () => setSelected(null),
    onSave: async () => {
      const built = saveBody(text, description, selected.version)
      if ('problem' in built) { setProblem(built.problem); return }
      try { const saved = await gatewayApi.savedQueries.save(selected.model, selected.name, built.body); setNotice(`Saved as version ${saved.version}.`); await refresh({ ...selected, ...saved, own: true }) } catch (e) { setProblem(say(e)); setNotice(null) }
    },
    onRestore: async (version) => {
      try { const saved = await gatewayApi.savedQueries.restore(selected.model, selected.name, version); setNotice(`Version ${version} is current again, as version ${saved.version}.`); await refresh({ ...selected, ...saved, own: true }) } catch (e) { setProblem(say(e)); setNotice(null) }
    },
    onShare: async () => {
      const { grantees, problems } = parseGrantees(shares)
      if (problems.length > 0) return
      try { const now = await gatewayApi.savedQueries.setShares(selected.model, selected.name, grantees); setShares(granteesText(now.grantees)); setNotice('Sharing saved.'); setProblem(null) } catch (e) { setProblem(say(e)); setNotice(null) }
    },
    onDelete: async () => {
      if (!confirmDelete) { setConfirmDelete(true); return }
      try { await gatewayApi.savedQueries.remove(selected.model, selected.name); setSelected(null); await refresh() } catch (e) { setProblem(say(e)) }
    },
  } : undefined

  return (
    <GatewayShell crumbs={[{ label: 'Saved queries', heading: true }]}>
      <SavedQueriesView rows={list.data} loading={list.isLoading} error={list.error} model={model} onModel={setModel} selected={selected ? rowKey(selected) : null} onManage={(r) => void load(r)} manager={manager} />
    </GatewayShell>
  )
}
