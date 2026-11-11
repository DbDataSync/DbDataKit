import { useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ViewsClientError, type ViewKind, type ViewsDoc, type WidgetDoc } from '@dbdatakit/visuals'
import { personalViewsClient, type ViewsSource } from '../viewsSource'
import { withSource } from '../viewsUrl'
import { MINE_NAME, pinWidget, sharedName, splitName, widgetFromRequest, type MineItem, type PinResult, type QueryShape, type VersionInfo } from '../userViews'

const AREA: Record<ViewKind, { path: string; plural: string; singular: string }> = {
  dashboard: { path: '/views/dashboards', plural: 'Dashboards', singular: 'dashboard' },
  report: { path: '/views/reports', plural: 'Reports', singular: 'report' },
}

const say = (e: unknown) => (e instanceof Error ? e.message : String(e))

/** "Shared by Wendy": who owns the shared document that is open, by name (the address carries their id), and what that means for the rows. */
export function SharedBy({ source, kind, name }: { source: ViewsSource; kind: ViewKind; name: string }) {
  const client = personalViewsClient(source.id)
  const mine = useQuery({ queryKey: ['views', 'mine', source.id, kind], queryFn: () => client.mine(kind), retry: false })
  const { owner, name: bare } = splitName(name)
  const found = mine.data?.find((i) => !i.own && i.owner === owner && i.name === bare)
  return <span className="hint" data-testid="views-shared-by">Shared by {found?.ownerName ?? 'another person'}; you see the rows your own access gives.</span>
}

/** How a share is written for a person: `user:alice` is "alice", `group:analyst` is "the analyst group", `*` is "everyone". */
export function describeGrantee(grantee: string): string {
  if (grantee === '*') return 'everyone who signs in'
  if (grantee.startsWith('group:')) return `the ${grantee.slice(6)} group`
  if (grantee.startsWith('user:')) return grantee.slice(5)
  return grantee
}

type Panel = { kind: 'share' | 'history' | 'promote'; item: MineItem } | null

/**
 * "My dashboards" (plan 32 DBR12), the personal source of the Views page: the dashboards (or reports) a person made for themselves and the ones others shared with them, each with who owns it, its version and who it is
 * shared with. An own document is opened, edited (the editor of the Views page, against this source), **shared** to a person, a group or everyone, shown with its **history** (an earlier version restored as a new one), **promoted** to a file
 * for an app or a Build project, or deleted; a shared one is opened (it shows **your** rows, not its owner's) or copied. Below the list a query can be pinned: a semantic request kept as a widget of one of your dashboards.
 */
export function MyViewsPanel({ kind, source, keep, picker }: { kind: ViewKind; source: ViewsSource; keep: string | null; picker: ReactNode }) {
  const area = AREA[kind]
  const client = personalViewsClient(source.id)
  const queryClient = useQueryClient()
  const mine = useQuery({ queryKey: ['views', 'mine', source.id, kind], queryFn: () => client.mine(kind), retry: false })
  const [panel, setPanel] = useState<Panel>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null)
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['views'] })
  const open = (item: MineItem) => withSource(`${area.path}/${encodeURIComponent(item.own ? item.name : sharedName(item.name, item.owner))}`, keep)

  /** A copy of a shared document under a name of the person's own (the shared one is the owner's and cannot be changed). */
  const copy = async (item: MineItem) => {
    setNotice(null)
    try {
      const opened = await client.open(kind, sharedName(item.name, item.owner))
      let name = item.name, n = 1
      const taken = new Set((mine.data ?? []).filter((i) => i.own && i.kind === kind).map((i) => i.name))
      while (taken.has(name)) name = `${item.name}-copy${n > 1 ? n : ''}`, n++
      await client.save(kind, name, opened.text)
      await refresh()
      setNotice(`Copied to your own ${area.singular} \`${name}\`.`)
    } catch (e) { setNotice(say(e)) }
  }

  return (
    <div data-testid="my-views" data-kind={kind}>
      <div className="card" style={{ marginBottom: 16 }}>
        {picker}
        <div className="row" style={{ gap: 8, alignItems: 'center' }}>
          <Link className="btn btn-primary" to={withSource(`${area.path}?new=1`, keep)} data-testid="views-new">New {area.singular}</Link>
          <span className="hint">Yours until you share it. Saving keeps every version, so a change can be undone. Whoever opens a document sees the rows their own access gives, never the owner's.</span>
        </div>
      </div>
      {notice && <div className="hint" role="status" data-testid="my-notice" style={{ margin: '6px 0' }}>{notice}</div>}
      {mine.isPending && <div className="empty" data-testid="views-loading" role="status">Loading your {area.plural.toLowerCase()}…</div>}
      {mine.isError && <div className="empty" data-testid="views-error" role="alert">Your {area.plural.toLowerCase()} could not be listed: {say(mine.error)}</div>}
      {mine.data && mine.data.length === 0 && (
        <div className="empty" data-testid="views-empty">
          You have no {area.plural.toLowerCase()} of your own yet, and nobody has shared one with you. Make one with New {area.singular}, or pin a widget from a {area.singular} you can open (Pin to my dashboard on its page).
        </div>
      )}
      {mine.data && mine.data.length > 0 && (
        <table className="table" data-testid="views-list">
          <thead><tr><th>Name</th><th>Title</th><th>Owner</th><th>Version</th><th>Shared with</th><th /></tr></thead>
          <tbody>
            {mine.data.map((item) => {
              const id = item.own ? item.name : sharedName(item.name, item.owner)
              return (
                <tr key={id} data-testid={`views-row-${id}`}>
                  <td><Link to={open(item)} data-testid={`views-open-${id}`}>{item.name}</Link></td>
                  <td>{item.title}</td>
                  <td data-testid={`my-owner-${id}`}>{item.own ? 'You' : item.ownerName}</td>
                  <td>{item.version}</td>
                  <td data-testid={`my-shared-${id}`}>{item.own ? (item.sharedWith.length === 0 ? 'Only you' : item.sharedWith.map(describeGrantee).join(', ')) : 'Shared with you'}</td>
                  <td>
                    {item.own ? (
                      <div className="row" style={{ gap: 4, flexWrap: 'wrap' }}>
                        <Link className="btn btn-sm" to={withSource(`${area.path}/${encodeURIComponent(item.name)}?edit=1`, keep)} data-testid={`my-edit-${id}`}>Edit</Link>
                        <button type="button" className="btn btn-sm" data-testid={`my-share-${id}`} onClick={() => setPanel({ kind: 'share', item })}>Share</button>
                        <button type="button" className="btn btn-sm" data-testid={`my-history-${id}`} onClick={() => setPanel({ kind: 'history', item })}>History</button>
                        <button type="button" className="btn btn-sm" data-testid={`my-promote-${id}`} onClick={() => setPanel({ kind: 'promote', item })}>Promote to a file</button>
                        {confirmDelete === id
                          ? (
                            <>
                              <button type="button" className="btn btn-primary btn-sm" data-testid={`my-delete-confirm-${id}`} onClick={() => {
                                void client.delete(kind, item.name).then(async () => { setConfirmDelete(null); setPanel(null); await refresh(); setNotice(`Deleted \`${item.name}\` with its history and shares.`) }, (e: unknown) => setNotice(say(e)))
                              }}>Delete for good</button>
                              <button type="button" className="btn btn-sm" onClick={() => setConfirmDelete(null)}>Keep</button>
                            </>
                          )
                          : <button type="button" className="btn btn-sm" data-testid={`my-delete-${id}`} onClick={() => setConfirmDelete(id)}>Delete…</button>}
                      </div>
                    ) : <button type="button" className="btn btn-sm" data-testid={`my-copy-${id}`} onClick={() => void copy(item)}>Make my copy</button>}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
      {panel?.kind === 'share' && <SharePanel key={`share-${panel.item.name}`} kind={kind} item={panel.item} source={source} onChanged={refresh} />}
      {panel?.kind === 'history' && <HistoryPanel key={`history-${panel.item.name}`} kind={kind} item={panel.item} source={source} onRestored={async () => { await refresh() }} />}
      {panel?.kind === 'promote' && <PromotePanel key={`promote-${panel.item.name}`} kind={kind} item={panel.item} source={source} />}
      {kind === 'dashboard' && <PinQueryPanel source={source} items={mine.data ?? []} onPinned={async (r) => { await refresh(); setNotice(`Pinned to \`${r.name}\` as \`${r.widgetId}\`.`) }} keep={keep} />}
    </div>
  )
}

/** Who a document is shared with, and the way to change it: a person (by name), a role (the group), or everyone. Saved as the whole list. */
function SharePanel({ kind, item, source, onChanged }: { kind: ViewKind; item: MineItem; source: ViewsSource; onChanged: () => Promise<unknown> }) {
  const client = personalViewsClient(source.id)
  const shares = useQuery({ queryKey: ['views', 'shares', source.id, kind, item.name], queryFn: () => client.shares(kind, item.name), retry: false })
  const [type, setType] = useState<'user' | 'group' | 'everyone'>('user')
  const [value, setValue] = useState('')
  const [error, setError] = useState<string | null>(null)
  const queryClient = useQueryClient()
  const write = async (next: string[]) => {
    setError(null)
    try {
      const saved = await client.setShares(kind, item.name, next)
      queryClient.setQueryData(['views', 'shares', source.id, kind, item.name], saved)
      await onChanged()
    } catch (e) { setError(say(e)) }
  }
  const current = shares.data ?? []
  const grantee = type === 'everyone' ? '*' : `${type}:${value.trim()}`
  const valid = type === 'everyone' || value.trim() !== ''
  return (
    <div className="card" style={{ marginTop: 12 }} data-testid="my-share-panel">
      <strong>Share {item.name}</strong>
      <p className="hint">Sharing lets people open this {kind}; it gives them none of your data. Each person sees the rows their own access gives.</p>
      {shares.isError && <div className="empty" role="alert" data-testid="my-share-error">{say(shares.error)}</div>}
      <ul data-testid="my-share-list">
        {current.length === 0 && <li className="hint">Shared with nobody.</li>}
        {current.map((g) => (
          <li key={g} data-testid={`my-share-item-${g}`}>{describeGrantee(g)} <button type="button" className="btn btn-sm" data-testid={`my-unshare-${g}`} onClick={() => void write(current.filter((x) => x !== g))}>Stop sharing</button></li>
        ))}
      </ul>
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <select aria-label="Share with" data-testid="my-share-type" value={type} onChange={(e) => setType(e.target.value as typeof type)}>
          <option value="user">A person</option>
          <option value="group">A group (a role)</option>
          <option value="everyone">Everyone who signs in</option>
        </select>
        {type !== 'everyone' && <input aria-label="Name" data-testid="my-share-value" value={value} placeholder={type === 'user' ? 'wendy' : 'analyst'} onChange={(e) => setValue(e.target.value)} />}
        <button type="button" className="btn btn-primary btn-sm" data-testid="my-share-add" disabled={!valid || current.includes(grantee.toLowerCase())} onClick={() => { void write([...current, grantee]); setValue('') }}>Share</button>
      </div>
      {error && <div className="hint" role="alert" data-testid="my-share-failed">{error}</div>}
    </div>
  )
}

/** Every version, newest first; an earlier one can be read and brought back as a new version (the stale case is the module's 409, said in words). */
function HistoryPanel({ kind, item, source, onRestored }: { kind: ViewKind; item: MineItem; source: ViewsSource; onRestored: () => Promise<unknown> }) {
  const client = personalViewsClient(source.id)
  const history = useQuery({ queryKey: ['views', 'history', source.id, kind, item.name], queryFn: () => client.history(kind, item.name), retry: false })
  const queryClient = useQueryClient()
  const [shown, setShown] = useState<{ version: string; text: string } | null>(null)
  const [message, setMessage] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null)
  const current = history.data?.find((v) => v.current)
  const view = async (v: VersionInfo) => {
    try { setShown({ version: v.version, text: await client.historyText(kind, item.name, v.version) }) } catch (e) { setMessage({ tone: 'error', text: say(e) }) }
  }
  const restore = async (v: VersionInfo) => {
    if (!current) return
    try {
      const result = await client.restore(kind, item.name, v.version, current.version)
      setMessage({ tone: 'ok', text: `Version ${v.version} is back as version ${result.version}.` })
      setShown(null)
      await queryClient.invalidateQueries({ queryKey: ['views', 'history', source.id, kind, item.name] })
      await onRestored()
    } catch (e) {
      setMessage({ tone: 'error', text: e instanceof ViewsClientError && e.code === 'version_conflict' ? `${e.message} The history below is what is stored now.` : say(e) })
      await queryClient.invalidateQueries({ queryKey: ['views', 'history', source.id, kind, item.name] })
    }
  }
  return (
    <div className="card" style={{ marginTop: 12 }} data-testid="my-history-panel">
      <strong>History of {item.name}</strong>
      {history.isError && <div className="empty" role="alert" data-testid="my-history-error">{say(history.error)}</div>}
      <table className="table" data-testid="my-history-list">
        <thead><tr><th>Version</th><th>Saved by</th><th>Saved at</th><th /></tr></thead>
        <tbody>
          {(history.data ?? []).map((v) => (
            <tr key={v.version} data-testid={`my-version-${v.version}`} data-current={String(v.current)}>
              <td>{v.version}{v.current ? ' (current)' : ''}</td>
              <td>{v.savedBy}</td>
              <td>{v.savedAt}</td>
              <td>
                <button type="button" className="btn btn-sm" data-testid={`my-version-view-${v.version}`} onClick={() => void view(v)}>Show</button>{' '}
                {!v.current && <button type="button" className="btn btn-sm" data-testid={`my-version-restore-${v.version}`} onClick={() => void restore(v)}>Restore as a new version</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {message && <div className="hint" role={message.tone === 'error' ? 'alert' : 'status'} data-testid="my-history-message" data-tone={message.tone}>{message.text}</div>}
      {shown && <pre data-testid="my-version-text" style={{ maxHeight: 260, overflow: 'auto', fontSize: 12 }}>{shown.text}</pre>}
    </div>
  )
}

/** The document as a file for an app's `views/` folder or a Build project's `dashboards/` (the same loader reads it there): its path and text, and a download. The document stays where it is. */
function PromotePanel({ kind, item, source }: { kind: ViewKind; item: MineItem; source: ViewsSource }) {
  const client = personalViewsClient(source.id)
  const file = useQuery({ queryKey: ['views', 'promote', source.id, kind, item.name, item.version], queryFn: () => client.promote(kind, item.name), retry: false })
  const download = () => {
    if (!file.data) return
    const url = URL.createObjectURL(new Blob([file.data.text], { type: 'application/yaml' }))
    const a = document.createElement('a')
    a.href = url
    a.download = file.data.path.split('/').pop() ?? `${item.name}.yaml`
    a.click()
    URL.revokeObjectURL(url)
  }
  return (
    <div className="card" style={{ marginTop: 12 }} data-testid="my-promote-panel">
      <strong>Promote {item.name} to a file</strong>
      <p className="hint">Put the file in an app&apos;s source or a Build project and publish it as any other {kind}: from then on it is the group&apos;s, not yours. Your own copy stays as it is.</p>
      {file.isError && <div className="empty" role="alert" data-testid="my-promote-error">{say(file.error)}</div>}
      {file.data && (
        <>
          <div className="row" style={{ gap: 8, alignItems: 'center' }}>
            <code data-testid="my-promote-path">{file.data.path}</code>
            <button type="button" className="btn btn-sm" data-testid="my-promote-download" onClick={download}>Download</button>
          </div>
          <pre data-testid="my-promote-text" style={{ maxHeight: 260, overflow: 'auto', fontSize: 12 }}>{file.data.text}</pre>
        </>
      )}
    </div>
  )
}

/** The names of the person's own dashboards, for a pin's target. */
const ownDashboards = (items: MineItem[]) => items.filter((i) => i.own && i.kind === 'dashboard')

function TargetPicker({ items, target, setTarget, newName, setNewName, newTitle, setNewTitle }: {
  items: MineItem[]; target: string; setTarget: (t: string) => void; newName: string; setNewName: (n: string) => void; newTitle: string; setNewTitle: (t: string) => void
}) {
  const own = ownDashboards(items)
  return (
    <>
      <label htmlFor="pin-target">Pin to</label>
      <select id="pin-target" data-testid="pin-target" value={target} onChange={(e) => setTarget(e.target.value)}>
        {own.map((i) => <option key={i.name} value={i.name}>{i.title} ({i.name})</option>)}
        <option value="">A new dashboard…</option>
      </select>
      {target === '' && (
        <>
          <input aria-label="New dashboard name" data-testid="pin-new-name" value={newName} placeholder="my-board" onChange={(e) => setNewName(e.target.value)} />
          <input aria-label="New dashboard title" data-testid="pin-new-title" value={newTitle} placeholder="My board" onChange={(e) => setNewTitle(e.target.value)} />
        </>
      )}
    </>
  )
}

/**
 * Pin to my dashboard, from a rendered dashboard or report (plan 32 DBR12): one of its widgets (a copy of the widget as the document has it, with the parameters it uses) goes into a dashboard of the person's own, a new one or an
 * existing one, in its default layout; what was pinned is read as the person who opens it, so a pinned copy never carries the rows of whoever pinned it.
 */
export function PinPanel({ document, personal }: { document: ViewsDoc; personal: ViewsSource }) {
  const client = personalViewsClient(personal.id)
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const pinnable = Object.entries(document.widgets ?? {}).filter(([, w]) => w.type !== 'image')
  const [widget, setWidget] = useState(pinnable[0]?.[0] ?? '')
  const mine = useQuery({ queryKey: ['views', 'mine', personal.id, 'dashboard'], queryFn: () => client.mine('dashboard'), retry: false, enabled: open })
  const own = ownDashboards(mine.data ?? [])
  const [target, setTarget] = useState<string | null>(null)
  const chosen = target ?? own[0]?.name ?? ''
  const [newName, setNewName] = useState('')
  const [newTitle, setNewTitle] = useState('')
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<PinResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const targetName = chosen === '' ? newName.trim() : chosen
  const ready = widget !== '' && (chosen !== '' || MINE_NAME.test(targetName))
  const go = async () => {
    setBusy(true); setError(null); setResult(null)
    try {
      const w = document.widgets[widget] as WidgetDoc
      const pinned = await pinWidget(client, { target: targetName, title: newTitle, widget: w, id: widget, parameters: document.parameters })
      setResult(pinned)
      setTarget(pinned.name)   // the next pin goes to the same dashboard unless another is chosen
      await queryClient.invalidateQueries({ queryKey: ['views'] })
    } catch (e) { setError(say(e)) } finally { setBusy(false) }
  }
  return (
    <span data-testid="pin" style={{ display: 'contents' }}>
      <button type="button" className="btn btn-sm" data-testid="pin-open" aria-expanded={open} disabled={pinnable.length === 0} title="Keep one of these widgets on a dashboard of your own" onClick={() => setOpen(!open)}>Pin to my dashboard</button>
      {open && (
        <div className="card" style={{ flexBasis: '100%' }} data-testid="pin-panel">
          <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <label htmlFor="pin-widget">Widget</label>
            <select id="pin-widget" data-testid="pin-widget" value={widget} onChange={(e) => setWidget(e.target.value)}>
              {pinnable.map(([id, w]) => <option key={id} value={id}>{(w as { title?: string; label?: string }).title ?? (w as { label?: string }).label ?? id} ({w.type})</option>)}
            </select>
            <TargetPicker items={mine.data ?? []} target={chosen} setTarget={setTarget} newName={newName} setNewName={setNewName} newTitle={newTitle} setNewTitle={setNewTitle} />
            <button type="button" className="btn btn-primary btn-sm" data-testid="pin-go" disabled={busy || !ready} onClick={() => void go()}>Pin</button>
          </div>
          {mine.isError && <div className="hint" role="alert" data-testid="pin-error">{say(mine.error)}</div>}
          {error && <div className="hint" role="alert" data-testid="pin-error">{error}</div>}
          {result && (
            <div className="hint" role="status" data-testid="pin-done" data-name={result.name} data-widget={result.widgetId}>
              Pinned as <code>{result.widgetId}</code> to {result.created ? 'the new dashboard ' : ''}<code>{result.name}</code> (version {result.version}).{' '}
              <Link to={withSource(`/views/dashboards/${encodeURIComponent(result.name)}`, `${personal.id}`)} data-testid="pin-open-board">Open it</Link>
            </div>
          )}
        </div>
      )}
    </span>
  )
}

/**
 * Pin a query result (plan 32 DBR12): a semantic request, kept as a widget of one of your dashboards (written into the widget as it stands, so the widget runs exactly that request, as whoever opens it). The console has no screen that runs a
 * request yet (J14 and J28 are the serving layer's), so the request is the one a person pastes or an agent hands over; the widget is a table, a chart or a metric of its outputs, and the dashboard's editor takes it from there.
 */
function PinQueryPanel({ source, items, onPinned, keep }: { source: ViewsSource; items: MineItem[]; onPinned: (r: PinResult) => Promise<unknown> | void; keep: string | null }) {
  const client = personalViewsClient(source.id)
  const [open, setOpen] = useState(false)
  const [model, setModel] = useState('')
  const [request, setRequest] = useState('')
  const [shape, setShape] = useState<QueryShape>('table')
  const [title, setTitle] = useState('')
  const own = ownDashboards(items)
  const [target, setTarget] = useState<string | null>(null)
  const chosen = target ?? own[0]?.name ?? ''
  const [newName, setNewName] = useState('')
  const [newTitle, setNewTitle] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState<PinResult | null>(null)
  const targetName = chosen === '' ? newName.trim() : chosen
  const go = async () => {
    setError(null); setDone(null)
    try {
      const widget = widgetFromRequest({ model, requestText: request, shape, title })
      const pinned = await pinWidget(client, { target: targetName, title: newTitle, widget, id: 'query' })
      setDone(pinned)
      await onPinned(pinned)
    } catch (e) { setError(say(e)) }
  }
  return (
    <div className="card" style={{ marginTop: 16 }} data-testid="pin-query">
      <button type="button" className="btn btn-sm" data-testid="pin-query-open" aria-expanded={open} onClick={() => setOpen(!open)}>Pin a query result</button>
      {open && (
        <div style={{ marginTop: 8 }}>
          <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <label htmlFor="pin-query-model">Model</label>
            <input id="pin-query-model" data-testid="pin-query-model" value={model} placeholder="sales_catalog" onChange={(e) => setModel(e.target.value)} />
            <label htmlFor="pin-query-shape">Show as</label>
            <select id="pin-query-shape" data-testid="pin-query-shape" value={shape} onChange={(e) => setShape(e.target.value as QueryShape)}>
              <option value="table">A table</option>
              <option value="chart">A bar chart</option>
              <option value="metric">A number</option>
            </select>
            <input aria-label="Widget title" data-testid="pin-query-title" value={title} placeholder="Revenue by region" onChange={(e) => setTitle(e.target.value)} />
          </div>
          <label htmlFor="pin-query-request">Semantic request (JSON)</label>
          <textarea id="pin-query-request" data-testid="pin-query-request" rows={6} spellCheck={false} style={{ width: '100%', fontFamily: 'var(--mono)', fontSize: 12 }} value={request}
            placeholder={'{"measures": ["orders.net_revenue"], "attributes": ["customers.region"], "limit": 100}'} onChange={(e) => setRequest(e.target.value)} />
          <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <TargetPicker items={items} target={chosen} setTarget={setTarget} newName={newName} setNewName={setNewName} newTitle={newTitle} setNewTitle={setNewTitle} />
            <button type="button" className="btn btn-primary btn-sm" data-testid="pin-query-go" disabled={model.trim() === '' || request.trim() === '' || (chosen === '' && !MINE_NAME.test(targetName))} onClick={() => void go()}>Pin</button>
          </div>
          {error && <div className="hint" role="alert" data-testid="pin-query-error">{error}</div>}
          {done && <div className="hint" role="status" data-testid="pin-query-done">Pinned as <code>{done.widgetId}</code> to <code>{done.name}</code> (version {done.version}). <Link to={withSource(`/views/dashboards/${encodeURIComponent(done.name)}`, keep ?? source.id)}>Open it</Link></div>}
        </div>
      )}
    </div>
  )
}
