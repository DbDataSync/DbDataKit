import { useEffect, useMemo, useState } from 'react'
import {
  WIDGET_TYPES, WellError, WidgetView, bindingIssues, catalogRoles, defaultLimit, derivedQuery, describeDerivation, explicitQuery, filterEntry, filterProblems, memberProblem, operatorsFor, orderEntry, orderProblems,
  parameterValue, parseRequestText, parseValues, sortAt, wellFields, wellsOf,
  DATE_PRESETS, NUMBER_STYLES, draftOf, draftProblem, formatOf, formatSample, levelCommand, levelsOf, sortValue,
  type AxisChannel, type FormatDraft, type Catalog, type CatalogMember, type ClientDiagnostic, type EditCommand, type RenderedWidget, type ViewsDoc, type WellChange, type WellSpec, type WidgetDoc, type WidgetOptionsDoc,
} from '@dbdatakit/visuals'
import { CUSTOM_FORMAT, FORMAT_PRESETS, formatChoice } from './editing'

type Apply = (command: EditCommand, group?: string) => void

const FORMATS = FORMAT_PRESETS

/** A format select: the presets, and for a format written as a mapping one read-only choice that keeps it (choosing it changes nothing). */
function FormatSelect({ format, onPick, testid, label }: { format: unknown; onPick: (value: string) => void; testid: string; label?: string }) {
  const value = formatChoice(format)
  return (
    <select aria-label={label} data-testid={testid} value={value} onChange={(e) => { if (e.target.value !== CUSTOM_FORMAT) onPick(e.target.value) }}>
      <option value="">(default)</option>
      {value === CUSTOM_FORMAT && <option value={CUSTOM_FORMAT}>{CUSTOM_FORMAT}</option>}
      {FORMATS.map((f) => <option key={f} value={f}>{f}</option>)}
    </select>
  )
}
/**
 * A format with its structured form and a live sample (plan 32 DBR44): the preset select stays as it was, and under it the controls of a number mapping (style, decimals, currency, grouping) or a date mapping (a preset
 * or a pattern), a sample written by the formatter the page and the PDF use, and the loaders' words when what is typed is refused. What cannot be stored is not applied: the controls keep what was typed until it can.
 */
function FormatEditor({ format, onChange, testid, label }: { format: unknown; onChange: (value: unknown) => void; testid: string; label?: string }) {
  const stored = JSON.stringify(format ?? null)
  const [draft, setDraft] = useState<FormatDraft>(() => draftOf(format))
  useEffect(() => setDraft(draftOf(JSON.parse(stored) as unknown)), [stored])
  const problem = draftProblem(draft)
  const update = (patch: Partial<FormatDraft>) => {
    const next = { ...draft, ...patch }
    setDraft(next)
    if (draftProblem(next) === null) onChange(formatOf(next))
  }
  const sample = problem === null ? formatSample(formatOf(draft)) : ''
  return (
    <span data-testid={`${testid}-editor`}>
      <FormatSelect label={label} testid={testid} format={format} onPick={(v) => onChange(v === '' ? undefined : v)} />
      <details style={{ display: 'inline-block', marginLeft: 6 }}>
        <summary className="hint">structured</summary>
        <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
          <select aria-label="Kind of format" data-testid={`${testid}-kind`} value={draft.kind} onChange={(e) => update({ kind: e.target.value as FormatDraft['kind'] })}>
            <option value="default">(default)</option><option value="preset">a preset</option><option value="number">number mapping</option><option value="date">date mapping</option>
          </select>
          {draft.kind === 'number' && (
            <>
              <select aria-label="Number style" data-testid={`${testid}-style`} value={draft.style} onChange={(e) => update({ style: e.target.value })}>{NUMBER_STYLES.map((s) => <option key={s} value={s}>{s}</option>)}</select>
              <input aria-label="Decimals" data-testid={`${testid}-decimals`} type="text" inputMode="numeric" placeholder="decimals" style={{ width: 70 }} value={draft.decimals} onChange={(e) => update({ decimals: e.target.value })} />
              <input aria-label="Currency code" data-testid={`${testid}-currency`} placeholder="USD" style={{ width: 56 }} value={draft.currency} onChange={(e) => update({ currency: e.target.value })} />
              <label className="hint"><input type="checkbox" data-testid={`${testid}-grouping`} checked={draft.grouping} onChange={(e) => update({ grouping: e.target.checked })} /> thousands separator</label>
            </>
          )}
          {draft.kind === 'date' && (
            <>
              <input aria-label="Date preset or pattern" data-testid={`${testid}-date`} list={`${testid}-date-presets`} style={{ width: 150 }} value={draft.date} onChange={(e) => update({ date: e.target.value })} />
              <datalist id={`${testid}-date-presets`}>{DATE_PRESETS.map((p) => <option key={p} value={p} />)}</datalist>
            </>
          )}
          <span className="hint" data-testid={`${testid}-sample`}>{sample === '' ? '' : `reads ${sample}`}</span>
          {problem !== null && <span className="hint" role="alert" data-testid={`${testid}-problem`}>{problem}</span>}
        </div>
      </details>
    </span>
  )
}

/** The levels of an axis well (a chart's `x`, a matrix's rows and columns): each level's format and, for a chart's nested `x`, its sort and title. A change is the edit command `setPath` of that level's entry. */
function AxisLevels({ id, widget, channel, catalog, apply }: { id: string; widget: WidgetDoc; channel: AxisChannel; catalog: Catalog | null; apply: Apply }) {
  const levels = levelsOf(widget, channel)
  if (levels.length === 0) return null
  const nested = channel === 'x' && levels.length > 1
  const lev = (index: number, key: 'format' | 'sort' | 'title', value: unknown) => {
    const c = levelCommand(id, widget, channel, index, key, value)
    apply({ type: 'setPath', path: c.path, value: c.value }, `${id}:${channel}${index}:${key}`)
  }
  return (
    <div data-testid={`ed-levels-${id}-${channel}`} style={{ margin: '4px 0' }}>
      <span className="hint">{channel === 'x' ? 'x levels' : `${channel} levels`}, outermost first</span>
      {levels.map((level, i) => {
        const innermost = i === levels.length - 1
        const base = `ed-level-${id}-${channel}-${i}`
        return (
          <div key={`${level.field}-${i}`} className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap', padding: '2px 0' }} data-testid={base}>
            <code>{level.field}</code>
            <FormatEditor label={`Format of ${level.field}`} testid={`${base}-format`} format={level.format} onChange={(v) => lev(i, 'format', v)} />
            {nested && (
              <>
                <label className="hint">sort{' '}
                  <select aria-label={`Sort of ${level.field}`} data-testid={`${base}-sort-by`} value={level.sort?.by ?? ''} onChange={(e) => lev(i, 'sort', sortValue(e.target.value, level.sort?.dir))}>
                    <option value="">(ascending by value)</option><option value="sort_key">sort_key (the rows&apos; order)</option><option value="label">label</option>
                    {innermost && (catalog?.measures ?? []).map((m) => <option key={m.name} value={m.name}>{m.name}</option>)}
                  </select>
                </label>
                <select aria-label={`Direction of ${level.field}`} data-testid={`${base}-sort-dir`} disabled={!level.sort} value={level.sort?.dir ?? 'asc'} onChange={(e) => level.sort && lev(i, 'sort', sortValue(level.sort.by, e.target.value))}>
                  <option value="asc">asc</option><option value="desc">desc</option>
                </select>
              </>
            )}
            {channel === 'x' && <input aria-label={`Title of ${level.field}`} data-testid={`${base}-title`} placeholder="title" style={{ width: 110 }} value={level.title ?? ''} onChange={(e) => lev(i, 'title', e.target.value === '' ? undefined : e.target.value)} />}
          </div>
        )
      })}
    </div>
  )
}

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

const RELATIVE = ['last_complete', 'current', 'period_to_date']
const NO_VALUE = ['set', 'not_set', ...RELATIVE]

/** The Filter and Sort sections (plan 32 DBR43): the filters and the order of the derived query as forms, with the catalog's operators for each field's type and the engine's words beside an entry that would be refused. */
function QuerySections({ id, widget, doc, catalog, apply }: { id: string; widget: WidgetDoc; doc: ViewsDoc; catalog: Catalog | null; apply: Apply }) {
  const query = ((widget as { query?: unknown }).query ?? {}) as Record<string, any>
  const filters: any[] = Array.isArray(query.filters) ? query.filters : []
  const order: unknown[] = Array.isArray(query.order) ? query.order : []
  const parameters = doc.parameters ?? []
  const attributes = catalog?.attributes ?? []
  const [attribute, setAttribute] = useState('')
  const [operator, setOperator] = useState('equals')
  const [valueText, setValueText] = useState('')
  const [parameter, setParameter] = useState('')
  const [count, setCount] = useState('1')
  const [error, setError] = useState<string | null>(null)
  const [sortField, setSortField] = useState('')
  const member = attributes.find((m) => m.name === attribute)
  const operators = operatorsFor(member)
  const run = (command: EditCommand) => { try { apply(command); setError(null) } catch (e) { setError(e instanceof Error ? e.message : String(e)) } }
  const add = () => {
    const values = NO_VALUE.includes(operator) ? undefined : parameter !== '' ? parameterValue(parameter, parameters) : parseValues(valueText, member)
    run({ type: 'setFilter', widget: id, change: { op: 'add', filter: filterEntry(attribute, operator, values, operator === 'last_complete' ? Number(count) : undefined) }, catalog })
    setValueText('')
  }
  const sorted = order.map(orderEntry)
  const orderIssues = orderProblems(order, catalog)
  const allFields = catalog ? [...catalog.measures, ...catalog.attributes] : []
  const axisSort = (axis: 'rows' | 'columns') => sortAt(widget, { kind: 'axis', axis })
  return (
    <div data-testid={`ed-query-parts-${id}`} style={{ margin: '8px 0' }}>
      <div data-testid={`ed-filters-${id}`}>
        <strong>Filter</strong> <span className="hint">the filters of the query</span>
        <ul style={{ listStyle: 'none', margin: '4px 0', padding: 0 }}>
          {filters.map((f, i) => {
            const problems = filterProblems({ attribute: String(f.attribute ?? ''), operator: String(f.operator ?? ''), values: f.values, count: f.count }, catalog)
            const shown = f.values === undefined ? '' : JSON.stringify(f.values)
            return (
              <li key={i} data-testid={`ed-filter-${id}-${i}`} className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
                <code>{String(f.attribute)}</code> <code>{String(f.operator)}</code> {shown !== '' && <code>{shown}</code>}
                {f.count !== undefined && <code>count {String(f.count)}</code>}
                {problems.map((p, k) => <span key={k} className="hint" role="alert" data-testid={`ed-filter-problem-${id}-${i}`}>{p.code}: {p.message}</span>)}
                <button type="button" className="btn btn-sm" data-testid={`ed-filter-up-${id}-${i}`} disabled={i === 0} onClick={() => run({ type: 'setFilter', widget: id, change: { op: 'move', from: i, to: i - 1 } })}>Up</button>
                <button type="button" className="btn btn-sm" data-testid={`ed-filter-remove-${id}-${i}`} onClick={() => run({ type: 'setFilter', widget: id, change: { op: 'remove', index: i } })}>Remove</button>
              </li>
            )
          })}
          {filters.length === 0 && <li className="hint">No filter.</li>}
        </ul>
        <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
          <select aria-label="Attribute to filter on" data-testid={`ed-filter-attribute-${id}`} value={attribute} onChange={(e) => { setAttribute(e.target.value); setOperator('equals') }}>
            <option value="">Attribute…</option>
            {attributes.map((m) => <option key={m.name} value={m.name}>{m.label ? `${m.label} (${m.name})` : m.name}</option>)}
          </select>
          <select aria-label="Operator" data-testid={`ed-filter-operator-${id}`} value={operator} onChange={(e) => setOperator(e.target.value)}>
            {(operators.includes(operator) ? operators : [operator, ...operators]).map((o) => <option key={o} value={o}>{o}</option>)}
          </select>
          {operator === 'last_complete' && <input type="number" min={1} aria-label="Count" data-testid={`ed-filter-count-${id}`} style={{ width: 60 }} value={count} onChange={(e) => setCount(e.target.value)} />}
          {!NO_VALUE.includes(operator) && (
            <>
              <input aria-label="Value" data-testid={`ed-filter-value-${id}`} placeholder="value, or a, b" disabled={parameter !== ''} value={valueText} onChange={(e) => setValueText(e.target.value)} />
              <select aria-label="Or a parameter" data-testid={`ed-filter-parameter-${id}`} value={parameter} onChange={(e) => setParameter(e.target.value)}>
                <option value="">(a value)</option>
                {parameters.map((p) => <option key={p.name} value={p.name}>{`parameter ${p.name}`}</option>)}
              </select>
            </>
          )}
          <button type="button" className="btn btn-sm" data-testid={`ed-filter-add-${id}`} disabled={attribute === ''} onClick={add}>Add filter</button>
        </div>
        {error && <div className="hint" role="alert" data-testid={`ed-filter-error-${id}`}>{error}</div>}
      </div>

      <div data-testid={`ed-sort-${id}`} style={{ marginTop: 8 }}>
        <strong>Sort</strong> <span className="hint">the order of the query</span>
        <ul style={{ listStyle: 'none', margin: '4px 0', padding: 0 }}>
          {sorted.map((entry, i) => (
            <li key={i} data-testid={`ed-order-${id}-${i}`} className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
              {entry ? <code>{entry.field}</code> : <code>{JSON.stringify(order[i])}</code>}
              {entry && (
                <select aria-label={`Direction of ${entry.field}`} data-testid={`ed-order-dir-${id}-${i}`} value={entry.dir} onChange={(e) => run({ type: 'setOrder', widget: id, change: { op: 'direction', index: i, dir: e.target.value as 'asc' | 'desc' } })}>
                  <option value="asc">asc</option><option value="desc">desc</option>
                </select>
              )}
              {orderIssues.filter((p) => p.index === i).map((p, k) => <span key={k} className="hint" role="alert" data-testid={`ed-order-problem-${id}-${i}`}>{p.code}: {p.message}</span>)}
              <button type="button" className="btn btn-sm" data-testid={`ed-order-up-${id}-${i}`} disabled={i === 0} onClick={() => run({ type: 'setOrder', widget: id, change: { op: 'move', from: i, to: i - 1 } })}>Up</button>
              <button type="button" className="btn btn-sm" data-testid={`ed-order-remove-${id}-${i}`} onClick={() => run({ type: 'setOrder', widget: id, change: { op: 'remove', index: i } })}>Remove</button>
            </li>
          ))}
          {order.length === 0 && <li className="hint">No order of its own.</li>}
        </ul>
        <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
          <select aria-label="Field to sort by" data-testid={`ed-order-field-${id}`} value={sortField} onChange={(e) => setSortField(e.target.value)}>
            <option value="">Field…</option>
            {allFields.map((m) => <option key={m.name} value={m.name}>{m.label ? `${m.label} (${m.name})` : m.name}</option>)}
          </select>
          <button type="button" className="btn btn-sm" data-testid={`ed-order-add-${id}`} disabled={sortField === ''} onClick={() => { run({ type: 'setOrder', widget: id, change: { op: 'add', field: sortField, dir: 'asc' } }); setSortField('') }}>Add to the order</button>
        </div>
        {widget.type === 'matrix' && (['rows', 'columns'] as const).map((axis) => {
          const current = axisSort(axis)
          const keywords = ['sort_key', 'label']
          const measures = catalog?.measures ?? []
          return (
            <div key={axis} className="row" style={{ gap: 6, alignItems: 'center' }} data-testid={`ed-axis-sort-${id}-${axis}`}>
              <label className="hint">{axis} sorted by{' '}
                <select data-testid={`ed-axis-sort-by-${id}-${axis}`} value={current?.by ?? ''} onChange={(e) => run({ type: 'setSort', widget: id, target: { kind: 'axis', axis }, sort: e.target.value === '' ? undefined : { by: e.target.value, ...(current?.dir ? { dir: current.dir } : {}) }, catalog })}>
                  <option value="">(the result&apos;s order)</option>
                  {keywords.map((k) => <option key={k} value={k}>{k}</option>)}
                  {measures.map((m) => <option key={m.name} value={m.name}>{m.name}</option>)}
                </select>
              </label>
              <select aria-label={`Direction of the ${axis}`} data-testid={`ed-axis-sort-dir-${id}-${axis}`} disabled={!current} value={current?.dir ?? 'asc'} onChange={(e) => current && run({ type: 'setSort', widget: id, target: { kind: 'axis', axis }, sort: { by: current.by, dir: e.target.value as 'asc' | 'desc' }, catalog })}>
                <option value="asc">asc</option><option value="desc">desc</option>
              </select>
            </div>
          )
        })}
        {widget.type === 'chart' && (['x', 'y', 'color'] as const).filter((c) => (widget.encodings as Record<string, unknown>)[c] !== undefined && c !== 'y' && !Array.isArray((widget.encodings as Record<string, unknown>)[c])).map((channel) => {
          const current = sortAt(widget, { kind: 'channel', channel })
          const value = current === undefined ? '' : current.by === 'sort_key' ? 'sort_key' : current.dir === 'desc' ? 'descending' : 'ascending'
          return (
            <label key={channel} className="hint" style={{ display: 'block' }}>{channel} members{' '}
              <select data-testid={`ed-channel-sort-${id}-${channel}`} value={value} onChange={(e) => run({ type: 'setSort', widget: id, target: { kind: 'channel', channel }, sort: e.target.value === '' ? undefined : e.target.value === 'sort_key' ? { by: 'sort_key' } : { by: 'field', dir: e.target.value === 'descending' ? 'desc' : 'asc' } })}>
                <option value="">(default)</option><option value="ascending">ascending</option><option value="descending">descending</option><option value="sort_key">in the result&apos;s order</option>
              </select>
            </label>
          )
        })}
      </div>
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
 * check says of the widget (filters, order and totals are the server's), the preview, and how each layout shows the widget (`widget_options`). Every change is an edit command: one undo step.
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
  const setOption = (layout: string, key: keyof WidgetOptionsDoc, value: string) => apply({ type: 'setWidgetOption', layout, widget: id, key, value: value === '' ? undefined : key === 'show_title' ? value === 'true' : (value as never) })

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
            <FormatEditor testid={`ed-format-${id}`} format={w.format} onChange={(v) => setOrClear(['format'], v, 'format')} />
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
        {widget.type === 'embed' && (
          <>
            <label className="hint">address <input data-testid={`ed-embed-url-${id}`} style={{ width: 360 }} value={widget.url} onChange={(e) => set(['url'], e.target.value, 'url')} /></label>
            <label className="hint">link text <input data-testid={`ed-embed-link-${id}`} style={{ width: 200 }} value={widget.link_text ?? ''} onChange={(e) => setOrClear(['link_text'], e.target.value, 'link_text')} /></label>
            <label className="hint">query pairs <input data-testid={`ed-embed-with-${id}`} style={{ width: 280 }} placeholder="Name=$params.region, Other=literal" value={Object.entries(widget.with ?? {}).map(([k, v]) => `${k}=${v}`).join(', ')}
              onChange={(e) => { const pairs = e.target.value.split(',').map((p) => p.trim()).filter((p) => p.includes('=')).map((p) => [p.slice(0, p.indexOf('=')).trim(), p.slice(p.indexOf('=') + 1).trim()] as const); setOrClear(['with'], pairs.length === 0 ? undefined : Object.fromEntries(pairs), 'with') }} /></label>
            <span className="hint" data-testid={`ed-embed-note-${id}`}>framed only when the host approves the address&apos;s origin; otherwise a link</span>
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

      {widget.type === 'chart' && <AxisLevels id={id} widget={widget} channel="x" catalog={catalog} apply={apply} />}
      {widget.type === 'matrix' && (['rows', 'columns'] as const).map((axis) => <AxisLevels key={axis} id={id} widget={widget} channel={axis} catalog={catalog} apply={apply} />)}
      {widget.type === 'table' && columns.length > 0 && (
        <table className="table" data-testid={`ed-columns-${id}`}>
          <thead><tr><th>Column</th><th>Label</th><th>Format</th><th>Align</th><th>Width</th></tr></thead>
          <tbody>
            {columns.map((c, i) => (
              <tr key={`${c.field}-${i}`}>
                <td><code>{c.field}</code></td>
                <td><input aria-label={`Label of ${c.field}`} data-testid={`ed-column-label-${id}-${i}`} value={c.label ?? ''} onChange={(e) => setOrClear(['encodings', 'columns', i, 'label'], e.target.value, `col${i}:label`)} /></td>
                <td><FormatEditor label={`Format of ${c.field}`} testid={`ed-column-format-${id}-${i}`} format={c.format} onChange={(v) => setOrClear(['encodings', 'columns', i, 'format'], v, `col${i}:format`)} /></td>
                <td><select aria-label={`Align of ${c.field}`} data-testid={`ed-column-align-${id}-${i}`} value={c.align ?? ''} onChange={(e) => setOrClear(['encodings', 'columns', i, 'align'], e.target.value, `col${i}:align`)}><option value="">(default)</option>{['left', 'right', 'center'].map((a) => <option key={a} value={a}>{a}</option>)}</select></td>
                <td><input type="number" min={20} max={2000} style={{ width: 70 }} aria-label={`Width of ${c.field}`} data-testid={`ed-column-width-${id}-${i}`} placeholder="auto" value={c.width ?? ''} onChange={(e) => setOrClear(['encodings', 'columns', i, 'width'], e.target.value === '' ? undefined : Number(e.target.value), `col${i}:width`)} /></td>
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

      {hasQuery && !explicit && <QuerySections id={id} widget={widget} doc={doc} catalog={catalog} apply={apply} />}

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

      <div data-testid={`ed-options-${id}`} style={{ marginTop: 8 }}>
        <strong>How each layout shows it</strong> <span className="hint">widget_options</span>
        {doc.layouts.map((layout) => {
          const o = layout.widget_options?.[id] ?? {}
          return (
            <div key={layout.name} className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', padding: '2px 0' }} data-testid={`ed-options-${id}-${layout.name}`}>
              <code style={{ minWidth: 70 }}>{layout.name}</code>
              <label className="hint">density{' '}
                <select data-testid={`ed-opt-${layout.name}-${id}-density`} value={o.density ?? ''} onChange={(e) => setOption(layout.name, 'density', e.target.value)}><option value="">(by the box)</option><option value="compact">compact</option><option value="comfortable">comfortable</option></select>
              </label>
              <label className="hint">title{' '}
                <select data-testid={`ed-opt-${layout.name}-${id}-show_title`} value={o.show_title === undefined ? '' : String(o.show_title)} onChange={(e) => setOption(layout.name, 'show_title', e.target.value)}><option value="">(shown)</option><option value="true">show</option><option value="false">hide</option></select>
              </label>
              {widget.type === 'chart' && (
                <label className="hint">legend{' '}
                  <select data-testid={`ed-opt-${layout.name}-${id}-legend`} value={o.legend ?? ''} onChange={(e) => setOption(layout.name, 'legend', e.target.value)}><option value="">(default)</option><option value="show">show</option><option value="hide">hide</option></select>
                </label>
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}
