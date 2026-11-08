import { useEffect, useState, type ReactNode } from 'react'
import { StoreClientProvider, useLoad, useStoreClient } from './context'
import { draftChange, keyOf, toInput } from './fields'
import { ErrorNote, FormView, GridView, HistoryView, InstancePickerView } from './views'
import { StoreError, type ApplyResult, type EntitySchema, type ModelSchema, type RowValues, type StoredRow, type Violation } from './types'

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
  /** Called when the write has gone through; `result.waiting` when it was kept for approval instead of applied. */
  onSaved?: (result?: ApplyResult) => void
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
    try { onSaved?.(await client.apply(model, [change])) }
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

/** Everything below reads and writes one instance of the models (null: the model's own copy). Takes the client from the provider above and scopes it. */
export function InstanceScope({ instance, children }: { instance: string | null; children: ReactNode }) {
  const client = useStoreClient()
  return <StoreClientProvider client={client.withInstance(instance)}>{children}</StoreClientProvider>
}

/** A picker of the instances of a model; nothing is drawn for a model that keeps one copy. */
export function InstancePicker({ model, value, onChange }: { model: string; value: string | null; onChange: (instance: string | null) => void }) {
  const client = useStoreClient()
  const list = useLoad(() => client.instances(model), [client, model])
  if (list.error) return <ErrorNote error={list.error} />
  if (!list.data) return null
  return <InstancePickerView list={list.data} value={value} onChange={onChange} />
}

export interface EntityEditorProps {
  model: string
  entity: string
  /** The instance shown (null: the model's own copy). With `onInstance`, the editor offers a picker; without it, the host chooses. */
  instance?: string | null
  onInstance?: (instance: string | null) => void
  /** Whether this person may change rows; the server decides in the end. */
  canWrite?: boolean
  pageSize?: number
  columns?: string[]
  /** Where to send a person whose write is waiting for approval: shown beside the notice (a link to the host's change requests). */
  waitingLink?: (requestId: string) => ReactNode
}

/** The grid and the form together: pick a row to edit it or its history, or start a new one. The one control most hosts want. */
export function EntityEditor({ model, entity: entityName, instance = null, onInstance, canWrite = true, pageSize, columns, waitingLink }: EntityEditorProps) {
  const picker = onInstance ? <InstancePicker model={model} value={instance} onChange={onInstance} /> : null
  return (
    <>
      {picker}
      <InstanceScope instance={instance}>
        <EditorBody key={instance ?? 'default'} model={model} entity={entityName} canWrite={canWrite} pageSize={pageSize} columns={columns} waitingLink={waitingLink} />
      </InstanceScope>
    </>
  )
}

function EditorBody({ model, entity: entityName, canWrite, pageSize, columns, waitingLink }: Pick<EntityEditorProps, 'model' | 'entity' | 'pageSize' | 'columns' | 'waitingLink'> & { canWrite: boolean }) {
  const schema = useSchema(model)
  const entity = entityOf(schema.data, entityName)
  const [selected, setSelected] = useState<StoredRow | null>(null)
  const [creating, setCreating] = useState(false)
  const [refresh, setRefresh] = useState(0)
  const [waiting, setWaiting] = useState<string | null>(null)
  if (schema.error) return <ErrorNote error={schema.error} />
  if (!entity) return <div className="sui-loading" aria-busy="true">Loading…</div>
  const done = (result?: ApplyResult) => { setSelected(null); setCreating(false); setRefresh((r) => r + 1); setWaiting(result?.waiting ? result.waiting.id : null) }
  return (
    <div className="sui-editor">
      <div className="sui-main">
        <div className="sui-toolbar">
          <h2>{entity.title ?? entity.name}</h2>
          <button type="button" data-testid="sui-new" disabled={!canWrite} title={canWrite ? undefined : 'Changing rows needs the author or administrator role.'} onClick={() => { setSelected(null); setCreating(true) }}>New</button>
          {!canWrite && <span className="sui-hint" data-testid="sui-readonly">Changing rows needs the author or administrator role.</span>}
        </div>
        {waiting !== null && (
          <div className="sui-notice" role="status" data-testid="sui-waiting">
            Not applied yet: your change was sent for approval{waiting ? ` as request ${waiting.slice(0, 8)}` : ''}. The rows change when someone approves it. {waitingLink?.(waiting)}
          </div>
        )}
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
