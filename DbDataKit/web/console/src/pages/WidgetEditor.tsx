import { useEffect, useMemo, useState } from 'react'
import {
  WIDGET_TYPES, WellError, WidgetView, bindingIssues, catalogRoles, defaultLimit, derivedQuery, describeDerivation, explicitQuery, memberProblem, parseRequestText, wellFields, wellsOf,
  type Catalog, type CatalogMember, type ClientDiagnostic, type EditCommand, type RenderedWidget, type ViewsDoc, type WellChange, type WellSpec, type WidgetDoc,
} from '@dbdatakit/visuals'

type Apply = (command: EditCommand, group?: string) => void

const FORMATS = ['catalog', 'full', 'compact', 'percent']
const KIND_WORD = { measure: 'a measure', attribute: 'an attribute', either: 'a measure or an attribute' } as const

/** One field well: its entries (each with a flag when the catalog lacks it), a pick list of the catalog's fields the well takes, a box to type a name, and a drop target for a field dragged from the catalog list. */
function Well({ id, spec, widget, catalog, put, change }: { id: string; spec: WellSpec; widget: WidgetDoc; catalog: Catalog | null; put: (spec: WellSpec, field: string) => void; change: (spec: WellSpec, change: WellChange) => void }) {
  const [typed, setTyped] = useState('')
  const roles = useMemo(() => (catalog ? catalogRoles(catalog) : null), [catalog])
  const fields = wellFields(widget, spec)
  const offered = catalog ? [...(spec.kind === 'attribute' ? [] : catalog.measures), ...(spec.kind === 'measure' ? [] : catalog.attributes)].filter((m) => !fields.includes(m.name)) : []
  const test = `ed-well-${id}-${spec.id}`
  return (
    <div data-testid={test} onDragOver={(e) => e.preventDefault()} onDrop={(e) => { e.preventDefault(); const f = e.dataTransfer.getData('text/plain'); if (f) put(spec, f) }}
      style={{ border: '1px dashed var(--border, #bbb)', borderRadius: 6, padding: 6, minWidth: 190, flex: '1 1 190px' }}>
      <div><strong>{spec.label}</strong> <span className="hint">{KIND_WORD[spec.kind]}</span></div>
      <ul style={{ listStyle: 'none', margin: '4px 0', padding: 0 }}>
        {fields.map((f, i) => {
          const problem = roles ? memberProblem(roles, f, spec.kind) : null
          return (
            <li key={`${f}-${i}`} data-testid={`${test}-entry-${i}`} className="row" style={{ gap: 4, alignItems: 'center', flexWrap: 'wrap' }}>
              <code>{f}</code>
              {problem && <span className="hint" role="alert" data-testid={`${test}-flag-${i}`}>{problem.code}: {problem.message}</span>}
              {spec.multiple && <button type="button" className="btn btn-sm" aria-label={`Move ${f} up`} data-testid={`${test}-up-${i}`} disabled={i === 0} onClick={() => change(spec, { op: 'move', from: i, to: i - 1 })}>Up</button>}
              {spec.multiple && <button type="button" className="btn btn-sm" aria-label={`Move ${f} down`} data-testid={`${test}-down-${i}`} disabled={i === fields.length - 1} onClick={() => change(spec, { op: 'move', from: i, to: i + 1 })}>Down</button>}
              <button type="button" className="btn btn-sm" aria-label={`Remove ${f}`} data-testid={`${test}-remove-${i}`} onClick={() => change(spec, { op: 'remove', index: i })}>Remove</button>
            </li>
          )
        })}
        {fields.length === 0 && <li className="hint">Drop a field here or pick one.</li>}
      </ul>
      <div className="row" style={{ gap: 4, alignItems: 'center', flexWrap: 'wrap' }}>
        {catalog && (
          <select aria-label={`Pick a field for ${spec.label}`} data-testid={`${test}-pick`} value="" onChange={(e) => { if (e.target.value) put(spec, e.target.value) }}>
            <option value="">Pick a field…</option>
            {spec.kind !== 'attribute' && <optgroup label="Measures">{offered.filter((m) => catalog.measures.includes(m)).map((m) => <option key={m.name} value={m.name}>{m.label ? `${m.label} (${m.name})` : m.name}</option>)}</optgroup>}
            {spec.kind !== 'measure' && <optgroup label="Attributes">{offered.filter((m) => catalog.attributes.includes(m)).map((m) => <option key={m.name} value={m.name}>{m.label ? `${m.label} (${m.name})` : m.name}</option>)}</optgroup>}
          </select>
        )}
        <input aria-label={`Type a field for ${spec.label}`} data-testid={`${test}-type`} placeholder="or type a name" style={{ width: 130 }} value={typed} onChange={(e) => setTyped(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter' && typed.trim() !== '') { put(spec, typed); setTyped('') } }} />
      </div>
    </div>
  )
}

function DerivedPanel({ id, widget, doc, catalog, apply }: { id: string; widget: WidgetDoc; doc: ViewsDoc; catalog: Catalog | null; apply: Apply }) {
  const roles = useMemo(() => (catalog ? catalogRoles(catalog) : null), [catalog])
  const derivation = useMemo(() => (roles ? describeDerivation(id, widget, roles) : null), [id, widget, roles])
  const written = (widget as { query?: { semantic?: { request?: unknown } } }).query?.semantic?.request
  const writtenText = written === undefined ? '' : JSON.stringify(written, null, 2)
  const [text, setText] = useState(writtenText)
  useEffect(() => setText(writtenText), [writtenText])
  const parsed = parseRequestText(text)
  const names = (doc.parameters ?? []).map((p) => p.name)
  // the binding check on what is typed, before it is applied: every encoded field must be an output of the written request
  const asWritten = (request: Record<string, unknown>): WidgetDoc => {
    const q = (widget as { query?: { semantic?: object } }).query ?? {}
    return { ...widget, query: { ...q, semantic: { ...q.semantic, request } } } as unknown as WidgetDoc
  }
  const binding = 'value' in parsed && derivation?.state === 'explicit' ? bindingIssues(id, asWritten(parsed.value), names) : []
  if (!catalog || !derivation) return <div className="hint" data-testid={`ed-derived-${id}`} data-state="no-catalog">There is no catalog for this widget&apos;s model, so the request it asks for cannot be shown. Type field names in the wells; the server checks them on save.</div>
  const queryPath = ['widgets', id, 'query'] as const
  return (
    <div data-testid={`ed-derived-${id}`} data-state={derivation.state}>
      {derivation.state === 'none' && <div className="hint">This widget asks for nothing: it has no query.</div>}
      {derivation.state === 'refused' && (
        <ul className="hint" data-testid={`ed-derived-issues-${id}`} style={{ margin: 0 }}>
          {derivation.issues.map((i, k) => <li key={k} role="alert"><code>{i.documentPath}</code> {i.code}: {i.message}</li>)}
        </ul>
      )}
      {derivation.state === 'request' && (
        <>
          <pre data-testid={`ed-derived-request-${id}`} style={{ margin: '4px 0', maxHeight: 220, overflow: 'auto' }}>{derivation.text}</pre>
          {derivation.bindings.length > 0 && (
            <ul className="hint" data-testid={`ed-derived-bindings-${id}`} style={{ margin: '2px 0' }}>
              {derivation.bindings.map((b) => <li key={b.request}>request parameter <code>{b.request}</code> is the document&apos;s <code>{b.document}</code></li>)}
            </ul>
          )}
          <table className="table" data-testid={`ed-derived-sources-${id}`}>
            <thead><tr><th>In the request</th><th>Comes from</th></tr></thead>
            <tbody>{derivation.sources.map((s) => <tr key={s.requestPath}><td><code>{s.requestPath}</code></td><td><code>{s.documentPath}</code></td></tr>)}</tbody>
          </table>
          <button type="button" className="btn btn-sm" data-testid={`ed-derived-explicit-${id}`} onClick={() => {
            try { apply({ type: 'setPath', path: [...queryPath], value: explicitQuery(id, widget, catalogRoles(catalog)) }) } catch (e) { if (!(e instanceof WellError)) throw e }
          }}>Write the query out (explicit)</button>
        </>
      )}
      {derivation.state === 'explicit' && (
        <>
          <div className="hint">This query is written out (<code>mode: explicit</code>): the encodings choose what the widget draws, not what it asks for.</div>
          {written === undefined
            ? <div className="hint">It is not a semantic request (a saved query, a store read or a reference): edit it in the YAML view.</div>
            : (
              <>
                <textarea aria-label="The semantic request" data-testid={`ed-explicit-text-${id}`} rows={10} style={{ width: '100%', fontFamily: 'monospace' }} value={text} onChange={(e) => setText(e.target.value)} />
                {'error' in parsed && <div className="hint" role="alert" data-testid={`ed-explicit-error-${id}`}>{parsed.error}</div>}
                {binding.length > 0 && <ul className="hint" data-testid={`ed-explicit-binding-${id}`} style={{ margin: '2px 0' }}>{binding.map((b, i) => <li key={i} role="alert"><code>{b.path}</code> {b.code} {b.message}</li>)}</ul>}
                <button type="button" className="btn btn-sm" data-testid={`ed-explicit-apply-${id}`} disabled={!('value' in parsed) || text === writtenText} onClick={() => { if ('value' in parsed) apply({ type: 'setPath', path: [...queryPath, 'semantic', 'request'], value: parsed.value }) }}>Apply</button>{' '}
              </>
            )}
          <button type="button" className="btn btn-sm" data-testid={`ed-derived-back-${id}`} onClick={() => apply({ type: 'setPath', path: [...queryPath], value: derivedQuery(widget) })}>Derive it from the encodings again</button>
        </>
      )}
    </div>
  )
}

function Preview({ id, widget, doc, preview }: { id: string; widget: WidgetDoc; doc: ViewsDoc; preview: (widget: WidgetDoc) => Promise<RenderedWidget> }) {
  const [result, setResult] = useState<RenderedWidget | undefined>()
  const key = JSON.stringify(widget)
  useEffect(() => {
    let cancelled = false
    const timer = setTimeout(() => { preview(JSON.parse(key) as WidgetDoc).then((r) => { if (!cancelled) setResult(r) }, (e: unknown) => { if (!cancelled) setResult({ type: 'error', code: 'preview_failed', message: e instanceof Error ? e.message : String(e) }) }) }, 150)
    return () => { cancelled = true; clearTimeout(timer) }
  }, [key, preview])
  return (
    <div data-testid={`ed-preview-${id}`} data-result={result?.type ?? 'loading'} style={{ width: 480, height: 280, border: '1px solid var(--border, #ddd)', overflow: 'hidden' }}>
      <WidgetView id={`preview-${id}`} widget={widget} result={result} loading={result === undefined} box={{ width: 480, height: 280 }} definitions={doc.parameters ?? []} />
    </div>
  )
}

/**
 * The widget editor of the Views page's form view (plan 32 DBR25): the type chooser, the field wells over the model's catalog (a field is dragged from the list or picked; a measure goes only where a measure goes), the
 * properties each type has, the request the encodings derive (read-only, with each part's place in the document and the parameter bindings; a switch to a written query with its binding check), what the source's
 * check says of the widget (filters, order and totals are the server's), and the preview. Every change is an edit command: one undo step.
 */
export function WidgetEditor({ id, widget, doc, catalog, catalogNote, apply, diagnostics, server, preview }: {
  id: string; widget: WidgetDoc; doc: ViewsDoc; catalog: Catalog | null; catalogNote: string | null; apply: Apply
  diagnostics: ClientDiagnostic[]; server: ClientDiagnostic[]; preview: (widget: WidgetDoc) => Promise<RenderedWidget>
}) {
  const [search, setSearch] = useState('')
  const roles = useMemo(() => (catalog ? catalogRoles(catalog) : undefined), [catalog])
  const specs = wellsOf(widget.type)
  const w = widget as WidgetDoc & Record<string, any>
  const set = (path: Array<string | number>, value: unknown, group: string) => apply({ type: 'setPath', path: ['widgets', id, ...path], value }, `${id}:${group}`)
  const setOrClear = (path: Array<string | number>, value: unknown, group: string) => (value === undefined || value === '' ? apply({ type: 'removePath', path: ['widgets', id, ...path] }, `${id}:${group}`) : set(path, value, group))
  const change = (spec: WellSpec, c: WellChange) => apply({ type: 'setWell', widget: id, well: spec.id, change: c, roles })
  const put = (spec: WellSpec, field: string) => change(spec, { op: 'add', field })
  const known = new Set(diagnostics.map((d) => `${d.code}|${d.path}`))
  const mine = server.filter((d) => (d.path === `widgets.${id}` || d.path.startsWith(`widgets.${id}.`)) && !known.has(`${d.code}|${d.path}`))
  const shown = (list: CatalogMember[]) => list.filter((m) => `${m.name} ${m.label ?? ''}`.toLowerCase().includes(search.toLowerCase()))
  const columns: Array<Record<string, any>> = widget.type === 'table' ? widget.encodings.columns ?? [] : []
  const hasQuery = ['metric', 'chart', 'matrix', 'table'].includes(widget.type)
  const query = (hasQuery ? (w.query ?? {}) : {}) as Record<string, any>
  const explicit = query.mode === 'explicit'
  const totals = (widget.type === 'matrix' ? (widget.encodings.totals ?? {}) : {}) as Record<string, boolean>

  return (
    <div className="card" style={{ marginBottom: 12 }} data-testid={`ed-widget-editor-${id}`}>
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <strong>Widget <code>{id}</code></strong>
        <label className="hint">type{' '}
          <select data-testid={`ed-type-${id}`} value={widget.type} onChange={(e) => apply({ type: 'changeWidgetType', id, to: e.target.value as WidgetDoc['type'], roles })}>
            {WIDGET_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
          </select>
        </label>
        {explicit && <span className="hint" data-testid={`ed-mode-${id}`}>query written out</span>}
        {hasQuery && !explicit && <span className="hint" data-testid={`ed-mode-${id}`}>query derived from the wells</span>}
      </div>
      {catalogNote && <div className="hint" role="note" data-testid="ed-catalog-note">{catalogNote}</div>}

      {specs.length > 0 && (
        <div className="row" style={{ gap: 12, alignItems: 'flex-start', flexWrap: 'wrap', margin: '8px 0' }}>
          {catalog && (
            <div data-testid="ed-catalog" style={{ minWidth: 220, maxWidth: 260 }}>
              <strong>{catalog.model}</strong> <span className="hint">drag a field into a well</span>
              <input aria-label="Search the catalog" data-testid="ed-catalog-search" placeholder="search" style={{ width: '100%' }} value={search} onChange={(e) => setSearch(e.target.value)} />
              <div style={{ maxHeight: 220, overflow: 'auto' }}>
                {([['Measures', catalog.measures], ['Attributes', catalog.attributes]] as const).map(([label, list]) => (
                  <div key={label}>
                    <div className="hint">{label}</div>
                    <ul style={{ listStyle: 'none', margin: 0, padding: 0 }}>
                      {shown(list).map((m) => (
                        <li key={m.name} draggable data-testid={`ed-field-${m.name}`} title={m.description ?? m.name} onDragStart={(e) => { e.dataTransfer.setData('text/plain', m.name); e.dataTransfer.effectAllowed = 'copy' }} style={{ cursor: 'grab', padding: '1px 4px' }}>
                          {m.label ?? m.name} <span className="hint">{m.name}</span>
                        </li>
                      ))}
                    </ul>
                  </div>
                ))}
              </div>
            </div>
          )}
          <div className="row" style={{ gap: 8, flex: 1, flexWrap: 'wrap', alignItems: 'flex-start' }} data-testid={`ed-wells-${id}`}>
            {specs.map((spec) => <Well key={spec.id} id={id} spec={spec} widget={widget} catalog={catalog} put={put} change={change} />)}
          </div>
        </div>
      )}

      <div className="row" style={{ gap: 10, alignItems: 'center', flexWrap: 'wrap', margin: '6px 0' }} data-testid={`ed-widget-fields-${id}`}>
        {widget.type === 'metric' && (
          <label className="hint">format{' '}
            <select data-testid={`ed-format-${id}`} value={w.format ?? ''} onChange={(e) => setOrClear(['format'], e.target.value, 'format')}>
              <option value="">(default)</option>
              {FORMATS.map((f) => <option key={f} value={f}>{f}</option>)}
            </select>
          </label>
        )}
        {widget.type === 'matrix' && (
          <>
            {(['rows', 'columns'] as const).map((axis) => (
              <label key={axis} className="hint"><input type="checkbox" data-testid={`ed-totals-${axis}-${id}`} checked={totals[axis] === true}
                onChange={(e) => { const next = { ...totals, [axis]: e.target.checked }; if (!e.target.checked) delete next[axis]; if (Object.keys(next).length === 0) apply({ type: 'removePath', path: ['widgets', id, 'encodings', 'totals'] }); else set(['encodings', 'totals'], next, 'totals') }} /> total {axis === 'rows' ? 'row' : 'column'}</label>
            ))}
            <label className="hint">column limit <input type="number" min={1} style={{ width: 70 }} data-testid={`ed-column-limit-${id}`} value={widget.encodings.column_limit as number | undefined ?? ''} onChange={(e) => setOrClear(['encodings', 'column_limit'], e.target.value === '' ? undefined : Number(e.target.value), 'column_limit')} /></label>
          </>
        )}
        {hasQuery && !explicit && (
          <label className="hint">limit{' '}
            <input type="number" min={1} max={10000} style={{ width: 80 }} data-testid={`ed-limit-${id}`} placeholder={String(defaultLimit(widget.type))} value={query.limit ?? ''}
              onChange={(e) => setOrClear(['query', 'limit'], e.target.value === '' ? undefined : Number(e.target.value), 'limit')} />
          </label>
        )}
        {widget.type === 'control' && (
          <>
            <label className="hint">parameter{' '}
              <select data-testid={`ed-parameter-${id}`} value={widget.parameter} onChange={(e) => set(['parameter'], e.target.value, 'parameter')}>
                {!(doc.parameters ?? []).some((p) => p.name === widget.parameter) && <option value={widget.parameter}>{widget.parameter || '(none)'}</option>}
                {(doc.parameters ?? []).map((p) => <option key={p.name} value={p.name}>{p.name}</option>)}
              </select>
            </label>
            <label className="hint">control{' '}
              <select data-testid={`ed-control-${id}`} value={widget.control ?? ''} onChange={(e) => setOrClear(['control'], e.target.value, 'control')}>
                <option value="">(by the parameter)</option>
                {['select', 'multiselect', 'date', 'daterange', 'number', 'toggle', 'text'].map((c) => <option key={c} value={c}>{c}</option>)}
              </select>
            </label>
            <label className="hint">values limit <input type="number" min={1} style={{ width: 70 }} data-testid={`ed-values-limit-${id}`} value={widget.values?.limit ?? ''} onChange={(e) => { if (widget.values?.attribute) setOrClear(['values', 'limit'], e.target.value === '' ? undefined : Number(e.target.value), 'values_limit') }} /></label>
          </>
        )}
        {widget.type === 'image' && (
          <>
            <label className="hint">alt text <input data-testid={`ed-alt-${id}`} style={{ width: 240 }} value={widget.alt} onChange={(e) => set(['alt'], e.target.value, 'alt')} /></label>
            <label className="hint">fit{' '}
              <select data-testid={`ed-fit-${id}`} value={widget.fit ?? ''} onChange={(e) => setOrClear(['fit'], e.target.value, 'fit')}>
                <option value="">(contain)</option><option value="contain">contain</option><option value="cover">cover</option>
              </select>
            </label>
            <span className="hint">bytes: <code>{widget.hash.slice(0, 14)}…</code> (uploading an image is the publish slice&apos;s)</span>
          </>
        )}
      </div>

      {widget.type === 'table' && columns.length > 0 && (
        <table className="table" data-testid={`ed-columns-${id}`}>
          <thead><tr><th>Column</th><th>Label</th><th>Format</th><th>Align</th></tr></thead>
          <tbody>
            {columns.map((c, i) => (
              <tr key={`${c.field}-${i}`}>
                <td><code>{c.field}</code></td>
                <td><input aria-label={`Label of ${c.field}`} data-testid={`ed-column-label-${id}-${i}`} value={c.label ?? ''} onChange={(e) => setOrClear(['encodings', 'columns', i, 'label'], e.target.value, `col${i}:label`)} /></td>
                <td><select aria-label={`Format of ${c.field}`} data-testid={`ed-column-format-${id}-${i}`} value={c.format ?? ''} onChange={(e) => setOrClear(['encodings', 'columns', i, 'format'], e.target.value, `col${i}:format`)}><option value="">(default)</option>{FORMATS.map((f) => <option key={f} value={f}>{f}</option>)}</select></td>
                <td><select aria-label={`Align of ${c.field}`} data-testid={`ed-column-align-${id}-${i}`} value={c.align ?? ''} onChange={(e) => setOrClear(['encodings', 'columns', i, 'align'], e.target.value, `col${i}:align`)}><option value="">(default)</option>{['left', 'right', 'center'].map((a) => <option key={a} value={a}>{a}</option>)}</select></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {widget.type === 'table' && (
        <div className="row" style={{ gap: 6, alignItems: 'center' }}>
          <label className="hint">sort by{' '}
            <select data-testid={`ed-sort-by-${id}`} value={(widget.encodings.sort as { by?: string } | undefined)?.by ?? ''} onChange={(e) => (e.target.value === '' ? apply({ type: 'removePath', path: ['widgets', id, 'encodings', 'sort'] }) : set(['encodings', 'sort'], { by: e.target.value, ...(((widget.encodings.sort as { dir?: string } | undefined)?.dir) ? { dir: (widget.encodings.sort as { dir: string }).dir } : {}) }, 'sort'))}>
              <option value="">(the query&apos;s order)</option>
              {columns.map((c) => <option key={c.field} value={c.field}>{c.field}</option>)}
            </select>
          </label>
          <select aria-label="Sort direction" data-testid={`ed-sort-dir-${id}`} value={(widget.encodings.sort as { dir?: string } | undefined)?.dir ?? 'asc'} disabled={!(widget.encodings.sort as { by?: string } | undefined)?.by}
            onChange={(e) => set(['encodings', 'sort', 'dir'], e.target.value, 'sort')}><option value="asc">asc</option><option value="desc">desc</option></select>
        </div>
      )}

      {hasQuery && !explicit && (Array.isArray(query.filters) || Array.isArray(query.order)) && (
        <div className="hint" data-testid={`ed-query-rest-${id}`}>
          {Array.isArray(query.filters) && <>filters: <code>{JSON.stringify(query.filters)}</code> </>}
          {Array.isArray(query.order) && <>order: <code>{JSON.stringify(query.order)}</code> </>}
          (written in the YAML view; the source checks them)
        </div>
      )}

      <div style={{ margin: '8px 0' }}>
        <strong>The request this asks for</strong> <span className="hint">read-only</span>
        <DerivedPanel id={id} widget={widget} doc={doc} catalog={catalog} apply={apply} />
      </div>

      {mine.length > 0 && (
        <ul className="hint" data-testid={`ed-server-${id}`} style={{ margin: '4px 0' }}>
          {mine.map((d, i) => <li key={i} role={d.severity === 'error' ? 'alert' : undefined}>the source says <code>{d.path}</code> {d.code}: {d.message}</li>)}
        </ul>
      )}

      {hasQuery && <Preview id={id} widget={widget} doc={doc} preview={preview} />}
    </div>
  )
}
