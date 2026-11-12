import { useState, type DragEvent } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { ErrorBanner, formatBytes } from '@dbdatakit/shell'
import { ConsoleShell } from '../ConsoleShell'
import {
  backlogApi, columns, dropRefusal, historyLine, importProblems, movesFrom, neighbourFor, pointsLine, priorityRank, problemText, stateTitle, summaryLine,
  type BacklogDetail, type BacklogItem, type BacklogSprint, type BacklogWorkflow,
} from '../backlog'

type Tab = 'backlog' | 'board' | 'sprints' | 'import'
const TABS: { id: Tab; label: string }[] = [{ id: 'backlog', label: 'Backlog' }, { id: 'board', label: 'Board' }, { id: 'sprints', label: 'Sprints' }, { id: 'import', label: 'Import and export' }]

/**
 * DbDataBacklog in the console (plan 27a BK01, BK03, BK04): the backlog of a project in order, the board (a card is dragged to the column of the state it moves to, and the workflow's rules decide), sprints (plan, start, close with what is carried on),
 * and an item with its comments, links, attachments and history. Everything is done as the signed-in person; an administrator works as a lead and a viewer only reads. The rules are the library's: a refusal is shown in the server's words.
 * It is the console's own host of the backlog until the serving layer's `backlog` module (BK02) and the app of BK03 exist.
 */
export function BacklogPage() {
  const client = useQueryClient()
  const [params, setParams] = useSearchParams()
  const workflow = useQuery({ queryKey: ['backlog', 'workflow'], queryFn: backlogApi.workflow })
  const projects = useQuery({ queryKey: ['backlog', 'projects'], queryFn: backlogApi.projects })
  const project = params.get('project') ?? projects.data?.[0]?.id ?? null
  const tab = (params.get('tab') as Tab | null) ?? 'backlog'
  const itemId = params.get('item')
  const items = useQuery({ queryKey: ['backlog', 'items', project], queryFn: () => backlogApi.items(project!), enabled: project !== null })
  const sprints = useQuery({ queryKey: ['backlog', 'sprints', project], queryFn: () => backlogApi.sprints(project!), enabled: project !== null })
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const role = workflow.data?.role ?? 'viewer'
  const canEdit = role !== 'viewer'
  const set = (next: Record<string, string | null>) => setParams((old) => { const p = new URLSearchParams(old); for (const [k, v] of Object.entries(next)) { if (v === null) p.delete(k); else p.set(k, v) } return p })
  const refresh = () => client.invalidateQueries({ queryKey: ['backlog'] })
  /** Runs a call, shows what it said when it was refused, and reads everything again. */
  const act = async <T,>(call: () => Promise<T>, done?: (result: T) => string): Promise<T | undefined> => {
    setMessage(null)
    try {
      const result = await call()
      if (done) setMessage({ ok: true, text: done(result) })
      await refresh()
      return result
    } catch (e) {
      setMessage({ ok: false, text: problemText(e) })
      return undefined
    }
  }

  return (
    <ConsoleShell crumbs={[{ label: 'Backlog', heading: true }]}>
      <div className="pane" data-testid="backlog-page">
        <div className="page-head">
          <h1 className="page-title">Backlog</h1>
          <span className="page-note">Work items, the board and sprints, kept in the suite's own state. {canEdit ? 'You work here as a ' + (role === 'lead' ? 'lead' : 'member') + '.' : 'You can read; an administrator changes.'}</span>
        </div>
        <ErrorBanner error={workflow.error ?? projects.error ?? items.error ?? sprints.error} />
        <div className="row" style={{ gap: 8, marginBottom: 10, alignItems: 'center' }}>
          {projects.data && projects.data.length > 0 && (
            <select value={project ?? ''} onChange={(e) => set({ project: e.target.value, item: null })} aria-label="Backlog" data-testid="backlog-project">
              {projects.data.map((p) => <option key={p.id} value={p.id}>{p.id}: {p.name}</option>)}
            </select>
          )}
          {role === 'lead' && <NewProject onMake={(key, name) => act(() => backlogApi.createProject(key, name, null), (p) => `Made the backlog ${p.id}.`).then((p) => { if (p) set({ project: p.id, item: null }) })} />}
        </div>
        {message && <div className={message.ok ? 'hint' : 'banner error'} role={message.ok ? 'status' : 'alert'} data-testid="backlog-message">{message.text}</div>}
        {projects.data?.length === 0 && <div className="empty" data-testid="backlog-none">{role === 'lead' ? 'There is no backlog yet. Make the first one above.' : 'There is no backlog yet; an administrator makes it.'}</div>}
        {project && workflow.data && items.data && sprints.data && (
          <>
            <div className="subtabbar" role="tablist" style={{ marginBottom: 10 }}>
              {TABS.map((t) => <button key={t.id} type="button" role="tab" aria-selected={tab === t.id} className={'btn btn-sm' + (tab === t.id ? ' btn-primary' : '')} onClick={() => set({ tab: t.id })} data-testid={`backlog-tab-${t.id}`}>{t.label}</button>)}
            </div>
            {tab === 'backlog' && <BacklogList project={project} workflow={workflow.data} items={items.data} sprints={sprints.data} canEdit={canEdit} act={act} open={(id) => set({ item: id })} />}
            {tab === 'board' && <Board workflow={workflow.data} items={items.data} sprints={sprints.data} canEdit={canEdit} act={act} open={(id) => set({ item: id })} />}
            {tab === 'sprints' && <Sprints project={project} workflow={workflow.data} items={items.data} sprints={sprints.data} role={role} act={act} />}
            {tab === 'import' && <ImportExport project={project} canEdit={canEdit} act={act} />}
            {itemId && <ItemPanel id={itemId} workflow={workflow.data} items={items.data} sprints={sprints.data} canEdit={canEdit} role={role} act={act} close={() => set({ item: null })} />}
          </>
        )}
      </div>
    </ConsoleShell>
  )
}

type Act = <T>(call: () => Promise<T>, done?: (result: T) => string) => Promise<T | undefined>

function NewProject({ onMake }: { onMake: (key: string, name: string) => void }) {
  const [open, setOpen] = useState(false)
  const [key, setKey] = useState('')
  const [name, setName] = useState('')
  if (!open) return <button type="button" className="btn btn-sm" onClick={() => setOpen(true)} data-testid="backlog-project-new">New backlog</button>
  return (
    <span className="row" style={{ gap: 6 }}>
      <input value={key} onChange={(e) => setKey(e.target.value.toUpperCase())} placeholder="KEY" size={8} aria-label="Key of the backlog" data-testid="backlog-project-key" />
      <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Name" aria-label="Name of the backlog" data-testid="backlog-project-name" />
      <button type="button" className="btn btn-sm btn-primary" disabled={!key || !name} onClick={() => { onMake(key, name); setOpen(false); setKey(''); setName('') }} data-testid="backlog-project-make">Make</button>
      <button type="button" className="btn btn-sm" onClick={() => setOpen(false)}>Cancel</button>
    </span>
  )
}

function NewItem({ project, workflow, act, open }: { project: string; workflow: BacklogWorkflow; act: Act; open: (id: string) => void }) {
  const [type, setType] = useState('story')
  const [title, setTitle] = useState('')
  return (
    <form className="row" style={{ gap: 6, marginBottom: 10 }} onSubmit={(e) => { e.preventDefault(); void act(() => backlogApi.createItem(project, { type, title }), (i) => `Made ${i.key}.`).then((i) => { if (i) { setTitle(''); open(i.id) } }) }}>
      <select value={type} onChange={(e) => setType(e.target.value)} aria-label="Kind of item" data-testid="backlog-new-type">{workflow.itemTypes.map((t) => <option key={t} value={t}>{t}</option>)}</select>
      <input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="What needs doing?" size={50} aria-label="Title of the new item" data-testid="backlog-new-title" />
      <button type="submit" className="btn btn-sm btn-primary" disabled={title.trim() === ''} data-testid="backlog-new-make">Add</button>
    </form>
  )
}

function BacklogList({ project, workflow, items, sprints, canEdit, act, open }: { project: string; workflow: BacklogWorkflow; items: BacklogItem[]; sprints: BacklogSprint[]; canEdit: boolean; act: Act; open: (id: string) => void }) {
  const [filter, setFilter] = useState('all')
  const shown = items.filter((i) => filter === 'all' || (filter === 'none' ? i.sprint === null : i.sprint === filter)).sort((a, b) => a.sortKey - b.sortKey)
  const sprintName = (id: string | null) => sprints.find((s) => s.id === id)?.name ?? ''
  return (
    <div data-testid="backlog-list">
      {canEdit && <NewItem project={project} workflow={workflow} act={act} open={open} />}
      <div className="row" style={{ gap: 8, marginBottom: 6, alignItems: 'center' }}>
        <select value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Show" data-testid="backlog-filter">
          <option value="all">All items</option><option value="none">Not in a sprint</option>
          {sprints.filter((s) => s.state !== 'closed').map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
        </select>
        <span className="hint" data-testid="backlog-count">{pointsLine(shown)}</span>
      </div>
      <table className="table">
        <thead><tr><th>Key</th><th>Title</th><th>Kind</th><th>State</th><th>Priority</th><th>Points</th><th>Assignee</th><th>Sprint</th>{canEdit && <th>Place</th>}</tr></thead>
        <tbody>
          {shown.length === 0 && <tr><td colSpan={9} className="hint" data-testid="backlog-empty">Nothing here yet.</td></tr>}
          {shown.map((i) => (
            <tr key={i.id} data-testid={`backlog-row-${i.key}`} data-state={i.state}>
              <td className="mono">{i.key}</td>
              <td><button type="button" className="btn btn-sm" onClick={() => open(i.id)} data-testid={`backlog-open-${i.key}`}>{i.title}</button></td>
              <td>{i.type}</td>
              <td>{stateTitle(workflow, i.state)}</td>
              <td>{i.priority}</td>
              <td>{i.estimate ?? ''}</td>
              <td>{i.assignee ?? ''}</td>
              <td>{sprintName(i.sprint)}</td>
              {canEdit && (
                <td>
                  <button type="button" className="btn btn-sm" aria-label={`Move ${i.key} up`} disabled={neighbourFor(shown, i.id, 'up') === undefined} onClick={() => void act(() => backlogApi.rank(i.id, neighbourFor(shown, i.id, 'up') ?? null))} data-testid={`backlog-up-${i.key}`}>Up</button>{' '}
                  <button type="button" className="btn btn-sm" aria-label={`Move ${i.key} down`} disabled={neighbourFor(shown, i.id, 'down') === undefined} onClick={() => void act(() => backlogApi.rank(i.id, neighbourFor(shown, i.id, 'down') ?? null))} data-testid={`backlog-down-${i.key}`}>Down</button>
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function Board({ workflow, items, sprints, canEdit, act, open }: { workflow: BacklogWorkflow; items: BacklogItem[]; sprints: BacklogSprint[]; canEdit: boolean; act: Act; open: (id: string) => void }) {
  const active = sprints.find((s) => s.state === 'active')
  const [filter, setFilter] = useState<string>(active?.id ?? 'all')
  const [dragging, setDragging] = useState<string | null>(null)
  const [pending, setPending] = useState<{ item: BacklogItem; to: string } | null>(null)
  const [resolution, setResolution] = useState('')
  const [refusal, setRefusal] = useState<string | null>(null)
  const shown = items.filter((i) => i.type !== 'epic' && (filter === 'all' || (filter === 'none' ? i.sprint === null : i.sprint === filter)))
  const move = (item: BacklogItem, to: string, why: string | null = null) => {
    setRefusal(null)
    const refused = dropRefusal(workflow, item, to)
    if (refused) { setRefusal(refused); return }
    if (item.state === to) return
    if (workflow.states.find((s) => s.id === to)?.requires.includes('resolution') && why === null) { setPending({ item, to }); setResolution(''); return }
    setPending(null)
    void act(() => backlogApi.transition(item.id, to, why, item.version), (i) => `${i.key} is now ${stateTitle(workflow, i.state)}.`)
  }
  const drop = (e: DragEvent, to: string) => { e.preventDefault(); const item = items.find((i) => i.id === dragging); setDragging(null); if (item) move(item, to) }
  const dropOnCard = (e: DragEvent, target: BacklogItem) => {
    e.preventDefault(); e.stopPropagation()
    const item = items.find((i) => i.id === dragging); setDragging(null)
    if (!item || item.id === target.id) return
    if (item.state === target.state) void act(() => backlogApi.rank(item.id, target.id))
    else move(item, target.state)
  }
  return (
    <div data-testid="backlog-board">
      <div className="row" style={{ gap: 8, marginBottom: 6, alignItems: 'center' }}>
        <select value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Show on the board" data-testid="board-filter">
          <option value="all">All items</option><option value="none">Not in a sprint</option>
          {sprints.filter((s) => s.state !== 'closed').map((s) => <option key={s.id} value={s.id}>{s.name}{s.state === 'active' ? ' (active)' : ''}</option>)}
        </select>
        <span className="hint">{pointsLine(shown)}</span>
      </div>
      {refusal && <div className="banner error" role="alert" data-testid="board-refusal">{refusal}</div>}
      {pending && (
        <div className="card" style={{ marginBottom: 8 }} data-testid="board-resolution">
          <div className="card-body">
            {pending.item.key} goes to {stateTitle(workflow, pending.to)}; say how it ended:{' '}
            <input value={resolution} onChange={(e) => setResolution(e.target.value)} size={40} aria-label="Resolution" data-testid="board-resolution-text" />{' '}
            <button type="button" className="btn btn-sm btn-primary" disabled={resolution.trim() === ''} onClick={() => move(pending.item, pending.to, resolution)} data-testid="board-resolution-save">Move</button>{' '}
            <button type="button" className="btn btn-sm" onClick={() => setPending(null)}>Cancel</button>
          </div>
        </div>
      )}
      <div style={{ display: 'grid', gridTemplateColumns: `repeat(${workflow.states.length}, minmax(150px, 1fr))`, gap: 8, alignItems: 'start' }}>
        {columns(workflow, shown).map(({ state, items: cards }) => (
          <div key={state.id} className="card" onDragOver={(e) => { if (canEdit) e.preventDefault() }} onDrop={(e) => drop(e, state.id)} data-testid={`board-column-${state.id}`} data-category={state.category}>
            <div className="card-body">
              <strong>{state.title}</strong> <span className="hint" data-testid={`board-count-${state.id}`}>{cards.length}</span>
              {cards.map((i) => (
                <div key={i.id} draggable={canEdit} onDragStart={() => setDragging(i.id)} onDrop={(e) => dropOnCard(e, i)} className="card" style={{ margin: '6px 0', padding: 6 }} data-testid={`board-card-${i.key}`}>
                  <div><span className="mono">{i.key}</span> <span className="hint">{i.priority}{i.estimate !== null ? `, ${i.estimate}` : ''}</span></div>
                  <button type="button" className="btn btn-sm" style={{ whiteSpace: 'normal', textAlign: 'left' }} onClick={() => open(i.id)} data-testid={`board-open-${i.key}`}>{i.title}</button>
                  {i.assignee && <div className="hint">{i.assignee}</div>}
                  {canEdit && (
                    <select value="" onChange={(e) => { if (e.target.value) move(i, e.target.value) }} aria-label={`Move ${i.key} to`} data-testid={`board-move-${i.key}`}>
                      <option value="">Move to…</option>
                      {movesFrom(workflow, i.state).map((s) => <option key={s} value={s}>{stateTitle(workflow, s)}</option>)}
                    </select>
                  )}
                </div>
              ))}
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}

function Sprints({ project, workflow, items, sprints, role, act }: { project: string; workflow: BacklogWorkflow; items: BacklogItem[]; sprints: BacklogSprint[]; role: BacklogWorkflow['role']; act: Act }) {
  const [name, setName] = useState('')
  const [goal, setGoal] = useState('')
  const [carry, setCarry] = useState<Record<string, string>>({})
  const doneIds = new Set(workflow.states.filter((s) => s.category === 'done').map((s) => s.id))
  return (
    <div data-testid="backlog-sprints">
      {role !== 'viewer' && (
        <form className="row" style={{ gap: 6, marginBottom: 10 }} onSubmit={(e) => { e.preventDefault(); void act(() => backlogApi.createSprint(project, name, goal || null), (s) => `Planned ${s.name}.`).then((s) => { if (s) { setName(''); setGoal('') } }) }}>
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Sprint name" aria-label="Name of the sprint" data-testid="sprint-name" />
          <input value={goal} onChange={(e) => setGoal(e.target.value)} placeholder="Goal (optional)" size={40} aria-label="Goal of the sprint" data-testid="sprint-goal" />
          <button type="submit" className="btn btn-sm btn-primary" disabled={name.trim() === ''} data-testid="sprint-make">Plan a sprint</button>
        </form>
      )}
      {sprints.length === 0 && <div className="empty" data-testid="sprints-none">No sprint is planned.</div>}
      {sprints.map((s) => {
        const inSprint = items.filter((i) => i.sprint === s.id)
        const open = inSprint.filter((i) => !doneIds.has(i.state))
        const planned = sprints.filter((x) => x.state === 'planned' && x.id !== s.id)
        return (
          <div key={s.id} className="card" style={{ marginBottom: 8 }} data-testid={`sprint-${s.name}`} data-state={s.state}>
            <div className="card-body">
              <strong>{s.name}</strong> <span className="hint" data-testid={`sprint-state-${s.name}`}>{s.state}</span> <span className="hint">{pointsLine(inSprint)}</span>
              {s.goal && <div className="hint">{s.goal}</div>}
              {s.state === 'planned' && role === 'lead' && <div style={{ marginTop: 6 }}><button type="button" className="btn btn-sm btn-primary" onClick={() => void act(() => backlogApi.startSprint(s.id), () => `Started ${s.name}.`)} data-testid={`sprint-start-${s.name}`}>Start</button></div>}
              {s.state === 'active' && role === 'lead' && (
                <div style={{ marginTop: 6 }}>
                  {open.length > 0 && (
                    <select value={carry[s.id] ?? ''} onChange={(e) => setCarry({ ...carry, [s.id]: e.target.value })} aria-label="Where open items go" data-testid={`sprint-carry-${s.name}`}>
                      <option value="">Open items go back to the backlog</option>
                      {planned.map((p) => <option key={p.id} value={p.id}>Open items go to {p.name}</option>)}
                    </select>
                  )}{' '}
                  <button type="button" className="btn btn-sm" onClick={() => void act(() => backlogApi.closeSprint(s.id, carry[s.id] || null), (r) => `Closed ${s.name}: ${summaryLine(r.summary, planned.find((p) => p.id === r.summary.movedTo)?.name ?? null)}`)} data-testid={`sprint-close-${s.name}`}>Close the sprint</button>
                </div>
              )}
            </div>
          </div>
        )
      })}
    </div>
  )
}

function ImportExport({ project, canEdit, act }: { project: string; canEdit: boolean; act: Act }) {
  const [text, setText] = useState('schema: work-item/1\nproject: ' + project + '\nitems:\n  - { id: first, type: story, title: A first item }\n')
  const [problems, setProblems] = useState<string[]>([])
  const run = async () => {
    setProblems([])
    try { await act(() => backlogApi.importItems(project, text).catch((e) => { setProblems(importProblems(e)); throw e }), (r) => `Made ${r.made} item${r.made === 1 ? '' : 's'}: ${r.items.map((i) => i.key).join(', ')}.`) } catch { /* shown above */ }
  }
  return (
    <div data-testid="backlog-import">
      <p className="hint">A <span className="mono">work-item/1</span> file makes its items (epics first) at the end of the backlog. The same file is what Export writes.</p>
      <textarea value={text} onChange={(e) => setText(e.target.value)} rows={10} style={{ width: '100%', fontFamily: 'monospace' }} aria-label="Work items to import" data-testid="import-text" disabled={!canEdit} />
      <div style={{ margin: '6px 0' }}>
        <button type="button" className="btn btn-sm btn-primary" disabled={!canEdit || text.trim() === ''} onClick={() => void run()} data-testid="import-run">Import</button>{' '}
        <a className="btn btn-sm" href={backlogApi.exportUrl(project)} download={`${project}-work-items.yml`} data-testid="import-export">Export the backlog</a>
      </div>
      {problems.length > 0 && <ul className="error" data-testid="import-problems">{problems.map((p, i) => <li key={i}>{p}</li>)}</ul>}
    </div>
  )
}

function ItemPanel({ id, workflow, items, sprints, canEdit, role, act, close }: { id: string; workflow: BacklogWorkflow; items: BacklogItem[]; sprints: BacklogSprint[]; canEdit: boolean; role: BacklogWorkflow['role']; act: Act; close: () => void }) {
  const detail = useQuery({ queryKey: ['backlog', 'item', id], queryFn: () => backlogApi.detail(id) })
  const d = detail.data
  return (
    <div className="card" style={{ marginTop: 14 }} data-testid="item-panel" data-item={d?.item.key}>
      <div className="card-body">
        <ErrorBanner error={detail.error} />
        {!d && !detail.error && <div className="hint">Reading the item…</div>}
        {d && <ItemBody key={d.item.id + ':' + d.item.version + ':' + d.comments.length + ':' + d.links.length} d={d} workflow={workflow} items={items} sprints={sprints} canEdit={canEdit} role={role} act={act} close={close} />}
      </div>
    </div>
  )
}

function ItemBody({ d, workflow, items, sprints, canEdit, role, act, close }: { d: BacklogDetail; workflow: BacklogWorkflow; items: BacklogItem[]; sprints: BacklogSprint[]; canEdit: boolean; role: BacklogWorkflow['role']; act: Act; close: () => void }) {
  const i = d.item
  const [title, setTitle] = useState(i.title)
  const [description, setDescription] = useState(i.description ?? '')
  const [estimate, setEstimate] = useState(i.estimate === null ? '' : String(i.estimate))
  const [assignee, setAssignee] = useState(i.assignee ?? '')
  const [labels, setLabels] = useState(i.labels.join(', '))
  const [resolution, setResolution] = useState(i.resolution ?? '')
  const [comment, setComment] = useState('')
  const [linkKind, setLinkKind] = useState('relates')
  const [linkTarget, setLinkTarget] = useState('')
  const [linkObject, setLinkObject] = useState(workflow.objectKinds[0] ?? '')
  const epics = items.filter((x) => x.type === 'epic' && x.id !== i.id)
  const others = items.filter((x) => x.id !== i.id)
  const save = () => act(() => backlogApi.change(i.id, {
    title, description: description || null, estimate: estimate === '' ? null : Number(estimate), assignee: assignee || null,
    labels: labels.split(',').map((l) => l.trim()).filter(Boolean), version: i.version,
  }), () => `Saved ${i.key}.`)
  const field = (name: string, value: unknown) => act(() => backlogApi.change(i.id, { [name]: value, version: i.version }))
  const itemLink = linkKind === 'relates' || linkKind === 'blocks' || linkKind === 'duplicates'
  const titles = (s: string) => stateTitle(workflow, s)
  return (
    <div data-testid="item-detail">
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h2 style={{ margin: 0 }}><span className="mono" data-testid="item-key">{i.key}</span> <span className="hint">{i.type}, {titles(i.state)}</span></h2>
        <button type="button" className="btn btn-sm" onClick={close} data-testid="item-close">Close</button>
      </div>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(260px, 1fr))', gap: 10, marginTop: 8 }}>
        <label>Title<br /><input value={title} onChange={(e) => setTitle(e.target.value)} disabled={!canEdit} style={{ width: '100%' }} data-testid="item-title" /></label>
        <label>Priority<br />
          <select value={i.priority} disabled={!canEdit} onChange={(e) => void field('priority', e.target.value)} data-testid="item-priority">
            {[...workflow.priorities].sort((a, b) => priorityRank(a) - priorityRank(b)).map((p) => <option key={p} value={p}>{p}</option>)}
          </select>
        </label>
        <label>Points<br /><input value={estimate} onChange={(e) => setEstimate(e.target.value)} disabled={!canEdit} size={6} data-testid="item-estimate" /></label>
        <label>Assignee<br /><input value={assignee} onChange={(e) => setAssignee(e.target.value)} disabled={!canEdit} data-testid="item-assignee" /></label>
        <label>Labels<br /><input value={labels} onChange={(e) => setLabels(e.target.value)} disabled={!canEdit} data-testid="item-labels" /></label>
        {i.type !== 'epic' && (
          <label>Epic<br />
            <select value={i.epic ?? ''} disabled={!canEdit} onChange={(e) => void field('epic', e.target.value || null)} data-testid="item-epic">
              <option value="">None</option>{epics.map((e) => <option key={e.id} value={e.id}>{e.key}: {e.title}</option>)}
            </select>
          </label>
        )}
        {i.type !== 'epic' && (
          <label>Sprint<br />
            <select value={i.sprint ?? ''} disabled={!canEdit} onChange={(e) => void act(() => backlogApi.setSprint(i.id, e.target.value || null))} data-testid="item-sprint">
              <option value="">Backlog</option>{sprints.filter((s) => s.state !== 'closed' || s.id === i.sprint).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </select>
          </label>
        )}
        <label>Move to<br />
          <select value="" disabled={!canEdit || d.targets.length === 0} onChange={(e) => { if (e.target.value) void act(() => backlogApi.transition(i.id, e.target.value, resolution || null, i.version), (r) => `${r.key} is now ${titles(r.state)}.`) }} data-testid="item-move">
            <option value="">{d.targets.length === 0 ? 'Nowhere from here' : 'Choose a state…'}</option>{d.targets.map((s) => <option key={s} value={s}>{titles(s)}</option>)}
          </select>
        </label>
        <label>Resolution (for a move that ends the item)<br /><input value={resolution} onChange={(e) => setResolution(e.target.value)} disabled={!canEdit} data-testid="item-resolution" /></label>
      </div>
      <label>Description<br /><textarea value={description} onChange={(e) => setDescription(e.target.value)} disabled={!canEdit} rows={4} style={{ width: '100%' }} data-testid="item-description" /></label>
      {canEdit && <div style={{ margin: '6px 0' }}><button type="button" className="btn btn-sm btn-primary" onClick={() => void save()} data-testid="item-save">Save</button>{role === 'lead' && <> <button type="button" className="btn btn-sm" onClick={() => { if (window.confirm(`Delete ${i.key} with its comments and attachments?`)) void act(() => backlogApi.remove(i.id), () => `Deleted ${i.key}.`).then(close) }} data-testid="item-delete">Delete</button></>}</div>}
      {d.children.length > 0 && <div data-testid="item-children"><strong>Items of this epic</strong><ul>{d.children.map((c) => <li key={c.id} className="mono">{c.key} <span className="hint">{c.title}, {titles(c.state)}</span></li>)}</ul></div>}

      <h3>Comments</h3>
      <ul data-testid="item-comments">{d.comments.map((c) => <li key={c.id}><strong>{c.authorName}</strong> <span className="hint">{c.createdAt}{c.editedAt ? ' (edited)' : ''}</span><div>{c.body}</div></li>)}{d.comments.length === 0 && <li className="hint">No comments.</li>}</ul>
      {canEdit && <div className="row" style={{ gap: 6 }}><input value={comment} onChange={(e) => setComment(e.target.value)} size={60} placeholder="Add a comment" aria-label="Comment" data-testid="item-comment-text" /><button type="button" className="btn btn-sm" disabled={comment.trim() === ''} onClick={() => void act(() => backlogApi.comment(i.id, comment)).then(() => setComment(''))} data-testid="item-comment-add">Comment</button></div>}

      <h3>Links</h3>
      <ul data-testid="item-links">
        {d.links.map((l) => (
          <li key={l.id} data-state={l.state}>{l.kind === 'relates' || l.kind === 'blocks' || l.kind === 'duplicates' ? `${l.kind} ${l.targetKey ?? l.target}` : `${l.targetKind}: ${l.target}`}{l.label ? ` (${l.label})` : ''}{l.state === 'dropped' && <span className="hint"> the target is gone</span>}{canEdit && <> <button type="button" className="btn btn-sm" onClick={() => void act(() => backlogApi.unlink(l.id))} data-testid={`item-unlink-${l.id}`}>Remove</button></>}</li>
        ))}
        {d.links.length === 0 && <li className="hint">No links.</li>}
      </ul>
      {canEdit && (
        <div className="row" style={{ gap: 6 }}>
          <select value={linkKind} onChange={(e) => { setLinkKind(e.target.value); setLinkTarget('') }} aria-label="Kind of link" data-testid="item-link-kind">{workflow.linkKinds.map((k) => <option key={k} value={k}>{k}</option>)}</select>
          {linkKind === 'object' && <select value={linkObject} onChange={(e) => setLinkObject(e.target.value)} aria-label="Kind of object" data-testid="item-link-object">{workflow.objectKinds.map((k) => <option key={k} value={k}>{k}</option>)}</select>}
          {itemLink
            ? <select value={linkTarget} onChange={(e) => setLinkTarget(e.target.value)} aria-label="Item to link" data-testid="item-link-target"><option value="">Choose an item…</option>{others.map((o) => <option key={o.id} value={o.id}>{o.key}: {o.title}</option>)}</select>
            : <input value={linkTarget} onChange={(e) => setLinkTarget(e.target.value)} placeholder={linkKind === 'pull-request' ? 'owner/repo#12' : 'identity'} aria-label="Target of the link" data-testid="item-link-target" />}
          <button type="button" className="btn btn-sm" disabled={linkTarget.trim() === ''} onClick={() => void act(() => backlogApi.link(i.id, linkKind, linkKind === 'object' ? linkObject : null, linkTarget, null)).then(() => setLinkTarget(''))} data-testid="item-link-add">Link</button>
        </div>
      )}

      <h3>Attachments</h3>
      <ul data-testid="item-attachments">
        {d.attachments.map((a) => <li key={a.id}><a href={backlogApi.attachmentUrl(a.id)} download={a.fileName}>{a.fileName}</a> <span className="hint">{formatBytes(a.size)}, {a.addedBy}</span>{canEdit && <> <button type="button" className="btn btn-sm" onClick={() => void act(() => backlogApi.removeAttachment(a.id))} data-testid={`item-detach-${a.fileName}`}>Remove</button></>}</li>)}
        {d.attachments.length === 0 && <li className="hint">No attachments.</li>}
      </ul>
      {canEdit && <input type="file" aria-label="Attach a file" data-testid="item-attach" onChange={(e) => { const file = e.target.files?.[0]; if (file) void act(() => backlogApi.attach(i.id, file), () => `Attached ${file.name}.`) }} />}

      <h3>History</h3>
      <ol data-testid="item-history" reversed>{[...d.history].reverse().map((h) => <li key={h.seq} data-kind={h.kind}>{historyLine(h, titles)} <span className="hint">{h.at}</span></li>)}</ol>
    </div>
  )
}
