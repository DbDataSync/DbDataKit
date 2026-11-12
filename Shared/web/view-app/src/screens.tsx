import { ErrorNote, StoreClientProvider, StoreError, draftChange, toInput, useLoad, type ModelSchema, type StoreClient, type StoredRow, type Violation } from '@dbdatakit/store-ui'
import { useEffect, useMemo, useState, type ComponentType } from 'react'
import { loadAll } from './data'
import { rowKey } from './keys'
import { SchemeContext, useShellTheme } from './scheme'
import { DashboardBoard, DetailPage, FormFields, GridTable, Links, formAttributes } from './parts'
import { nextSort, shapeRows, type SortState } from './shape'
import { AS_PARAM, SUPPORTED_FORMAT, lockedFields, notifyParent, openedByLink, parseRoute, readsRows, resolveParams, startingValues, type DashboardSpec, type DetailSpec, type FormSpec, type GridSpec, type ViewSpec, type ViewsDoc } from './spec'
import type { Host } from './data'
import { DocumentScreen } from './document'
import type { OpenSurface, ViewsClient } from '@dbdatakit/visuals'

export interface ScreenProps<S extends ViewSpec = ViewSpec> { spec: S; client: StoreClient; params: Record<string, string>; go: (route: string) => void; /** The route's whole query: the starting values and `_lock` of a form a link opened. */ query?: Record<string, string> }

const Loading = () => <div className="sui-loading" aria-busy="true">Loading…</div>

function useEntity(client: StoreClient, spec: { model: string; entity: string }) {
  const schema = useLoad<ModelSchema>(() => client.schema(spec.model), [client, spec.model])
  const entity = schema.data?.entities.find((e) => e.name === spec.entity)
  return { entity, error: schema.error ?? (schema.data && !entity ? new Error(`The model '${spec.model}' has no entity '${spec.entity}' for this app.`) : null) }
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
  const key = entity ? rowKey(entity, spec.key, params) : null
  const keyText = key && !(key instanceof Error) ? JSON.stringify(key.key) : ''
  const row = useLoad(async () => (entity && key && !(key instanceof Error) ? client.row(spec.model, spec.entity, key.key) : null), [client, spec.model, spec.entity, entity, keyText])
  if (error || row.error) return <ErrorNote error={error ?? row.error} />
  if (key instanceof Error) return <ErrorNote error={key} />
  if (!entity || row.loading) return <Loading />
  if (!row.data) return <div className="sui-error" role="alert">{`There is no ${spec.entity} ${key?.label ?? ''}.`}</div>
  return <DetailPage spec={spec} entity={entity} row={row.data} params={params} go={go} />
}

export function FormScreen({ spec, client, params, go, query = {} }: ScreenProps<FormSpec>) {
  const starting = startingValues(spec, query)
  const locked = lockedFields(spec, query)
  const framed = openedByLink(query)
  const { entity, error } = useEntity(client, spec)
  const key = entity && spec.mode === 'edit' ? rowKey(entity, spec.key, params) : null
  const keyText = key && !(key instanceof Error) ? JSON.stringify(key.key) : ''
  const existing = useLoad<StoredRow | null>(async () => (spec.mode === 'edit' && entity && key && !(key instanceof Error) ? client.row(spec.model, spec.entity, key.key) : null), [client, spec.mode, entity, keyText])
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
    setText(Object.fromEntries(formAttributes(spec, entity, row === null).map(({ attribute }) => [attribute.name, row ? toInput(attribute, row.values[attribute.name]) : starting[attribute.name] ?? ''])))
    setProblems({}); setViolations([]); setFailure(null)
    // the form starts again when the row (or its version) changes
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [entity, rowId])
  if (error || existing.error) return <ErrorNote error={error ?? existing.error} />
  if (key instanceof Error) return <ErrorNote error={key} />
  if (!entity || existing.loading) return <Loading />
  if (spec.mode === 'edit' && !row) return <div className="sui-error" role="alert">{`There is no ${spec.entity} ${key?.label ?? ''}.`}</div>

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
      if (row === null) setText(Object.fromEntries(Object.keys(text).map((k) => [k, starting[k] ?? ''])))
      else existing.reload()
      // a form a link opened in a dialog or a panel tells the page that framed it, which closes it and has the widget read again (`then`)
      if (framed) notifyParent({ type: 'views:saved' })
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
      <FormFields spec={spec} entity={entity} row={row} text={text} onText={(n, v) => setText((t) => ({ ...t, [n]: v }))} problems={problems} violations={violations} error={failure} busy={busy} saved={saved} onSubmit={() => void submit()} locked={locked} onCancel={framed ? () => notifyParent({ type: 'views:close' }) : undefined} />
      <Links links={spec.links} row={row ? row.values : null} params={params} go={go} />
    </>
  )
}

export function DashboardScreen({ spec, client }: ScreenProps<DashboardSpec>) {
  const entities = [...new Set(spec.widgets.filter(readsRows).map((w) => w.entity))]
  const all = useLoad(async () => Object.fromEntries(await Promise.all(entities.map(async (e) => [e, await loadAll(client, spec.model, e)] as const))), [client, spec.model, entities.join()])
  if (all.error) return <ErrorNote error={all.error} />
  if (!all.data) return <Loading />
  return <DashboardBoard spec={spec} rows={Object.fromEntries(Object.entries(all.data).map(([e, l]) => [e, l.rows]))} truncated={Object.entries(all.data).filter(([, l]) => l.truncated).map(([e]) => e)} />
}

export function Problems({ items }: { items: string[] }) {
  return <div className="sui-error" role="alert"><b>These view files do not read:</b><ul>{items.map((p, i) => <li key={i}>{p}</li>)}</ul></div>
}

/** What a code page is given: the Store client, the parameters its route carried (the query after `?`), and `go` to move to another view or page (`/views/grid.yaml`, `/pages/overview?code=NA`). */
export interface PageProps { client: StoreClient; params: Record<string, string>; go: (route: string) => void }

/** A page written as code, shown beside the spec files (plan 22a GP34): its title is the menu's entry. Its key is its route (`pages/overview` is `/pages/overview`). A key that is also a spec file of the manifest draws the page **in place of** that spec. */
export interface CodePage { title: string; component: ComponentType<PageProps> }

export type CodePages = Record<string, CodePage>

export interface ViewAppProps {
  doc: ViewsDoc
  client: StoreClient
  host: Pick<Host, 'route' | 'onNavigate'> & Partial<Pick<Host, 'onTheme'>>
  /** Pages of code. An app can be only spec files (none here); one page can be code and the rest stay specs. */
  pages?: CodePages
  /** The views client of the app (`appViewsClient`): what draws the app's dashboards and reports (plan 32 DBR13). Without it such a file says the host cannot draw it. */
  views?: ViewsClient
  /** Opens a link's dialog or panel in the shell (plan 32 DBR36); without it such a link goes to its target as a page. */
  openSurface?: (surface: OpenSurface, closed: (saved: boolean) => void) => void
}

/** The views and pages a person can open from the menu: the spec files that need no parameter, in the manifest's order (a code page with the key of a spec takes its place), then the other code pages in key order. */
export function menuOf(doc: ViewsDoc, pages: CodePages = {}): { key: string; title: string }[] {
  const specs = doc.files.flatMap((f) => pages[f] ? [{ key: f, title: pages[f].title }] : doc.views[f] && doc.views[f].params.every((p) => !p.required) ? [{ key: f, title: doc.views[f].title }] : [])
  return [...specs, ...Object.keys(pages).filter((k) => !doc.files.includes(k)).sort().map((k) => ({ key: k, title: pages[k].title }))]
}

/** The app: the view or page the route names (the first one by default), a menu of the views that need no parameter and the pages, and the screen. Moving between them tells the shell, so the address and the back button follow. */
export function ViewApp({ doc, client, host, pages = {}, views, openSurface }: ViewAppProps) {
  const [route, setRoute] = useState('/')
  useEffect(() => host.onNavigate((p) => setRoute(p)), [host])
  const shellScheme = useShellTheme(host)   // the portal's look reaches the controls as --sui-* variables and the charts as a scheme
  const go = (r: string) => { setRoute(r); host.route(r) }
  if (doc.format !== SUPPORTED_FORMAT) return <div className="sui-error" role="alert">{`This host reads ${SUPPORTED_FORMAT}; the app's views are ${doc.format}.`}</div>
  const { file, query } = parseRoute(route)
  const menu = menuOf(doc, pages)
  const current = file ?? doc.home ?? menu[0]?.key ?? null
  const page = current ? pages[current] : undefined
  const spec = current && !page ? doc.views[current] : undefined
  let body
  if (page) body = <page.component key={current + route} client={client} params={Object.fromEntries(Object.entries(query))} go={go} />
  else if (doc.problems.length > 0) body = <Problems items={doc.problems} />
  else if (!spec || !current) body = <div className="sui-error" role="alert">{`This app has no view '${current ?? ''}'.`}</div>
  else {
    const resolved = resolveParams(spec.params, query)
    if (resolved.errors.length > 0) body = <div className="sui-error" role="alert">{resolved.errors.map((e) => <p key={e}>{e}</p>)}</div>
    else {
      const props = { client, params: resolved.values, go, query }
      const key = current + route   // a new route starts the screen again (React takes a key as a prop of its own, never in a spread)
      body = spec.kind === 'document'
        ? (views
          ? <DocumentScreen key={key} file={current} kind={spec.document} views={views} query={query} doc={doc} route={host.route} go={go} openSurface={openSurface} />
          : <div className="sui-error" role="alert">{`This host cannot draw the ${spec.document} '${current}': it gives the app no views client.`}</div>)
        : spec.kind === 'grid' ? <GridScreen key={key} spec={spec} {...props} /> : spec.kind === 'form' ? <FormScreen key={key} spec={spec} {...props} /> : spec.kind === 'detail' ? <DetailScreen key={key} spec={spec} {...props} /> : <DashboardScreen key={key} spec={spec} {...props} />
    }
  }
  // a portal's home block draws a dashboard or report to the whole frame (`?_as=block`): no menu, no title, the board is the box (plan 32 DBR13)
  const block = spec?.kind === 'document' && query[AS_PARAM] === 'block'
  const titleOf = page?.title ?? spec?.title ?? doc.title
  return (
    <StoreClientProvider client={client}>
      <SchemeContext.Provider value={shellScheme}>
      <div className={block ? 'va-app va-block' : 'va-app'}>
        {!block && menu.length > 1 && (
          <nav className="va-menu" aria-label="Views">
            {menu.map((m) => <a key={m.key} href={`#/${m.key}`} aria-current={m.key === current ? 'page' : undefined} onClick={(e) => { e.preventDefault(); go(`/${m.key}`) }}>{m.title}</a>)}
          </nav>
        )}
        {!block && <h2 className="va-title">{titleOf}</h2>}
        {body}
        {spec && (spec.kind === 'grid' || spec.kind === 'dashboard') && <Links links={spec.links} row={null} params={resolveParams(spec.params, query).values} go={go} only={spec.kind === 'grid' ? 'page' : 'all'} />}
      </div>
      </SchemeContext.Provider>
    </StoreClientProvider>
  )
}
