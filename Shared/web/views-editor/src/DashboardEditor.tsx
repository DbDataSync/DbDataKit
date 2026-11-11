import { useEffect, useRef, useState, type ReactNode } from 'react'
import { flushSync } from 'react-dom'
import { CodeEditor, Link } from '@dbdatakit/shell'
import {
  NAME_PATTERN, canRedo, canUndo, draftFromOpened, edit, editText, errorsOf, format, formTarget, diagnosticsFor, isDirty, markersOf, newDocumentText, newDraft, redo, reloadDraft, saveDraft, undo,
  widgetModel, type Catalog, type ClientDiagnostic, type Draft, type DocumentStore, type ViewsClient, type RenderedWidget, type EditCommand, type TileDoc, type ViewKind, type WidgetDoc, type WidgetType, type LayoutDoc,
} from '@dbdatakit/visuals'
import { newWidget, nextTile } from './editing'
import { GridEditor, LayoutSettings, SelectorEditor, SelectorTester } from './LayoutEditors'
import { DocumentSettings } from './DocumentSettings'
import { FlowEditor } from './FlowEditor'
import { WidgetEditor } from './WidgetEditor'

/**
 * Where an editor is hosted (plan 32 DBR24 and DBR29): the console's Views page over a source, and Build's Views tab over a workspace's files, use the same screen and say here what differs. `client` answers what the editor asks of the server
 * (the server's check of a text, a widget's preview, the model's catalog); `store` is where a document is opened and saved; the links say where the screen's own links lead and `go` moves there.
 */
export interface EditorHost {
  client: ViewsClient
  store: DocumentStore
  /** The list of documents of a kind (the link back from a new or missing document). */
  listHref: (kind: ViewKind) => string
  /** The page that draws a saved document, or null when the host has none (Build draws no document: the preview pane does). */
  viewHref: (kind: ViewKind, name: string) => string | null
  /** The editor on a saved document. */
  editHref: (kind: ViewKind, name: string) => string
  go: (href: string, options?: { replace?: boolean }) => void
  /** Something was saved, duplicated or deleted: a host refreshes its lists. */
  changed?: () => void | Promise<void>
  /** What a file is called to the person ("sales-today.yaml", "dashboards/sales-today.yaml"). */
  fileLabel?: (kind: ViewKind, name: string) => string
  /** What the Delete question says when deleting does not erase the file (a published document is retired: its versions stay). */
  deleteQuestion?: (kind: ViewKind, name: string) => string
  /** The model a new widget asks of when the document has none yet. */
  defaultModel?: string
  /** Saving, duplicating and deleting are refused for this person, and why (the server refuses them as well). */
  readOnly?: string
  /** What a host adds under the toolbar (Build's publish bar), given the state of the draft. */
  extra?: (state: { kind: ViewKind; name: string; saved: boolean; dirty: boolean; valid: boolean }) => ReactNode
}

const PALETTE: WidgetType[] = ['metric', 'chart', 'matrix', 'table', 'text', 'image', 'control', 'embed']
const where = (d: ClientDiagnostic) => (d.line !== undefined ? `line ${d.line}${d.column !== undefined ? `:${d.column}` : ''}` : d.path)

function Problems({ diagnostics, testId }: { diagnostics: ClientDiagnostic[]; testId: string }) {
  if (diagnostics.length === 0) return null
  return (
    <ul className="hint" data-testid={testId} style={{ margin: '4px 0' }}>
      {diagnostics.map((d, i) => <li key={i} role={d.severity === 'error' ? 'alert' : undefined}><code>{where(d)}</code> {d.code} {d.message}</li>)}
    </ul>
  )
}

type Message = { tone: 'ok' | 'error'; text: string } | null

/**
 * The editor of the Views page (plan 32 DBR24): one dashboard or report as a draft (text and document kept in step, undo and redo), the validator's answer on every change shown beside the part it is about (the widget,
 * the layout, the tile) and in the YAML view as markers, save with the version the file was opened at (a stale save is a conflict that offers a reload), duplicate and delete. A form change and a YAML change are the same text.
 * The report's sections, blocks and page setup are `FlowEditor` (DBR27). Not yet: a live preview of the unsaved draft on the server (DBR28).
 */
export function DashboardEditor({ kind, name, host }: { kind: ViewKind; name: string | null; host: EditorHost }) {
  const { store, client } = host
  const fileOf = host.fileLabel ?? ((_: ViewKind, n: string) => `${n}.yaml`)
  const [draft, setDraftState] = useState<Draft | null>(null)
  // the draft as the last change left it: a drag sends a change per pointer move, and each must start from the one before it, rendered or not
  const latest = useRef<Draft | null>(null)
  const setDraft = (next: Draft | null | ((current: Draft | null) => Draft | null)) => {
    latest.current = typeof next === 'function' ? next(latest.current) : next
    setDraftState(latest.current)
  }
  const [loadError, setLoadError] = useState<string | null>(null)
  const [tab, setTab] = useState<'form' | 'yaml'>('form')
  const [message, setMessage] = useState<Message>(null)
  const [conflict, setConflict] = useState<{ message: string; storedVersion: string | undefined } | null>(null)
  const [fileName, setFileName] = useState('')
  const [duplicateAs, setDuplicateAs] = useState('')
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [busy, setBusy] = useState(false)
  const [layoutName, setLayoutName] = useState('')
  const [renaming, setRenaming] = useState<Record<string, string>>({})
  const [selected, setSelected] = useState<string | null>(null)
  const [catalog, setCatalog] = useState<{ model: string; catalog: Catalog | null; note: string | null } | null>(null)
  const [server, setServer] = useState<ClientDiagnostic[]>([])

  useEffect(() => {
    let cancelled = false
    setDraft(null)
    setLoadError(null)
    if (name === null) { setDraft(newDraft(kind, '', newDocumentText(kind, kind === 'dashboard' ? 'New dashboard' : 'New report'))); return }
    store.open(kind, name).then((opened) => { if (!cancelled) setDraft(draftFromOpened(opened)) }, (e: unknown) => { if (!cancelled) setLoadError(e instanceof Error ? e.message : String(e)) })
    return () => { cancelled = true }
  }, [kind, name, store])

  // the catalog of the selected widget's model (the wells' fields), and the source's check of the text (what the browser cannot check: filters, order, totals), a moment after each change
  const selectedWidget = selected && draft?.value ? draft.value.widgets?.[selected] : undefined
  const selectedModel = selectedWidget ? widgetModel(selectedWidget, draft!.value) : undefined
  useEffect(() => {
    if (!selectedModel) { setCatalog(null); return }
    let cancelled = false
    if (!client.catalog) { setCatalog({ model: selectedModel, catalog: null, note: 'This source has no catalog: type field names into the wells.' }); return }
    client.catalog(selectedModel).then(
      (c) => { if (!cancelled) setCatalog({ model: selectedModel, catalog: c, note: null }) },
      (e: unknown) => { if (!cancelled) setCatalog({ model: selectedModel, catalog: null, note: e instanceof Error ? e.message : String(e) }) })
    return () => { cancelled = true }
  }, [selectedModel, client])
  const draftText = draft?.text
  useEffect(() => {
    if (draftText === undefined || !selected) { setServer([]); return }
    let cancelled = false
    const timer = setTimeout(() => { client.validate(draftText, { kind }).then((r) => { if (!cancelled) setServer(r.diagnostics) }, () => { if (!cancelled) setServer([]) }) }, 200)
    return () => { cancelled = true; clearTimeout(timer) }
  }, [draftText, kind, selected, client])

  if (loadError) return <div className="empty" data-testid="ed-error" role="alert">{loadError} <Link to={host.listHref(kind)}>All {kind}s</Link></div>
  if (!draft) return <div className="empty" data-testid="ed-loading" role="status">Opening the editor…</div>

  const saved = name !== null || draft.baseVersion !== undefined
  const doc = draft.value
  const errors = errorsOf(draft)
  const dirty = isDirty(draft)
  const apply = (command: EditCommand, group?: string) => {
    const result = edit(latest.current ?? draft, command, group)
    setDraft(result.draft)
    setMessage(result.error ? { tone: 'error', text: result.error.message } : null)
  }
  const say = (text: string | null) => setMessage(text === null ? null : { tone: 'error', text })
  const refresh = async () => { await host.changed?.() }

  const save = async () => {
    const target = saved ? draft : { ...draft, name: fileName.trim() }
    if (!saved && !NAME_PATTERN.test(target.name)) { setMessage({ tone: 'error', text: 'Give the file a name: a letter, then letters, digits, `_` or `-`.' }); return }
    setBusy(true)
    const outcome = await saveDraft(store, target)
    setBusy(false)
    setDraft(outcome.draft)
    if (outcome.status === 'saved') {
      setConflict(null)
      setMessage({ tone: 'ok', text: `Saved ${outcome.draft.name} (version ${outcome.draft.baseVersion}).` })
      await refresh()
      if (!saved) host.go(host.editHref(kind, outcome.draft.name), { replace: true })
    } else if (outcome.status === 'conflict') {
      setConflict({ message: outcome.message, storedVersion: outcome.storedVersion })
      setMessage({ tone: 'error', text: outcome.message })
    } else setMessage({ tone: 'error', text: outcome.message })
  }

  const reload = async () => {
    setDraft(await reloadDraft(store, draft))
    setConflict(null)
    setMessage({ tone: 'ok', text: 'Reloaded what is stored; your changes were given up.' })
  }

  const layouts: LayoutDoc[] = doc?.layouts ?? []
  const widgets = doc?.widgets ?? {}

  return (
    <div data-testid="ed-root" style={{ overflow: 'auto', flex: 1, minHeight: 0 }}>
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }} data-testid="ed-toolbar">
        {(() => { const view = saved ? host.viewHref(kind, draft.name) : null; return <Link to={view ?? host.listHref(kind)} data-testid="ed-back">{view ? 'View' : `All ${kind}s`}</Link> })()}
        {saved ? <strong data-testid="ed-name">{fileOf(kind, draft.name)}</strong> : (
          <>
            <label htmlFor="ed-file-name">File name</label>
            <input id="ed-file-name" data-testid="ed-file-name" value={fileName} placeholder="sales-today" onChange={(e) => setFileName(e.target.value)} />
          </>
        )}
        <label htmlFor="ed-title">Title</label>
        <input id="ed-title" data-testid="ed-title" value={typeof doc?.title === 'string' ? doc.title : ''} disabled={!doc} onChange={(e) => apply({ type: 'setTitle', title: e.target.value }, 'title')} />
        <button type="button" className="btn btn-sm" data-testid="ed-undo" disabled={!canUndo(draft)} onClick={() => setDraft(undo(draft))}>Undo</button>
        <button type="button" className="btn btn-sm" data-testid="ed-redo" disabled={!canRedo(draft)} onClick={() => setDraft(redo(draft))}>Redo</button>
        <button type="button" className="btn btn-sm" data-testid="ed-format" disabled={!doc} title="Canonical key order and style; comments are dropped" onClick={() => { const r = format(draft); setDraft(r.draft); setMessage(r.error ? { tone: 'error', text: r.error.message } : null) }}>Format</button>
        <button type="button" className="btn btn-primary" data-testid="ed-save" disabled={busy || (saved && !dirty) || host.readOnly !== undefined} title={host.readOnly} onClick={() => void save()}>Save</button>
        <span className="hint" data-testid="ed-dirty" data-dirty={String(dirty)}>{dirty ? 'Unsaved changes' : 'Saved'}</span>
        <span className="hint" data-testid="ed-validity" data-errors={errors.length}>{errors.length === 0 ? 'Valid' : `${errors.length} problem${errors.length === 1 ? '' : 's'}`}</span>
      </div>
      {host.readOnly !== undefined && <div className="hint" data-testid="ed-readonly" role="note" style={{ margin: '6px 0' }}>{host.readOnly}</div>}
      {host.extra?.({ kind, name: draft.name, saved, dirty, valid: errors.length === 0 })}
      {message && <div className="hint" data-testid="ed-status" data-tone={message.tone} role={message.tone === 'error' ? 'alert' : 'status'} style={{ margin: '6px 0' }}>{message.text}</div>}
      {conflict && (
        <div className="empty" data-testid="ed-conflict" role="alert">
          <strong>This {kind} was changed elsewhere.</strong> The stored version is {conflict.storedVersion ?? 'unknown'}; yours was opened at {draft.baseVersion ?? 'none'}.
          <div className="row" style={{ gap: 8, marginTop: 6 }}>
            <button type="button" className="btn" data-testid="ed-conflict-reload" onClick={() => void reload()}>Reload what is stored</button>
            <button type="button" className="btn" data-testid="ed-conflict-keep" onClick={() => setConflict(null)}>Keep editing</button>
          </div>
        </div>
      )}
      <Problems diagnostics={draft.validation.diagnostics} testId="ed-problems" />

      <div className="row" style={{ gap: 8, margin: '8px 0' }} role="tablist">
        <button type="button" role="tab" aria-selected={tab === 'form'} className={tab === 'form' ? 'btn btn-primary' : 'btn'} data-testid="ed-tab-form" onClick={() => setTab('form')}>Form</button>
        <button type="button" role="tab" aria-selected={tab === 'yaml'} className={tab === 'yaml' ? 'btn btn-primary' : 'btn'} data-testid="ed-tab-yaml" onClick={() => setTab('yaml')}>YAML</button>
      </div>

      {tab === 'yaml' && (
        <CodeEditor value={draft.text} language="yaml" testId="ed-yaml" minLines={20} maxLines={40} diagnostics={markersOf(draft.validation.diagnostics)} 
          // flushed at once: the editor writes its own text back when the value it is given differs from what it holds, so a keystroke that is rendered late would be undone by the one before it
          onChange={(text) => flushSync(() => setDraft((current) => (current ? editText(current, text) : current)))} />
      )}

      {tab === 'form' && !doc && <div className="empty" data-testid="ed-form-unavailable" role="note">The text is not a document yet, so the form cannot show it. Fix it in the YAML view.</div>}
      {tab === 'form' && doc && (
        <>
          <div className="card" style={{ marginBottom: 12 }} data-testid="ed-widgets">
            <strong>Widgets</strong>
            <table className="table">
              <tbody>
                {Object.entries(widgets).map(([id, widget]) => (
                  <tr key={id} data-testid={`ed-widget-${id}`}>
                    <td><code>{id}</code></td>
                    <td>{widget.type}</td>
                    <td>{(widget as { title?: string; label?: string }).title ?? (widget as { label?: string }).label ?? ''}</td>
                    <td>
                      <Problems diagnostics={diagnosticsFor(draft.validation.diagnostics, { area: 'widget', id })} testId={`ed-widget-problems-${id}`} />
                      <button type="button" className="btn btn-sm" data-testid={`ed-widget-edit-${id}`} aria-pressed={selected === id} onClick={() => setSelected(selected === id ? null : id)}>{selected === id ? 'Close' : 'Fields'}</button>{' '}
                      <button type="button" className="btn btn-sm" data-testid={`ed-widget-remove-${id}`} onClick={() => apply({ type: 'removeWidget', id })}>Remove</button>
                    </td>
                  </tr>
                ))}
                {Object.keys(widgets).length === 0 && <tr><td className="hint">No widgets yet.</td></tr>}
              </tbody>
            </table>
            <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }} data-testid="ed-palette">
              <span className="hint">Add:</span>
              {PALETTE.filter((type) => !(type === 'embed' && doc.kind === 'report')).map((type) => (
                <button key={type} type="button" className="btn btn-sm" data-testid={`ed-add-${type}`} onClick={() => {
                  let id: string = type, n = 1
                  while (widgets[id]) id = `${type}${++n}`
                  let widget: WidgetDoc
                  try { widget = newWidget(type, doc, host.defaultModel) } catch (e) { say(e instanceof Error ? e.message : String(e)); return }
                  apply({ type: 'addWidget', id, widget })
                  setSelected(id)
                }}>{type}</button>
              ))}
            </div>
          </div>

          {selected && widgets[selected] && (
            <WidgetEditor id={selected} widget={widgets[selected]!} doc={doc} catalog={catalog?.catalog ?? null} catalogNote={catalog?.note ?? null} apply={apply} diagnostics={draft.validation.diagnostics} server={server}
              preview={(widget) => client.preview({ widget, widgetId: selected, kind, draft: doc, document: draft.name || undefined }) as Promise<RenderedWidget>} />
          )}

          <DocumentSettings doc={doc} diagnostics={draft.validation.diagnostics} apply={apply} />

          <div className="card" data-testid="ed-layouts">
            <strong>Layouts</strong>
            <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap', margin: '6px 0' }} data-testid="ed-layout-add">
              <input aria-label="New layout name" data-testid="ed-layout-new-name" placeholder="phone" value={layoutName} onChange={(e) => setLayoutName(e.target.value)} />
              <button type="button" className="btn btn-sm" data-testid="ed-layout-add-reflow" disabled={!NAME_PATTERN.test(layoutName)} onClick={() => { apply({ type: 'addLayout', layout: { name: layoutName, type: 'reflow', grid: { columns: 12 }, tiles: [] } }); setLayoutName('') }}>Add reflow layout</button>
              <button type="button" className="btn btn-sm" data-testid="ed-layout-add-flow" disabled={!NAME_PATTERN.test(layoutName)} onClick={() => { apply({ type: 'addLayout', layout: { name: layoutName, type: 'flow', page: { size: 'A4', orientation: 'portrait' }, sections: [{ blocks: [] }] } as LayoutDoc }); setLayoutName('') }}>Add flow layout</button>
              <button type="button" className="btn btn-sm" data-testid="ed-layout-add-canvas" disabled={!NAME_PATTERN.test(layoutName)} onClick={() => { apply({ type: 'addLayout', layout: { name: layoutName, type: 'canvas', canvas: { width: 390, height: 900 }, tiles: [] } }); setLayoutName('') }}>Add canvas layout</button>
            </div>
            <SelectorTester doc={doc} />
            {layouts.map((layout, index) => {
              const own = diagnosticsFor(draft.validation.diagnostics, { area: 'layout', index })
              const tiles = (layout as { tiles?: TileDoc[] }).tiles
              const placed = new Set((tiles ?? []).map((t) => t.widget))
              return (
                <div key={layout.name} data-testid={`ed-layout-${layout.name}`} style={{ borderTop: '1px solid var(--border, #ddd)', padding: '8px 0' }}>
                  <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
                    <strong>{layout.name}</strong> <span className="hint">{layout.type}</span>
                    <label><input type="radio" name="ed-default" data-testid={`ed-layout-default-${layout.name}`} checked={layout.default === true} onChange={() => apply({ type: 'setDefaultLayout', name: layout.name })} /> default</label>
                    <input aria-label={`Rename ${layout.name}`} data-testid={`ed-layout-rename-${layout.name}`} style={{ width: 110 }} placeholder="rename" value={renaming[layout.name] ?? ''}
                      onChange={(e) => setRenaming({ ...renaming, [layout.name]: e.target.value })}
                      onKeyDown={(e) => { if (e.key === 'Enter' && NAME_PATTERN.test(renaming[layout.name] ?? '')) { apply({ type: 'renameLayout', name: layout.name, to: renaming[layout.name]! }); setRenaming({}) } }} />
                    <button type="button" className="btn btn-sm" data-testid={`ed-layout-rename-go-${layout.name}`} disabled={!NAME_PATTERN.test(renaming[layout.name] ?? '')} onClick={() => { apply({ type: 'renameLayout', name: layout.name, to: renaming[layout.name]! }); setRenaming({}) }}>Rename</button>
                    <button type="button" className="btn btn-sm" data-testid={`ed-layout-up-${layout.name}`} disabled={index === 0} onClick={() => apply({ type: 'moveLayout', name: layout.name, to: index - 1 })}>Up</button>
                    <button type="button" className="btn btn-sm" data-testid={`ed-layout-down-${layout.name}`} disabled={index === layouts.length - 1} onClick={() => apply({ type: 'moveLayout', name: layout.name, to: index + 1 })}>Down</button>
                    <button type="button" className="btn btn-sm" data-testid={`ed-layout-duplicate-${layout.name}`} onClick={() => { let as = `${layout.name}-copy`, n = 1; while (layouts.some((l) => l.name === as)) as = `${layout.name}-copy${++n}`; apply({ type: 'duplicateLayout', name: layout.name, as }) }}>Duplicate</button>
                    <button type="button" className="btn btn-sm" data-testid={`ed-layout-delete-${layout.name}`} onClick={() => apply({ type: 'deleteLayout', name: layout.name })}>Delete</button>
                  </div>
                  <Problems diagnostics={own.filter((d) => formTarget(d.path).area === 'layout' && (formTarget(d.path) as { tile?: number }).tile === undefined)} testId={`ed-layout-problems-${layout.name}`} />
                  <SelectorEditor layout={layout} apply={apply} />
                  {layout.type === 'flow' && (
                    <FlowEditor layout={layout} index={index} doc={doc} diagnostics={draft.validation.diagnostics} apply={apply} name={draft.name} text={draft.text} client={client} kind={kind}
                      printBlocked={!saved ? 'Save the report first: the print preview is the exporter\'s PDF of the saved report.' : dirty ? 'The print preview is the exporter\'s PDF of the saved report, and this one has unsaved changes: save it first.' : undefined}
                      preview={(widget, widgetId) => client.preview({ widget, widgetId, kind, draft: doc, document: draft.name || undefined })}
                      previewDocument={client.previewDocument ? (request) => client.previewDocument!({ kind, ...request }) : undefined} />
                  )}
                  {layout.type !== 'flow' && <LayoutSettings layout={layout} index={index} apply={apply} />}
                  {layout.type !== 'flow' && <GridEditor layout={layout} widgets={widgets} apply={apply} say={say} />}
                  {tiles && (
                    <table className="table" data-testid={`ed-tiles-${layout.name}`}>
                      <thead><tr><th>Tile</th><th>x</th><th>y</th><th>w</th><th>h</th>{layout.type === 'canvas' && <th>layer</th>}<th /></tr></thead>
                      <tbody>
                        {tiles.map((tile, ti) => (
                          <tr key={tile.widget} data-testid={`ed-tile-${layout.name}-${tile.widget}`}>
                            <td><code>{tile.widget}</code></td>
                            {(['x', 'y', 'w', 'h'] as const).map((k) => (
                              <td key={k}>
                                <input type="number" aria-label={`${tile.widget} ${k}`} data-testid={`ed-tile-${layout.name}-${tile.widget}-${k}`} style={{ width: 70 }} value={tile[k]}
                                  onChange={(e) => { if (e.target.value !== '') apply({ type: 'setTile', layout: layout.name, widget: tile.widget, changes: { [k]: Number(e.target.value) } }, `tile:${layout.name}:${tile.widget}:${k}`) }} />
                              </td>
                            ))}
                            {layout.type === 'canvas' && (
                              <td><input type="number" aria-label={`${tile.widget} layer`} data-testid={`ed-tile-${layout.name}-${tile.widget}-layer`} style={{ width: 60 }} value={tile.layer ?? ''}
                                onChange={(e) => apply({ type: 'setTile', layout: layout.name, widget: tile.widget, changes: { layer: e.target.value === '' ? undefined : Number(e.target.value) } }, `tile:${layout.name}:${tile.widget}:layer`)} /></td>
                            )}
                            <td>
                              <Problems diagnostics={diagnosticsFor(draft.validation.diagnostics, { area: 'layout', index, tile: ti })} testId={`ed-tile-problems-${layout.name}-${tile.widget}`} />
                              <button type="button" className="btn btn-sm" data-testid={`ed-tile-remove-${layout.name}-${tile.widget}`} onClick={() => apply({ type: 'removeTile', layout: layout.name, widget: tile.widget })}>Remove</button>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  )}
                  {tiles && Object.keys(widgets).filter((id) => !placed.has(id)).length > 0 && (
                    <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
                      <span className="hint">Not placed here:</span>
                      {Object.keys(widgets).filter((id) => !placed.has(id)).map((id) => (
                        <button key={id} type="button" className="btn btn-sm" data-testid={`ed-place-${layout.name}-${id}`} onClick={() => apply({ type: 'placeTile', layout: layout.name, tile: nextTile(layout, widgets[id]!, id) })}>Place {id}</button>
                      ))}
                    </div>
                  )}
                </div>
              )
            })}
          </div>
        </>
      )}

      {saved && (
        <div className="card" style={{ marginTop: 12 }} data-testid="ed-file-actions">
          <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <label htmlFor="ed-duplicate-name">Duplicate as</label>
            <input id="ed-duplicate-name" data-testid="ed-duplicate-name" value={duplicateAs} placeholder={`${draft.name}-copy`} onChange={(e) => setDuplicateAs(e.target.value)} />
            <button type="button" className="btn" data-testid="ed-duplicate" disabled={!NAME_PATTERN.test(duplicateAs) || host.readOnly !== undefined} title={host.readOnly} onClick={() => {
              void store.duplicate(kind, draft.name, duplicateAs).then(async () => { await refresh(); host.go(host.editHref(kind, duplicateAs)) }, (e: unknown) => setMessage({ tone: 'error', text: e instanceof Error ? e.message : String(e) }))
            }}>Duplicate</button>
            {!confirmDelete
              ? <button type="button" className="btn" data-testid="ed-delete" disabled={host.readOnly !== undefined} title={host.readOnly} onClick={() => setConfirmDelete(true)}>Delete…</button>
              : (
                <>
                  <span className="hint">{host.deleteQuestion ? host.deleteQuestion(kind, draft.name) : `Delete ${fileOf(kind, draft.name)} for good?`}</span>
                  <button type="button" className="btn btn-primary" data-testid="ed-delete-confirm" onClick={() => {
                    void store.delete(kind, draft.name).then(async () => { await refresh(); host.go(host.listHref(kind)) }, (e: unknown) => setMessage({ tone: 'error', text: e instanceof Error ? e.message : String(e) }))
                  }}>Delete</button>
                  <button type="button" className="btn" data-testid="ed-delete-cancel" onClick={() => setConfirmDelete(false)}>Keep</button>
                </>
              )}
          </div>
        </div>
      )}
    </div>
  )
}
