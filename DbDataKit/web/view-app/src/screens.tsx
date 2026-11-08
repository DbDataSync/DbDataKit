import { ErrorNote, StoreClientProvider, StoreError, draftChange, toInput, useLoad, type ModelSchema, type StoreClient, type StoredRow, type Violation } from '@dbdatakit/store-ui'
import { useEffect, useMemo, useState } from 'react'
import { loadAll } from './data'
import { DashboardBoard, DetailPage, FormFields, GridTable, Links, formAttributes } from './parts'
import { nextSort, shapeRows, type SortState } from './shape'
import { SUPPORTED_FORMAT, parseRoute, resolveParams, type DashboardSpec, type DetailSpec, type FormSpec, type GridSpec, type ViewSpec, type ViewsDoc } from './spec'
import type { Host } from './data'

export interface ScreenProps<S extends ViewSpec = ViewSpec> { spec: S; client: StoreClient; params: Record<string, string>; go: (route: string) => void }

const Loading = () => <div className="sui-loading" aria-busy="true">Loading…</div>

function useEntity(client: StoreClient, spec: { model: string; entity: string }) {
  const schema = useLoad<ModelSchema>(() => client.schema(spec.model), [client, spec.model])
  const entity = schema.data?.entities.find((e) => e.name === spec.entity)
  return { entity, error: schema.error ?? (schema.data && !entity ? new Error(`The model '${spec.model}' has no entity '${spec.entity}' for this app.`) : null) }
}

/** The one key attribute a detail or an edit form is addressed by. A composite key is not addressable by one parameter. */
function keyAttribute(entity: { key: string[]; name: string }): string | Error {
  return entity.key.length === 1 ? entity.key[0] : new Error(`'${entity.name}' has a key of ${entity.key.length} attributes; a view addresses a row by one.`)
}

export function GridScreen({ spec, client, params, go }: ScreenProps<GridSpec>) {
  const { entity, error } = useEntity(client, spec)
  const data = useLoad(() => loadAll(client, spec.model, spec.entity), [client, spec.model, spec.entity])
  const [filters, setFilters] = useState<Record<string, string>>({})
  const [sort, setSort] = useState<SortState[]>(spec.sort)
  const [offset, setOffset] = useState(0)
  const shaped = useMemo(() => shapeRows(data.data?.rows ?? [], filters, sort), [data.data, filters, sort])
  if (error || data.error) return <ErrorNote error={error ?? data.error} />
  if (!entity || !data.data) return <Loading />
  return (
    <GridTable spec={spec} entity={entity} rows={shaped.slice(offset, offset + spec.pageSize)} total={shaped.length} offset={offset} sort={sort} filters={filters} params={params} truncated={data.data.truncated}
      onSort={(a) => { setSort((s) => nextSort(s, a, spec.sort)); setOffset(0) }} onFilter={(a, v) => { setFilters((f) => ({ ...f, [a]: v })); setOffset(0) }} onPage={setOffset} go={go} />
  )
}

export function DetailScreen({ spec, client, params, go }: ScreenProps<DetailSpec>) {
  const { entity, error } = useEntity(client, spec)
  const key = entity ? keyAttribute(entity) : null
  const row = useLoad(async () => (entity && typeof key === 'string' ? client.row(spec.model, spec.entity, { [key]: params[spec.key] }) : null), [client, spec.model, spec.entity, entity, key, params[spec.key]])
  if (error || row.error) return <ErrorNote error={error ?? row.error} />
  if (key instanceof Error) return <ErrorNote error={key} />
  if (!entity || row.loading) return <Loading />
  if (!row.data) return <div className="sui-error" role="alert">{`There is no ${spec.entity} ${params[spec.key]}.`}</div>
  return <DetailPage spec={spec} entity={entity} row={row.data} params={params} go={go} />
}

export function FormScreen({ spec, client, params, go }: ScreenProps<FormSpec>) {
  const { entity, error } = useEntity(client, spec)
  const key = entity ? keyAttribute(entity) : null
  const keyValue = spec.key ? params[spec.key] : undefined
  const existing = useLoad<StoredRow | null>(async () => (spec.mode === 'edit' && entity && typeof key === 'string' ? client.row(spec.model, spec.entity, { [key]: keyValue }) : null), [client, spec.mode, entity, key, keyValue])
  const [text, setText] = useState<Record<string, string>>({})
  const [problems, setProblems] = useState<Record<string, string>>({})
  const [violations, setViolations] = useState<Violation[]>([])
  const [failure, setFailure] = useState<Error | null>(null)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState<string | null>(null)
  const row = existing.data ?? null
  const rowId = row ? JSON.stringify(row.values) + row.version : 'new'
  useEffect(() => {
    if (!entity) return
    setText(Object.fromEntries(formAttributes(spec, entity, row === null).map(({ attribute }) => [attribute.name, row ? toInput(attribute, row.values[attribute.name]) : ''])))
    setProblems({}); setViolations([]); setFailure(null)
    // the form starts again when the row (or its version) changes
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [entity, rowId])
  if (error || existing.error) return <ErrorNote error={error ?? existing.error} />
  if (key instanceof Error && spec.mode === 'edit') return <ErrorNote error={key} />
  if (!entity || existing.loading) return <Loading />
  if (spec.mode === 'edit' && !row) return <div className="sui-error" role="alert">{`There is no ${spec.entity} ${keyValue}.`}</div>

  const submit = async () => {
    // the entity the form draws: only the attributes the view lists, so a change carries only what the person could see
    const shown = new Set(spec.fields.map((f) => f.attribute))
    const draft = draftChange({ ...entity, attributes: entity.attributes.filter((a) => shown.has(a.name) && !spec.fields.find((f) => f.attribute === a.name)!.readOnly) }, row, text)
    setProblems(draft.problems); setSaved(null)
    if (!draft.change) { if (Object.keys(draft.problems).length === 0) setSaved('Nothing changed.'); return }
    setBusy(true); setFailure(null); setViolations([])
    try {
      await client.apply(spec.model, [draft.change])
      setSaved(row === null ? 'Created.' : 'Saved.')
      if (row === null) setText(Object.fromEntries(Object.keys(text).map((k) => [k, ''])))
      else existing.reload()
    } catch (e) {
      const f = e instanceof Error ? e : new Error(String(e))
      setFailure(f)
      if (f instanceof StoreError) {
        setViolations(f.violations)
        setProblems(Object.fromEntries(f.violations.filter((v) => v.attribute).map((v) => [v.attribute!, v.message])))
      }
    } finally { setBusy(false) }
  }
  return (
    <>
      <FormFields spec={spec} entity={entity} row={row} text={text} onText={(n, v) => setText((t) => ({ ...t, [n]: v }))} problems={problems} violations={violations} error={failure} busy={busy} saved={saved} onSubmit={() => void submit()} />
      <Links links={spec.links} row={row ? row.values : null} params={params} go={go} />
    </>
  )
}

export function DashboardScreen({ spec, client }: ScreenProps<DashboardSpec>) {
  const entities = [...new Set(spec.widgets.map((w) => w.entity))]
  const all = useLoad(async () => Object.fromEntries(await Promise.all(entities.map(async (e) => [e, await loadAll(client, spec.model, e)] as const))), [client, spec.model, entities.join()])
  if (all.error) return <ErrorNote error={all.error} />
  if (!all.data) return <Loading />
  return <DashboardBoard spec={spec} rows={Object.fromEntries(Object.entries(all.data).map(([e, l]) => [e, l.rows]))} truncated={Object.entries(all.data).filter(([, l]) => l.truncated).map(([e]) => e)} />
}

export function Problems({ items }: { items: string[] }) {
  return <div className="sui-error" role="alert"><b>These view files do not read:</b><ul>{items.map((p, i) => <li key={i}>{p}</li>)}</ul></div>
}

export interface ViewAppProps { doc: ViewsDoc; client: StoreClient; host: Pick<Host, 'route' | 'onNavigate'> }

/** The app: the view the route names (the first one by default), a menu of the views that need no parameter, and the view's own screen. Moving between views tells the shell, so the address and the back button follow. */
export function ViewApp({ doc, client, host }: ViewAppProps) {
  const [route, setRoute] = useState('/')
  useEffect(() => host.onNavigate((p) => setRoute(p)), [host])
  const go = (r: string) => { setRoute(r); host.route(r) }
  if (doc.format !== SUPPORTED_FORMAT) return <div className="sui-error" role="alert">{`This host reads ${SUPPORTED_FORMAT}; the app's views are ${doc.format}.`}</div>
  const { file, query } = parseRoute(route)
  const current = file ?? doc.home
  const spec = current ? doc.views[current] : undefined
  const menu = doc.files.filter((f) => doc.views[f] && doc.views[f].params.every((p) => !p.required))
  let body
  if (doc.problems.length > 0) body = <Problems items={doc.problems} />
  else if (!spec || !current) body = <div className="sui-error" role="alert">{`This app has no view '${current ?? ''}'.`}</div>
  else {
    const resolved = resolveParams(spec.params, query)
    if (resolved.errors.length > 0) body = <div className="sui-error" role="alert">{resolved.errors.map((e) => <p key={e}>{e}</p>)}</div>
    else {
      const props = { client, params: resolved.values, go, key: current + route }
      body = spec.kind === 'grid' ? <GridScreen spec={spec} {...props} /> : spec.kind === 'form' ? <FormScreen spec={spec} {...props} /> : spec.kind === 'detail' ? <DetailScreen spec={spec} {...props} /> : <DashboardScreen spec={spec} {...props} />
    }
  }
  return (
    <StoreClientProvider client={client}>
      <div className="va-app">
        {menu.length > 1 && (
          <nav className="va-menu" aria-label="Views">
            {menu.map((f) => <a key={f} href={`#/${f}`} aria-current={f === current ? 'page' : undefined} onClick={(e) => { e.preventDefault(); go(`/${f}`) }}>{doc.views[f].title}</a>)}
          </nav>
        )}
        <h2 className="va-title">{spec?.title ?? doc.title}</h2>
        {body}
        {spec && (spec.kind === 'grid' || spec.kind === 'dashboard') && <Links links={spec.links} row={null} params={resolveParams(spec.params, query).values} go={go} only={spec.kind === 'grid' ? 'page' : 'all'} />}
      </div>
    </StoreClientProvider>
  )
}
