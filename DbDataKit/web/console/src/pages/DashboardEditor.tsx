import { useEffect, useMemo, useState } from 'react'
import { flushSync } from 'react-dom'
import { Link, useNavigate } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { CodeEditor } from '@dbdatakit/shell'
import {
  NAME_PATTERN, canRedo, canUndo, clientStore, draftFromOpened, edit, editText, errorsOf, format, formTarget, diagnosticsFor, isDirty, markersOf, newDocumentText, newDraft, redo, reloadDraft, saveDraft, undo,
  type ClientDiagnostic, type Draft, type EditCommand, type TileDoc, type ViewKind, type WidgetDoc, type WidgetType, type LayoutDoc,
} from '@dbdatakit/visuals'
import { viewsClient } from '../viewsSource'
import { newWidget, nextTile } from '../viewsEditing'

const AREA: Record<ViewKind, string> = { dashboard: '/views/dashboards', report: '/views/reports' }
const PALETTE: WidgetType[] = ['metric', 'chart', 'table', 'text']
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
 * What it does not do yet: field wells and the derived request (DBR25), dragging on the grid and the canvas, selectors and parameters (DBR26), the report's sections (DBR27), a live preview of the unsaved draft (DBR28).
 */
export function DashboardEditor({ kind, name }: { kind: ViewKind; name: string | null }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const store = useMemo(() => clientStore(viewsClient()), [])
  const [draft, setDraft] = useState<Draft | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [tab, setTab] = useState<'form' | 'yaml'>('form')
  const [message, setMessage] = useState<Message>(null)
  const [conflict, setConflict] = useState<{ message: string; storedVersion: string | undefined } | null>(null)
  const [fileName, setFileName] = useState('')
  const [duplicateAs, setDuplicateAs] = useState('')
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let cancelled = false
    setDraft(null)
    setLoadError(null)
    if (name === null) { setDraft(newDraft(kind, '', newDocumentText(kind, kind === 'dashboard' ? 'New dashboard' : 'New report'))); return }
    store.open(kind, name).then((opened) => { if (!cancelled) setDraft(draftFromOpened(opened)) }, (e: unknown) => { if (!cancelled) setLoadError(e instanceof Error ? e.message : String(e)) })
    return () => { cancelled = true }
  }, [kind, name, store])

  if (loadError) return <div className="empty" data-testid="ed-error" role="alert">{loadError} <Link to={AREA[kind]}>All {kind}s</Link></div>
  if (!draft) return <div className="empty" data-testid="ed-loading" role="status">Opening the editor…</div>

  const saved = name !== null || draft.baseVersion !== undefined
  const doc = draft.value
  const errors = errorsOf(draft)
  const dirty = isDirty(draft)
  const apply = (command: EditCommand, group?: string) => {
    const result = edit(draft, command, group)
    setDraft(result.draft)
    setMessage(result.error ? { tone: 'error', text: result.error.message } : null)
  }
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['views'] })

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
      if (!saved) navigate(`${AREA[kind]}/${encodeURIComponent(outcome.draft.name)}?edit=1`, { replace: true })
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
        <Link to={saved ? `${AREA[kind]}/${encodeURIComponent(draft.name)}` : AREA[kind]} data-testid="ed-back">{saved ? 'View' : `All ${kind}s`}</Link>
        {saved ? <strong data-testid="ed-name">{draft.name}.yaml</strong> : (
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
        <button type="button" className="btn btn-primary" data-testid="ed-save" disabled={busy || (saved && !dirty)} onClick={() => void save()}>Save</button>
        <span className="hint" data-testid="ed-dirty" data-dirty={String(dirty)}>{dirty ? 'Unsaved changes' : 'Saved'}</span>
        <span className="hint" data-testid="ed-validity" data-errors={errors.length}>{errors.length === 0 ? 'Valid' : `${errors.length} problem${errors.length === 1 ? '' : 's'}`}</span>
      </div>
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
                      <button type="button" className="btn btn-sm" data-testid={`ed-widget-remove-${id}`} onClick={() => apply({ type: 'removeWidget', id })}>Remove</button>
                    </td>
                  </tr>
                ))}
                {Object.keys(widgets).length === 0 && <tr><td className="hint">No widgets yet.</td></tr>}
              </tbody>
            </table>
            <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }} data-testid="ed-palette">
              <span className="hint">Add:</span>
              {PALETTE.map((type) => (
                <button key={type} type="button" className="btn btn-sm" data-testid={`ed-add-${type}`} onClick={() => {
                  let id: string = type, n = 1
                  while (widgets[id]) id = `${type}${++n}`
                  const widget: WidgetDoc = newWidget(type, doc)
                  apply({ type: 'addWidget', id, widget })
                }}>{type}</button>
              ))}
            </div>
          </div>

          <div className="card" data-testid="ed-layouts">
            <strong>Layouts</strong>
            {layouts.map((layout, index) => {
              const own = diagnosticsFor(draft.validation.diagnostics, { area: 'layout', index })
              const tiles = (layout as { tiles?: TileDoc[] }).tiles
              const placed = new Set((tiles ?? []).map((t) => t.widget))
              return (
                <div key={layout.name} data-testid={`ed-layout-${layout.name}`} style={{ borderTop: '1px solid var(--border, #ddd)', padding: '8px 0' }}>
                  <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
                    <strong>{layout.name}</strong> <span className="hint">{layout.type}</span>
                    <label><input type="radio" name="ed-default" data-testid={`ed-layout-default-${layout.name}`} checked={layout.default === true} onChange={() => apply({ type: 'setDefaultLayout', name: layout.name })} /> default</label>
                    <button type="button" className="btn btn-sm" data-testid={`ed-layout-up-${layout.name}`} disabled={index === 0} onClick={() => apply({ type: 'moveLayout', name: layout.name, to: index - 1 })}>Up</button>
                    <button type="button" className="btn btn-sm" data-testid={`ed-layout-down-${layout.name}`} disabled={index === layouts.length - 1} onClick={() => apply({ type: 'moveLayout', name: layout.name, to: index + 1 })}>Down</button>
                    <button type="button" className="btn btn-sm" data-testid={`ed-layout-duplicate-${layout.name}`} onClick={() => { let as = `${layout.name}-copy`, n = 1; while (layouts.some((l) => l.name === as)) as = `${layout.name}-copy${++n}`; apply({ type: 'duplicateLayout', name: layout.name, as }) }}>Duplicate</button>
                    <button type="button" className="btn btn-sm" data-testid={`ed-layout-delete-${layout.name}`} onClick={() => apply({ type: 'deleteLayout', name: layout.name })}>Delete</button>
                  </div>
                  <Problems diagnostics={own.filter((d) => formTarget(d.path).area === 'layout' && (formTarget(d.path) as { tile?: number }).tile === undefined)} testId={`ed-layout-problems-${layout.name}`} />
                  {layout.type === 'flow' && <div className="hint">A flow layout places widgets in sections and blocks: edit it in the YAML view (the report editor is the next slice).</div>}
                  {tiles && (
                    <table className="table" data-testid={`ed-tiles-${layout.name}`}>
                      <thead><tr><th>Tile</th><th>x</th><th>y</th><th>w</th><th>h</th><th /></tr></thead>
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
            <button type="button" className="btn" data-testid="ed-duplicate" disabled={!NAME_PATTERN.test(duplicateAs)} onClick={() => {
              void store.duplicate(kind, draft.name, duplicateAs).then(async () => { await refresh(); navigate(`${AREA[kind]}/${encodeURIComponent(duplicateAs)}?edit=1`) }, (e: unknown) => setMessage({ tone: 'error', text: e instanceof Error ? e.message : String(e) }))
            }}>Duplicate</button>
            {!confirmDelete
              ? <button type="button" className="btn" data-testid="ed-delete" onClick={() => setConfirmDelete(true)}>Delete…</button>
              : (
                <>
                  <span className="hint">Delete {draft.name}.yaml for good?</span>
                  <button type="button" className="btn btn-primary" data-testid="ed-delete-confirm" onClick={() => {
                    void store.delete(kind, draft.name).then(async () => { await refresh(); navigate(AREA[kind]) }, (e: unknown) => setMessage({ tone: 'error', text: e instanceof Error ? e.message : String(e) }))
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
