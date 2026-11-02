import { useEffect, useState } from 'react'
import { useLoad, useStoreClient } from './context'
import { draftChange, keyOf, toInput } from './fields'
import { ErrorNote, FormView, GridView, HistoryView } from './views'
import { StoreError, type EntitySchema, type ModelSchema, type RowValues, type StoredRow, type Violation } from './types'

/** The controls: each reads and writes through the nearest StoreClientProvider, and nothing else of the host. */

export function useSchema(model: string) {
  const client = useStoreClient()
  return useLoad(() => client.schema(model), [client, model])
}

export function entityOf(schema: ModelSchema | undefined, name: string): EntitySchema | undefined {
  return schema?.entities.find((e) => e.name === name)
}

export interface EntityGridProps {
  model: string
  entity: string
  pageSize?: number
  selected?: RowValues | null
  onSelect?: (row: StoredRow) => void
  columns?: string[]
  /** Changes when the host wrote something, to read the page again. */
  refresh?: number
}

/** A page of an entity's rows, in key order, with paging. */
export function EntityGrid({ model, entity, pageSize = 25, selected, onSelect, columns, refresh = 0 }: EntityGridProps) {
  const client = useStoreClient()
  const schema = useSchema(model)
  const [offset, setOffset] = useState(0)
  const page = useLoad(() => client.rows(model, entity, { limit: pageSize, offset }), [client, model, entity, pageSize, offset, refresh])
  const e = entityOf(schema.data, entity)
  if (schema.error || page.error) return <ErrorNote error={schema.error ?? page.error} />
  if (!e || !page.data) return <div className="sui-loading" aria-busy="true">Loading…</div>
  return <GridView entity={e} rows={page.data.rows} offset={offset} limit={pageSize} selected={selected} onSelect={onSelect} onPage={setOffset} columns={columns} />
}

export interface RowFormProps {
  model: string
  entity: string
  /** The row to edit; null for a new one. */
  row: StoredRow | null
  readOnly?: boolean
  onSaved?: () => void
  onCancel?: () => void
  /** Offers Delete on an existing row. */
  allowDelete?: boolean
}

function initialText(entity: EntitySchema, row: StoredRow | null) {
  return Object.fromEntries(entity.attributes.map((a) => [a.name, row ? toInput(a, row.values[a.name]) : a.kind === 'boolean' ? '' : '']))
}

/** A form made from the model: a control for each attribute, the key fixed once the row exists, the server's refusals shown against the attribute they name. */
export function RowForm({ model, entity: entityName, row, readOnly, onSaved, onCancel, allowDelete }: RowFormProps) {
  const client = useStoreClient()
  const schema = useSchema(model)
  const entity = entityOf(schema.data, entityName)
  const [text, setText] = useState<Record<string, string>>({})
  const [problems, setProblems] = useState<Record<string, string>>({})
  const [violations, setViolations] = useState<Violation[]>([])
  const [error, setError] = useState<Error | null>(null)
  const [busy, setBusy] = useState(false)
  const rowId = row ? JSON.stringify(row.values) + row.version : 'new'
  useEffect(() => {
    if (entity) setText(initialText(entity, row))
    setProblems({}); setViolations([]); setError(null)
    // the form starts again when the row (or its version) changes
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [entity, rowId])

  if (schema.error) return <ErrorNote error={schema.error} />
  if (!entity) return <div className="sui-loading" aria-busy="true">Loading…</div>

  const send = async (change: Parameters<typeof client.apply>[1][number]) => {
    setBusy(true); setError(null); setViolations([])
    try { await client.apply(model, [change]); onSaved?.() }
    catch (e) {
      const failure = e instanceof Error ? e : new Error(String(e))
      setError(failure)
      if (failure instanceof StoreError) {
        setViolations(failure.violations)
        setProblems(Object.fromEntries(failure.violations.filter((v) => v.attribute).map((v) => [v.attribute!, v.message])))
      }
    } finally { setBusy(false) }
  }
  const submit = () => {
    const draft = draftChange(entity, row, text)
    setProblems(draft.problems)
    if (draft.change) void send(draft.change)
    else if (Object.keys(draft.problems).length === 0) onSaved?.()   // an update that changed nothing
  }
  const remove = () => { if (row) void send({ action: 'delete', entity: entity.name, key: keyOf(entity, row.values), expected_version: row.version }) }
  return (
    <FormView entity={entity} row={row} text={text} onText={(n, v) => setText((t) => ({ ...t, [n]: v }))} problems={problems} violations={violations} error={error} busy={busy} readOnly={readOnly}
      onSubmit={submit} onCancel={onCancel} onDelete={allowDelete ? remove : undefined} />
  )
}

/** The versions of a row, oldest first (an entity that keeps history). */
export function RowHistory({ model, entity: entityName, rowKey }: { model: string; entity: string; rowKey: RowValues }) {
  const client = useStoreClient()
  const schema = useSchema(model)
  const versions = useLoad(() => client.history(model, entityName, rowKey), [client, model, entityName, JSON.stringify(rowKey)])
  const e = entityOf(schema.data, entityName)
  if (schema.error || versions.error) return <ErrorNote error={schema.error ?? versions.error} />
  if (!e || !versions.data) return <div className="sui-loading" aria-busy="true">Loading…</div>
  return <HistoryView entity={e} versions={versions.data} />
}

export interface EntityEditorProps {
  model: string
  entity: string
  /** Whether this person may change rows; the server decides in the end. */
  canWrite?: boolean
  pageSize?: number
  columns?: string[]
}

/** The grid and the form together: pick a row to edit it or its history, or start a new one. The one control most hosts want. */
export function EntityEditor({ model, entity: entityName, canWrite = true, pageSize, columns }: EntityEditorProps) {
  const schema = useSchema(model)
  const entity = entityOf(schema.data, entityName)
  const [selected, setSelected] = useState<StoredRow | null>(null)
  const [creating, setCreating] = useState(false)
  const [refresh, setRefresh] = useState(0)
  if (schema.error) return <ErrorNote error={schema.error} />
  if (!entity) return <div className="sui-loading" aria-busy="true">Loading…</div>
  const done = () => { setSelected(null); setCreating(false); setRefresh((r) => r + 1) }
  return (
    <div className="sui-editor">
      <div className="sui-main">
        <div className="sui-toolbar">
          <h2>{entity.title ?? entity.name}</h2>
          {canWrite && <button type="button" onClick={() => { setSelected(null); setCreating(true) }}>New</button>}
        </div>
        <EntityGrid model={model} entity={entityName} pageSize={pageSize} columns={columns} refresh={refresh} selected={selected ? keyOf(entity, selected.values) : null} onSelect={(r) => { setCreating(false); setSelected(r) }} />
      </div>
      {(selected || creating) && (
        <aside className="sui-side">
          <RowForm model={model} entity={entityName} row={creating ? null : selected} readOnly={!canWrite} allowDelete={canWrite} onSaved={done} onCancel={() => { setSelected(null); setCreating(false) }} />
          {selected && entity.history !== 'none' && <RowHistory model={model} entity={entityName} rowKey={keyOf(entity, selected.values)} />}
        </aside>
      )}
    </div>
  )
}
