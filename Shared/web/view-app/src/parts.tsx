import { ErrorNote, FieldView, StoreError, ViolationList, formatCell, titleOf, type AttributeSchema, type EntitySchema, type StoredRow, type Violation } from '@dbdatakit/store-ui'
import { DataTable, EmbedBlock, VegaChart } from '@dbdatakit/visuals'
import { useScheme } from './scheme'
import type { ReactNode } from 'react'
import { chartDocument, formatNumber, measure, measureTitle, tableRows, type SortState } from './shape'
import { linkRoute, type DashboardSpec, type DetailSpec, type FormSpec, type GridSpec, type LinkSpec } from './spec'

/** The presentational parts: props in, markup out, so a test renders them without a browser and the screens only wire them to data. */

export function attributeOf(entity: EntitySchema, name: string): AttributeSchema | undefined {
  return entity.attributes.find((a) => a.name === name)
}

const labelOf = (entity: EntitySchema, name: string, label: string | null) => label ?? (attributeOf(entity, name) ? titleOf(attributeOf(entity, name)!) : name)

/** Links of a view. In a grid a link is either one of every row (`row: true`, drawn in the row) or one of the page; in a detail page, a form or a dashboard there are no rows, so every link is drawn (a detail page's or an edit form's may take `$attribute` of the row it shows). */
export function Links({ links, row, params, go, only = 'all' }: { links: LinkSpec[]; row: Record<string, unknown> | null; params: Record<string, string>; go: (route: string) => void; only?: 'row' | 'page' | 'all' }) {
  const shown = links.filter((l) => only === 'all' || (only === 'row') === l.row).map((l) => ({ l, route: linkRoute(l, row, params) })).filter((x) => x.route !== null)
  if (shown.length === 0) return null
  return (
    <span className="va-links">
      {shown.map(({ l, route }) => <a key={l.label + route} href={`#${route}`} onClick={(e) => { e.preventDefault(); go(route!) }}>{l.label}</a>)}
    </span>
  )
}

export interface GridTableProps {
  spec: GridSpec
  entity: EntitySchema
  /** The page of rows to show, already shaped. */
  rows: StoredRow[]
  total: number
  offset: number
  sort: SortState[]
  filters: Record<string, string>
  params: Record<string, string>
  truncated?: boolean
  onSort?: (attribute: string) => void
  onFilter?: (attribute: string, value: string) => void
  onPage?: (offset: number) => void
  go: (route: string) => void
}

export function GridTable({ spec, entity, rows, total, offset, sort, filters, params, truncated, onSort, onFilter, onPage, go }: GridTableProps) {
  const rowLinks = spec.links.some((l) => l.row)
  return (
    <div className="sui-grid va-grid" data-testid="va-grid">
      {spec.filters.length > 0 && (
        <div className="va-filters">
          {spec.filters.map((f) => (
            <label key={f.name}>{labelOf(entity, f.name, f.label)}
              <input type="search" value={filters[f.name] ?? ''} onChange={(e) => onFilter?.(f.name, e.target.value)} />
            </label>
          ))}
        </div>
      )}
      <table>
        <thead>
          <tr>
            {spec.columns.map((c) => {
              const active = sort[0]?.attribute === c.attribute ? sort[0].direction : null
              const label = labelOf(entity, c.attribute, c.label)
              return (
                <th key={c.attribute} scope="col" aria-sort={active === 'asc' ? 'ascending' : active === 'desc' ? 'descending' : undefined}>
                  {c.sortable ? <button type="button" className="va-sort" onClick={() => onSort?.(c.attribute)}>{label}{active ? (active === 'asc' ? ' ▲' : ' ▼') : ''}</button> : label}
                </th>
              )
            })}
            {rowLinks && <th scope="col"><span className="va-sr">Links</span></th>}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 && <tr><td className="sui-empty" colSpan={spec.columns.length + (rowLinks ? 1 : 0) || 1}>No rows.</td></tr>}
          {rows.map((row, i) => (
            <tr key={entity.key.map((k) => String(row.values[k])).join('\u0001') + i}>
              {spec.columns.map((c) => {
                const a = attributeOf(entity, c.attribute)
                const numeric = a && (a.kind === 'decimal' || a.kind.startsWith('int') || a.kind === 'double' || a.kind === 'float')
                return <td key={c.attribute} className={numeric ? 'sui-num' : undefined}>{a ? formatCell(a, row.values[c.attribute]) : String(row.values[c.attribute] ?? '')}</td>
              })}
              {rowLinks && <td><Links links={spec.links} row={row.values} params={params} go={go} only="row" /></td>}
            </tr>
          ))}
        </tbody>
      </table>
      <div className="sui-pager">
        <button type="button" disabled={offset === 0 || !onPage} onClick={() => onPage?.(Math.max(0, offset - spec.pageSize))}>Previous</button>
        <span>{total === 0 ? 'No rows' : `${offset + 1}–${offset + rows.length} of ${total}`}</span>
        <button type="button" disabled={offset + spec.pageSize >= total || !onPage} onClick={() => onPage?.(offset + spec.pageSize)}>Next</button>
      </div>
      {truncated && <p className="sui-hint" role="note">Only the first rows are read; the figures above cover those.</p>}
    </div>
  )
}

export interface FormFieldsProps {
  spec: FormSpec
  entity: EntitySchema
  /** The row being edited; null for a new one. */
  row: StoredRow | null
  text: Record<string, string>
  onText?: (name: string, value: string) => void
  problems?: Record<string, string>
  violations?: Violation[]
  error?: Error | null
  busy?: boolean
  saved?: string | null
  onSubmit?: () => void
}

/** The attributes of a form view, as the shared field controls: the spec's label, `required` and `readonly` over the model's own type and limits. A key of an existing row is shown and never edited. */
export function formAttributes(spec: FormSpec, entity: EntitySchema, isNew: boolean) {
  return spec.fields.flatMap((f) => {
    const a = attributeOf(entity, f.attribute)
    if (!a) return []
    return [{ attribute: { ...a, title: f.label ?? a.title, required: a.required || f.required } as AttributeSchema, help: f.help, readOnly: f.readOnly || (!isNew && entity.key.includes(a.name)) }]
  })
}

export function FormFields({ spec, entity, row, text, onText, problems = {}, violations = [], error, busy, saved, onSubmit }: FormFieldsProps) {
  const fields = formAttributes(spec, entity, row === null)
  return (
    <form className="sui-form va-form" data-testid="va-form" noValidate onSubmit={(e) => { e.preventDefault(); onSubmit?.() }}>
      {fields.map(({ attribute, help, readOnly }) => (
        <div key={attribute.name}>
          <FieldView attribute={attribute} value={text[attribute.name] ?? ''} onChange={(v) => onText?.(attribute.name, v)} disabled={busy || readOnly} problem={problems[attribute.name]} />
          {help && <span className="sui-hint">{help}</span>}
        </div>
      ))}
      <ViolationList violations={violations.filter((v) => !v.attribute || !problems[v.attribute])} />
      {violations.length === 0 && (error instanceof StoreError && error.status === 409
        ? <div className="sui-error" role="alert">{error.message} Reload the row to see its latest values.</div>
        : <ErrorNote error={error} />)}
      {saved && <div className="va-saved" role="status">{saved}</div>}
      <div className="sui-actions"><button type="submit" disabled={busy}>{spec.submit ?? (row === null ? 'Create' : 'Save')}</button></div>
    </form>
  )
}

export function DetailPage({ spec, entity, row, params, go }: { spec: DetailSpec; entity: EntitySchema; row: StoredRow; params: Record<string, string>; go: (route: string) => void }) {
  return (
    <div className="va-detail" data-testid="va-detail">
      {spec.sections.map((s) => (
        <section key={s.title}>
          <h3>{s.title}</h3>
          <dl>
            {s.attributes.map((x) => {
              const a = attributeOf(entity, x.name)
              return <div key={x.name}><dt>{labelOf(entity, x.name, x.label)}</dt><dd>{a ? formatCell(a, row.values[x.name]) : String(row.values[x.name] ?? '')}</dd></div>
            })}
          </dl>
        </section>
      ))}
      <Links links={spec.links} row={row.values} params={params} go={go} />
    </div>
  )
}

/** The widgets of a dashboard, in order: a figure for a metric, the shared chart renderer for a bar or a line, the shared table for a table. `rows` is the rows read of each widget's entity. */
export function DashboardBoard({ spec, rows, truncated }: { spec: DashboardSpec; rows: Record<string, StoredRow[]>; truncated?: string[] }) {
  const theme = useScheme()
  return (
    <div className="va-dashboard" data-testid="va-dashboard">
      {spec.widgets.map((w, i) => {
        const data = rows[w.entity] ?? []
        let body: ReactNode
        // a report of an external report server (plan 26 AP17): a frame when the host approves its origin, always with its link; the policy of this document lists the same origins for `frame-src`, so a frame the host did not approve is blocked as well
        if (w.kind === 'embed') body = <EmbedBlock widget={{ type: 'embed', title: w.title, url: w.url ?? '', ...(w.link_text ? { link_text: w.link_text } : {}) }} result={{ type: 'embed', url: w.url ?? '', framed: w.framed === true, ...(w.link_text ? { link_text: w.link_text } : {}) }} />
        else if (w.kind === 'metric') body = <p className="va-metric" data-testid={`va-metric-${i}`}><b>{formatNumber(measure(data, w))}</b> <span>{measureTitle(w)}</span></p>
        else if (w.kind === 'table') {
          const t = tableRows(data, w)
          body = <DataTable id={`w${i}`} columns={t.columns} rows={t.rows} limit={w.limit} />
        } else {
          // the chart is made here from data and a mark (`chartDocument`, checked against the shared renderer's rules in the tests), so it goes straight to the chart: the renderer's validator compiles schemas with `new Function`, which the app's policy refuses
          const view = chartDocument([{ id: `w${i}`, widget: w, rows: data }]).views[0]
          body = <div className="vz" data-vz-theme={theme}><figure className="vz-view vz-view-chart"><VegaChart view={view} theme={theme} /></figure></div>
        }
        return <section key={i} className={`va-widget va-w-${w.kind}`}>{w.kind !== 'bar' && w.kind !== 'line' && <h3>{w.title}</h3>}{body}</section>
      })}
      {truncated && truncated.length > 0 && <p className="sui-hint" role="note">{`Only the first rows of ${truncated.join(', ')} are read; the figures cover those.`}</p>}
    </div>
  )
}
