import type { ReactNode } from 'react'
import { editable, formatCell, inputKind, keyOf, titleOf, toInput } from './fields'
import { StoreError, type AttributeSchema, type EntitySchema, type RowValues, type StoredRow, type Violation } from './types'

/** The presentational parts of the controls: props in, markup out, no client and no state, so a host can wire them to anything and a test can render them. */

export function ErrorNote({ error }: { error: Error | null | undefined }) {
  if (!error) return null
  return <div className="sui-error" role="alert">{error.message}</div>
}

export function ViolationList({ violations }: { violations: Violation[] }) {
  if (violations.length === 0) return null
  return (
    <ul className="sui-violations" role="alert">
      {violations.map((v, i) => <li key={i}><span className="sui-rule">{v.rule}</span> {v.attribute ? <b>{v.attribute}: </b> : null}{v.message}</li>)}
    </ul>
  )
}

export interface GridViewProps {
  entity: EntitySchema
  rows: StoredRow[]
  offset: number
  limit: number
  /** The key of the selected row, if any. */
  selected?: RowValues | null
  onSelect?: (row: StoredRow) => void
  onPage?: (offset: number) => void
  /** Attributes shown, in order; all by default. */
  columns?: string[]
}

export function sameKey(entity: EntitySchema, a: RowValues, b: RowValues) {
  return entity.key.every((k) => String(a[k]) === String(b[k]))
}

export function GridView({ entity, rows, offset, limit, selected, onSelect, onPage, columns }: GridViewProps) {
  const shown = (columns ? columns.map((c) => entity.attributes.find((a) => a.name === c)).filter((a): a is AttributeSchema => !!a) : entity.attributes)
  return (
    <div className="sui-grid">
      <table>
        <thead><tr>{shown.map((a) => <th key={a.name} scope="col">{titleOf(a)}</th>)}</tr></thead>
        <tbody>
          {rows.length === 0 && <tr><td className="sui-empty" colSpan={shown.length || 1}>No rows.</td></tr>}
          {rows.map((row) => {
            const isSelected = !!selected && sameKey(entity, keyOf(entity, row.values), selected)
            return (
              <tr key={entity.key.map((k) => String(row.values[k])).join('\u0001')} aria-selected={isSelected} className={isSelected ? 'sui-selected' : undefined} onClick={onSelect ? () => onSelect(row) : undefined}>
                {shown.map((a) => <td key={a.name} className={a.kind === 'decimal' || a.kind.startsWith('int') || a.kind === 'double' || a.kind === 'float' ? 'sui-num' : undefined}>{formatCell(a, row.values[a.name])}</td>)}
              </tr>
            )
          })}
        </tbody>
      </table>
      <div className="sui-pager">
        <button type="button" disabled={offset === 0 || !onPage} onClick={() => onPage?.(Math.max(0, offset - limit))}>Previous</button>
        <span>{rows.length === 0 ? 'No rows' : `${offset + 1}–${offset + rows.length}`}</span>
        <button type="button" disabled={rows.length < limit || !onPage} onClick={() => onPage?.(offset + limit)}>Next</button>
      </div>
    </div>
  )
}

export interface FieldProps {
  attribute: AttributeSchema
  value: string
  onChange?: (value: string) => void
  disabled?: boolean
  /** What is wrong with it, from the form or from the server. */
  problem?: string
}

/** One attribute as a labelled control. The hints (required, length, range) are help for the person; the server decides. */
export function FieldView({ attribute, value, onChange, disabled, problem }: FieldProps) {
  const kind = inputKind(attribute)
  const id = `sui-${attribute.name}`
  const set = (v: string) => onChange?.(v)
  let control: ReactNode
  if (kind === 'checkbox') control = <input id={id} type="checkbox" checked={value === 'true'} disabled={disabled} onChange={(e) => set(String(e.target.checked))} />
  else if (kind === 'select') control = (
    <select id={id} value={value} disabled={disabled} onChange={(e) => set(e.target.value)}>
      <option value="">{attribute.required ? 'Choose…' : ''}</option>
      {attribute.allowed!.map((a) => <option key={a} value={a}>{a}</option>)}
    </select>
  )
  else if (kind === 'textarea' || kind === 'json') control = <textarea id={id} value={value} rows={kind === 'json' ? 6 : 3} disabled={disabled} onChange={(e) => set(e.target.value)} />
  else {
    const type = kind === 'integer' || kind === 'number' ? 'number' : kind === 'datetime' ? 'datetime-local' : kind
    control = (
      <input id={id} type={type} value={value} disabled={disabled} step={kind === 'integer' ? 1 : kind === 'number' ? 'any' : undefined}
        min={attribute.min ?? undefined} max={attribute.max ?? undefined} maxLength={attribute.kind === 'string' && attribute.length ? attribute.length : undefined}
        onChange={(e) => set(e.target.value)} />
    )
  }
  const hints = [attribute.required ? 'required' : null, attribute.unique ? 'unique' : null, attribute.min !== null || attribute.max !== null ? `${attribute.min ?? ''}…${attribute.max ?? ''}` : null, attribute.references ? `→ ${attribute.references.entity}` : null].filter(Boolean)
  return (
    <div className={`sui-field${problem ? ' sui-invalid' : ''}`}>
      <label htmlFor={id}>{titleOf(attribute)}{attribute.required ? <span aria-hidden="true"> *</span> : null}</label>
      {control}
      {hints.length > 0 && <span className="sui-hint">{hints.join(' · ')}</span>}
      {problem && <span className="sui-problem" role="alert">{problem}</span>}
    </div>
  )
}

export interface FormViewProps {
  entity: EntitySchema
  /** The row being edited, or null for a new one. */
  row: StoredRow | null
  text: Record<string, string>
  onText?: (name: string, value: string) => void
  /** Problems by attribute: this form's own, and the server's. */
  problems?: Record<string, string>
  violations?: Violation[]
  error?: Error | null
  busy?: boolean
  readOnly?: boolean
  onSubmit?: () => void
  onCancel?: () => void
  onDelete?: () => void
}

export function FormView({ entity, row, text, onText, problems = {}, violations = [], error, busy, readOnly, onSubmit, onCancel, onDelete }: FormViewProps) {
  const isNew = row === null
  const conflict = error instanceof StoreError && error.status === 409
  return (
    <form className="sui-form" onSubmit={(e) => { e.preventDefault(); onSubmit?.() }}>
      <h3>{isNew ? `New ${titleOf(entity).toLowerCase()}` : `${titleOf(entity)} ${entity.key.map((k) => String(row!.values[k])).join(' / ')}`}</h3>
      {!isNew && entity.key.map((k) => {
        const attribute = entity.attributes.find((a) => a.name === k)!
        return <FieldView key={k} attribute={attribute} value={toInput(attribute, row!.values[k])} disabled />
      })}
      {editable(entity, isNew).map((a) => <FieldView key={a.name} attribute={a} value={text[a.name] ?? ''} onChange={(v) => onText?.(a.name, v)} disabled={readOnly || busy} problem={problems[a.name]} />)}
      <ViolationList violations={violations.filter((v) => !v.attribute || !problems[v.attribute])} />
      {conflict ? <div className="sui-error" role="alert">{error!.message} Reload the row to see its latest values.</div> : <ErrorNote error={violations.length > 0 ? null : error} />}
      {!readOnly && (
        <div className="sui-actions">
          <button type="submit" disabled={busy}>{isNew ? 'Create' : 'Save'}</button>
          {onCancel && <button type="button" onClick={onCancel} disabled={busy}>Cancel</button>}
          {!isNew && onDelete && <button type="button" className="sui-danger" onClick={onDelete} disabled={busy}>Delete</button>}
        </div>
      )}
    </form>
  )
}

export function HistoryView({ entity, versions }: { entity: EntitySchema; versions: StoredRow[] }) {
  return (
    <table className="sui-history">
      <thead><tr><th scope="col">Version</th><th scope="col">From</th><th scope="col">To</th>{entity.attributes.filter((a) => !entity.key.includes(a.name)).map((a) => <th key={a.name} scope="col">{titleOf(a)}</th>)}</tr></thead>
      <tbody>
        {versions.map((v) => (
          <tr key={`${v.version}-${v.valid_from}`}>
            <td>{v.version}</td><td>{v.valid_from ?? ''}</td><td>{v.valid_to ?? 'current'}</td>
            {entity.attributes.filter((a) => !entity.key.includes(a.name)).map((a) => <td key={a.name}>{formatCell(a, v.values[a.name])}</td>)}
          </tr>
        ))}
      </tbody>
    </table>
  )
}
