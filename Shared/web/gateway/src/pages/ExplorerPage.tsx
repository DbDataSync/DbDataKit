import { useEffect, useMemo, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link as ConsoleLink, useParams, useSearchParams } from 'react-router-dom'
import { ApiError, ErrorBanner, Link } from '@dbdatakit/shell'
import { RenderedViews, type RenderedViewDocument } from '@dbdatakit/visuals'
import { gatewayApi } from '../api'
import { GatewayShell } from '../GatewayShell'
import {
  DEFAULT_LIMIT, bodyFor, cellText, clampLimit, members, missingSlots, pinLink, pinRequest, picksOf, refusal, requestText, roles, scansOf, sqlFromPicks, suggest, toggle, viewSpecFor,
  type Encoding, type Mode, type Picks, type ViewShape,
} from '../explorer'
import type { RunResult } from '../runTypes'
import type { SemanticCatalogView } from '../types'
import { RunsPanel } from './RunsPanel'

const wrap = { whiteSpace: 'normal', overflowWrap: 'anywhere' } as const

export interface SaveForm { name: string; description: string; saved: string | null; error: string | null; busy: boolean }

export interface ExplorerViewProps {
  model: string
  catalog?: SemanticCatalogView
  catalogError?: unknown
  mode: Mode
  text: string
  picks: Picks
  running: boolean
  /** The engine's or the Gateway's refusal, with its code in front, or a problem found before anything was sent. */
  error: string | null
  result?: RunResult
  shape: ViewShape
  /** What the Gateway's renderer made of the matrix or chart chosen (its own errors when it refuses the encoding), and whether that is being asked. */
  rendered?: RenderedViewDocument
  rendering?: boolean
  /** A problem reaching the renderer (not the renderer's refusal, which is in `rendered`). */
  renderError?: string | null
  encoding: Encoding
  save: SaveForm
  onMode: (mode: Mode) => void
  onText: (text: string) => void
  onToggle: (kind: 'attributes' | 'measures', name: string) => void
  onLimit: (n: number) => void
  onRun: () => void
  onExplain: () => void
  onShape: (shape: ViewShape) => void
  /** Ask the Gateway to draw the shape and encodings chosen (again): the button is off while a required slot is empty. */
  onDraw: () => void
  onEncoding: (e: Encoding) => void
  onSaveName: (name: string) => void
  onSaveDescription: (text: string) => void
  onSave: () => void
}

const SHAPES: Array<[ViewShape, string]> = [['table', 'Table'], ['matrix', 'Matrix'], ['bar', 'Bar chart'], ['line', 'Line chart']]

/** A result the Gateway could draw: one that came with no `view_unavailable` notice (a column of a type the renderer cannot read, D38) shows as the table only. */
const drawable = (result: RunResult) => !result.notices?.some((n) => n.code === 'view_unavailable')

function Result(p: ExplorerViewProps) {
  const result = p.result!
  if (result.status === 'explained') {
    return (
      <div data-testid="explain-result">
        <h2 className="card-title">Plan</h2>
        <div className="hint" data-testid="explain-note">Nothing was sent to a source: this is what would be.</div>
        {scansOf(result).map((s, i) => (
          <div key={i} className="card" style={{ marginBottom: 8 }} data-testid={`scan-${i}`}>
            <div className="hint"><span className="mono" data-testid={`scan-source-${i}`}>{s.source}</span> · <span data-testid={`scan-engine-${i}`}>{s.engine}</span></div>
            <pre className="mono" style={wrap} data-testid={`scan-text-${i}`}>{s.text}</pre>
          </div>
        ))}
        {result.rows.length === 0 && <div className="empty" data-testid="explain-none">The engine would send no scan.</div>}
      </div>
    )
  }
  const r = roles(result)
  const missing = missingSlots(p.shape, p.encoding)
  const pinned = pinRequest(p.text, p.picks)
  const option = (names: string[]) => names.map((n) => <option key={n} value={n}>{n}</option>)
  return (
    <div data-testid="run-result">
      <div className="hint" data-testid="run-status">
        <span data-testid="qis-status">{result.status === 'ok' ? 'complete' : result.status}</span> · {result.rows.length} row{result.rows.length === 1 ? '' : 's'}
        {result.request_hash && <> · request <span className="mono" data-testid="request-hash">{result.request_hash}</span></>}
        {result.model_version != null && <> · model version {result.model_version}</>}
        {result.replay_of != null && <> · <span data-testid="replay-note">replay of run {result.replay_of}, {result.same_hash ? 'the same request hash' : 'a different request hash'}</span></>}
      </div>
      {result.warnings.length > 0 && <ul data-testid="run-warnings">{result.warnings.map((w, i) => <li key={i} className="hint">{w}</li>)}</ul>}
      {result.notices && result.notices.length > 0 && <ul data-testid="run-notices">{result.notices.map((n, i) => <li key={i} className="hint" style={wrap} data-testid={`run-notice-${n.code}`}>{n.message}</li>)}</ul>}
      {result.status === 'unsupported' && (
        <div role="alert" data-testid="run-unsupported">{result.reason}{result.remaining != null && <pre className="mono" style={wrap} data-testid="run-remaining">{JSON.stringify(result.remaining, null, 2)}</pre>}</div>
      )}
      {result.status === 'ok' && (
        <>
          {drawable(result) && <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', margin: '8px 0' }} data-testid="view-choice">
            {SHAPES.map(([id, label]) => (
              <button key={id} type="button" className={`btn btn-sm${p.shape === id ? ' btn-primary' : ''}`} aria-pressed={p.shape === id} data-testid={`shape-${id}`} onClick={() => p.onShape(id)}>{label}</button>
            ))}
            {p.shape !== 'table' && (
              <>
                <label htmlFor="enc-rows">Along</label>
                <select id="enc-rows" data-testid="enc-rows" value={p.encoding.rows[0] ?? ''} onChange={(e) => p.onEncoding({ ...p.encoding, rows: e.target.value ? [e.target.value] : [] })}><option value="">(none)</option>{option(result.columns.map((c) => c.name))}</select>
                <label htmlFor="enc-columns">Across</label>
                <select id="enc-columns" data-testid="enc-columns" value={p.encoding.columns[0] ?? ''} onChange={(e) => p.onEncoding({ ...p.encoding, columns: e.target.value ? [e.target.value] : [] })}><option value="">(none)</option>{option(result.columns.map((c) => c.name))}</select>
                <label htmlFor="enc-value">Value</label>
                <select id="enc-value" data-testid="enc-value" value={p.encoding.value} onChange={(e) => p.onEncoding({ ...p.encoding, value: e.target.value })}><option value="">(none)</option>{option(result.columns.map((c) => c.name))}</select>
              </>
            )}
            {p.shape !== 'table' && <button type="button" className="btn btn-sm btn-primary" data-testid="view-draw" disabled={missing.length > 0 || p.rendering} onClick={p.onDraw}>Draw</button>}
            {p.shape !== 'table' && missing.length > 0 && <span className="hint" role="status" data-testid="view-missing">required slot is empty: {missing.join(', ')}</span>}
            {r.hasTotals && <span className="hint" data-testid="has-totals">The result carries totals.</span>}
          </div>}
          {p.renderError && <div role="alert" className="hint" style={wrap} data-testid="view-error">{p.renderError}</div>}
          {p.shape !== 'table' && p.rendering && !p.rendered && <div className="hint" data-testid="view-rendering">Drawing…</div>}
          {p.shape !== 'table' && missing.length === 0 && p.rendered && (
            <div data-testid={p.rendered.status === 'failed' ? 'view-refused' : 'view-drawn'}>
              <RenderedViews document={p.rendered} />
            </div>
          )}
          {(p.shape === 'table' || !drawable(result)) && (
            <div className="card flush" style={{ overflowX: 'auto' }}>
              <table className="mono" data-testid="result-table" style={{ width: '100%' }}>
                <thead><tr>{result.columns.map((c) => <th key={c.name} style={{ textAlign: 'left' }}>{c.name}</th>)}</tr></thead>
                <tbody>{result.rows.map((row, i) => <tr key={i} data-testid="result-row">{row.map((v, j) => <td key={j}>{cellText(v)}</td>)}</tr>)}</tbody>
              </table>
            </div>
          )}
          {pinned && (
            <div style={{ marginTop: 8 }}>
              <ConsoleLink className="btn btn-sm" to={pinLink(p.model, pinned, p.shape === 'table' || p.shape === 'matrix' ? 'table' : 'chart')} data-testid="pin-result">Pin to my dashboard</ConsoleLink>
            </div>
          )}
        </>
      )}
    </div>
  )
}

/** The explorer: choose members from the model's palette (or write semantic SQL or a JSON request), run it as yourself, read the plan, draw the result and keep or pin it. */
export function ExplorerView(p: ExplorerViewProps) {
  const lists = p.catalog ? members(p.catalog) : { attributes: [], measures: [] }
  return (
    <div className="pane" data-testid="explorer">
      <ErrorBanner error={p.catalogError} />
      <div className="page-head">
        <h1 className="page-title mono" data-testid="explorer-model">{p.model}</h1>
        <span className="page-note">Runs as you: the Gateway's access rules and limits apply, and every run is recorded. <Link to={`/models/${encodeURIComponent(p.model)}`} data-testid="explorer-browse">Browse the model</Link></span>
      </div>

      <div className="card" data-testid="palette">
        <div className="row" style={{ gap: 24, flexWrap: 'wrap' }}>
          <div>
            <div className="card-title">Attributes</div>
            {lists.attributes.map((n) => (
              <label key={n} style={{ display: 'block' }}><input type="checkbox" data-testid={`palette-attribute-${n}`} checked={p.picks.attributes.includes(n)} onChange={() => p.onToggle('attributes', n)} /> <span className="mono">{n}</span></label>
            ))}
          </div>
          <div>
            <div className="card-title">Measures</div>
            {lists.measures.map((n) => (
              <label key={n} style={{ display: 'block' }}><input type="checkbox" data-testid={`palette-measure-${n}`} checked={p.picks.measures.includes(n)} onChange={() => p.onToggle('measures', n)} /> <span className="mono">{n}</span></label>
            ))}
          </div>
          <div>
            <label htmlFor="palette-limit">Rows (up to 10,000)</label>{' '}
            <input id="palette-limit" type="number" min={1} max={10000} style={{ width: 90 }} data-testid="palette-limit" value={p.picks.limit} onChange={(e) => p.onLimit(Number(e.target.value))} />
          </div>
        </div>
      </div>

      <div className="row" style={{ gap: 8, margin: '10px 0 4px' }}>
        <button type="button" className={`btn btn-sm${p.mode === 'sql' ? ' btn-primary' : ''}`} aria-pressed={p.mode === 'sql'} data-testid="mode-sql" onClick={() => p.onMode('sql')}>Semantic SQL</button>
        <button type="button" className={`btn btn-sm${p.mode === 'json' ? ' btn-primary' : ''}`} aria-pressed={p.mode === 'json'} data-testid="mode-json" onClick={() => p.onMode('json')}>JSON request</button>
      </div>
      <textarea aria-label="Statement" data-testid="statement" rows={7} spellCheck={false} style={{ width: '100%', fontFamily: 'var(--mono)', fontSize: 12 }} value={p.text} onChange={(e) => p.onText(e.target.value)} />
      <div className="row" style={{ gap: 8, margin: '8px 0', flexWrap: 'wrap' }}>
        <button type="button" className="btn btn-primary btn-sm" data-testid="run" disabled={p.running} onClick={p.onRun}>Run</button>
        <button type="button" className="btn btn-sm" data-testid="explain" disabled={p.running} onClick={p.onExplain} title="Shows the plan, one row per scan, and sends nothing to a source">Explain</button>
        {p.running && <span className="hint" data-testid="running">Running…</span>}
      </div>
      {p.error && <div role="alert" className="hint" style={wrap} data-testid="run-error">{p.error}</div>}
      {p.result && <Result {...p} />}

      <div className="card" style={{ marginTop: 16 }} data-testid="save-query">
        <div className="card-title">Keep this statement</div>
        <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
          <input aria-label="Name" data-testid="save-name" placeholder="revenue_by_region" value={p.save.name} onChange={(e) => p.onSaveName(e.target.value)} />
          <input aria-label="Description" data-testid="save-description" placeholder="what it answers" value={p.save.description} onChange={(e) => p.onSaveDescription(e.target.value)} style={{ minWidth: 260 }} />
          <button type="button" className="btn btn-sm" data-testid="save-go" disabled={p.save.busy || p.save.name.trim() === ''} onClick={p.onSave}>Save</button>
        </div>
        {p.save.error && <div role="alert" className="hint" data-testid="save-error">{p.save.error}</div>}
        {p.save.saved && <div className="hint" role="status" data-testid="save-done">Saved as <span className="mono">{p.save.saved}</span>. <Link to="/saved-queries" data-testid="save-open-list">Saved queries</Link></div>}
      </div>
    </div>
  )
}

const say = (e: unknown): string => {
  if (e instanceof ApiError) { const body = e.body as { code?: unknown; path?: unknown } | null; return refusal(e.message, body?.code, body?.path) }
  return e instanceof Error ? e.message : String(e)
}

/** `/models/<name>/explore`: the query explorer of this Gateway (plan 14a SL14 to SL16). `?run=<id>` opens a record's statement, `?saved=<name>&owner=<email>` a saved query, `?text=` a statement. */
export function ExplorerPage() {
  const { name = '' } = useParams()
  const [params] = useSearchParams()
  const catalog = useQuery({ queryKey: ['gateway', 'models', 'catalog', name], queryFn: () => gatewayApi.models.semantic.catalog(name), refetchOnMount: 'always' })
  const [mode, setMode] = useState<Mode>('sql')
  const [picks, setPicks] = useState<Picks>({ attributes: [], measures: [], limit: DEFAULT_LIMIT })
  const [text, setText] = useState('')
  const [running, setRunning] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<RunResult | undefined>()
  const [shape, setShape] = useState<ViewShape>('table')
  const [encoding, setEncoding] = useState<Encoding>({ rows: [], columns: [], value: '' })
  const [rendered, setRendered] = useState<RenderedViewDocument | undefined>()
  const [rendering, setRendering] = useState(false)
  const [renderError, setRenderError] = useState<string | null>(null)
  // the body of the last run and the id of the result the Gateway kept for it: another view of it is a render, not a run
  const last = useRef<{ body: Record<string, unknown>; id?: string }>({ body: {} })
  const drawn = useRef(0)
  const [drawTick, setDrawTick] = useState(0)
  const [save, setSave] = useState<SaveForm>({ name: '', description: '', saved: null, error: null, busy: false })

  // a result on show: the body that made it, and the id of the result the Gateway kept for it
  const show = (answer: RunResult, body: Record<string, unknown>) => {
    last.current = { body, id: answer.result_id }
    setRendered(undefined)
    setResult(answer)
    if (answer.status === 'ok') setEncoding(suggest(answer))
  }

  // a statement handed over by the address: a run record, a saved query, or plain text
  const runId = params.get('run')
  const savedName = params.get('saved')
  const savedOwner = params.get('owner')
  const given = params.get('text')
  useEffect(() => {
    let live = true
    const open = (statement: string) => { if (live) { setText(statement); setMode(statement.trim().startsWith('{') ? 'json' : 'sql'); const p = picksOf(statement); if (p) setPicks(p) } }
    // a record opens by its replay (D38 (3)): one run, the rows and the kept result come back with the text, and a record that did not complete (a refusal) opens as text only
    if (runId) {
      gatewayApi.runs.read(Number(runId)).then(async (r) => {
        open(r.text)
        if (!live || r.request == null || r.status !== 'complete' || (r.kind !== 'query' && r.kind !== 'explain')) return
        setError(null)
        setRunning(true)
        try {
          const answer = await gatewayApi.runs.replay(r.id, r.kind === 'explain' ? {} : { keep: true })
          const built = bodyFor(name, r.text, { explain: r.kind === 'explain', limit: picksOf(r.text)?.limit ?? DEFAULT_LIMIT })
          if (live) show(answer, 'body' in built ? built.body : {})
        } catch (e) { if (live) setError(say(e)) } finally { if (live) setRunning(false) }
      }).catch((e) => live && setError(say(e)))
    }
    else if (savedName) gatewayApi.savedQueries.read(name, savedName, savedOwner ?? undefined).then((q) => { open(q.sql ?? JSON.stringify(q.request, null, 2)); if (live && q.own) setSave((s) => ({ ...s, name: q.name, description: q.description ?? '' })) }).catch((e) => live && setError(say(e)))
    else if (given) open(given)
    return () => { live = false }
  }, [runId, savedName, savedOwner, given, name])

  const apply = (next: Picks, nextMode: Mode) => setText(nextMode === 'json' ? requestText(next) : sqlFromPicks(name, next))
  const onToggle = (kind: 'attributes' | 'measures', member: string) => { const next = toggle(picks, kind, member); setPicks(next); apply(next, mode) }
  const onLimit = (n: number) => { const next = { ...picks, limit: clampLimit(n) }; setPicks(next); apply(next, mode) }
  const onMode = (m: Mode) => { setMode(m); if (picks.attributes.length + picks.measures.length > 0) apply(picks, m) }

  const send = async (explain: boolean) => {
    setError(null)
    const built = bodyFor(name, text, { explain, limit: picks.limit })
    if ('problem' in built) { setError(built.problem); return }
    setRunning(true)
    try {
      // a result that may be drawn is kept by the Gateway, so a matrix or a chart of it later runs nothing
      const body = explain ? built.body : { ...built.body, keep: true }
      const answer = await gatewayApi.runs.run(body)
      show(answer, built.body)
    } catch (e) {
      setResult(undefined)
      setError(say(e))
    } finally { setRunning(false) }
  }

  // the matrix or chart chosen is the Gateway's renderer's: ask it for the result kept, or, when that is gone, run the statement again with the view
  useEffect(() => {
    const ticket = ++drawn.current
    const spec = result?.status === 'ok' && drawable(result) && missingSlots(shape, encoding).length === 0 ? viewSpecFor(result, shape, encoding) : null
    setRenderError(null)
    if (!spec) { setRendered(undefined); setRendering(false); return }
    setRendering(true)
    const draw = async () => {
      if (last.current.id) {
        try { return (await gatewayApi.runs.render(last.current.id, spec)).rendered_view } catch (e) { if (!(e instanceof ApiError && e.status === 410)) throw e }
      }
      const again = await gatewayApi.runs.run({ ...last.current.body, keep: true, view: spec })
      last.current = { ...last.current, id: again.result_id }
      return again.rendered_view
    }
    draw().then((doc) => { if (ticket === drawn.current) setRendered(doc) })
      .catch((e) => { if (ticket === drawn.current) { setRendered(undefined); setRenderError(say(e)) } })
      .finally(() => { if (ticket === drawn.current) setRendering(false) })
  }, [result, shape, encoding, drawTick])

  const keep = async () => {
    const built = bodyFor(name, text, {})
    if ('problem' in built) { setSave((s) => ({ ...s, error: built.problem, saved: null })); return }
    setSave((s) => ({ ...s, busy: true, error: null, saved: null }))
    try {
      const existing = await gatewayApi.savedQueries.read(name, save.name.trim()).catch(() => null)
      const body = built.body as { sql?: string; request?: Record<string, unknown> }
      const saved = await gatewayApi.savedQueries.save(name, save.name.trim(), { ...(body.sql !== undefined ? { sql: body.sql } : { request: body.request }), description: save.description || null, expected_version: existing?.version })
      setSave((s) => ({ ...s, busy: false, saved: `${saved.name} (version ${saved.version})` }))
    } catch (e) { setSave((s) => ({ ...s, busy: false, error: say(e) })) }
  }

  const props = useMemo<ExplorerViewProps>(() => ({
    model: name, catalog: catalog.data, catalogError: catalog.error, mode, text, picks, running, error, result, shape, encoding, rendered, rendering, renderError, save,
    onMode, onText: setText, onToggle, onLimit, onRun: () => void send(false), onExplain: () => void send(true), onShape: setShape, onDraw: () => setDrawTick((t) => t + 1), onEncoding: setEncoding,
    onSaveName: (n) => setSave((s) => ({ ...s, name: n })), onSaveDescription: (d) => setSave((s) => ({ ...s, description: d })), onSave: () => void keep(),
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }), [name, catalog.data, catalog.error, mode, text, picks, running, error, result, shape, encoding, rendered, rendering, renderError, save])

  return (
    <GatewayShell crumbs={[{ label: 'Models', to: '/models' }, { label: name, mono: true, to: `/models/${encodeURIComponent(name)}` }, { label: 'Explorer', heading: true }]}>
      <ExplorerView {...props} />
      <RunsPanel model={name} />
    </GatewayShell>
  )
}
