import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { workspaceApi, type ProjectSummary, type WorkspaceSummary } from '../api'
import { abandonState, createState, describeEvent } from '../model'
import { WorkspaceShell } from '../WorkspaceShell'

const when = (iso: string) => (iso ? new Date(iso).toLocaleString() : '')

/** What happened to one workspace, newest first. */
function History({ project, id }: { project: string; id: string }) {
  const events = useQuery({ queryKey: ['workspace', 'events', project, id], queryFn: () => workspaceApi.events(project, id) })
  return (
    <div className="hint" data-testid={`ws-history-${id}`}>
      <ErrorBanner error={events.error} />
      {events.isLoading && 'Loading...'}
      {(events.data ?? []).map((e, i) => <div key={i} data-testid="ws-event" data-kind={e.kind}>{describeEvent(e)} <span className="muted">({when(e.at)})</span></div>)}
    </div>
  )
}

/** One workspace row: its owner and branch, its history, and Abandon for its owner (asked twice, because what was not merged goes with it). */
function WorkspaceRow({ project, workspace, canWrite }: { project: string; workspace: WorkspaceSummary; canWrite: boolean }) {
  const client = useQueryClient()
  const [asking, setAsking] = useState(false)
  const [showHistory, setShowHistory] = useState(false)
  const abandon = useMutation({
    mutationFn: () => workspaceApi.abandon(project, workspace.id),
    onSuccess: async () => { setAsking(false); await client.invalidateQueries({ queryKey: ['workspace'] }) },
  })
  const can = abandonState({ mine: workspace.mine, canWrite })
  return (
    <tr data-testid="ws-workspace" data-workspace={workspace.id} data-project={project}>
      <td className="mono" data-testid="ws-name">{workspace.name}</td>
      <td data-testid="ws-owner">{workspace.owner}{workspace.mine ? ' (you)' : ''}</td>
      <td className="mono">{workspace.branch}</td>
      <td>{when(workspace.created)}</td>
      <td>
        <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
          <button type="button" className="btn" data-testid={`ws-history-toggle-${workspace.id}`} onClick={() => setShowHistory((v) => !v)}>{showHistory ? 'Hide history' : 'History'}</button>
          {!asking && <button type="button" className="btn" disabled={!can.enabled} title={can.why ?? undefined} data-testid={`ws-abandon-${workspace.id}`} onClick={() => setAsking(true)}>Abandon</button>}
          {asking && (
            <>
              <span className="hint">Whatever was committed here and not merged is lost.</span>
              <button type="button" className="btn btn-danger" disabled={abandon.isPending} data-testid={`ws-abandon-confirm-${workspace.id}`} onClick={() => abandon.mutate()}>Abandon it</button>
              <button type="button" className="btn" data-testid={`ws-abandon-cancel-${workspace.id}`} onClick={() => setAsking(false)}>Keep it</button>
            </>
          )}
        </div>
        <ErrorBanner error={abandon.error} />
        {showHistory && <History project={project} id={workspace.id} />}
      </td>
    </tr>
  )
}

/** A project: its workspaces, and the form that makes one. */
function ProjectCard({ project, canWrite }: { project: ProjectSummary; canWrite: boolean }) {
  const client = useQueryClient()
  const [name, setName] = useState('')
  const workspaces = useQuery({ queryKey: ['workspace', 'list', project.id], queryFn: () => workspaceApi.workspaces(project.id) })
  const create = useMutation({
    mutationFn: () => workspaceApi.createWorkspace(project.id, name),
    onSuccess: async () => { setName(''); await client.invalidateQueries({ queryKey: ['workspace'] }) },
  })
  const state = createState({ name, canWrite, busy: create.isPending, what: 'workspace', taken: (workspaces.data ?? []).filter((w) => w.mine).map((w) => w.name) })
  return (
    <div className="card" data-testid="ws-project" data-project={project.id}>
      <div className="card-body">
        <div className="card-title mono" data-testid="ws-project-title">{project.title ?? project.id} <span className="muted">({project.kind})</span></div>
        <ErrorBanner error={workspaces.error} />
        {workspaces.data?.length === 0 && <div className="empty" data-testid={`ws-empty-${project.id}`}>No one has a workspace in this project yet.</div>}
        {(workspaces.data?.length ?? 0) > 0 && (
          <table className="table" data-testid={`ws-table-${project.id}`}>
            <thead><tr><th>Name</th><th>Owner</th><th>Branch</th><th>Made</th><th /></tr></thead>
            <tbody>{(workspaces.data ?? []).map((w) => <WorkspaceRow key={w.id} project={project.id} workspace={w} canWrite={canWrite} />)}</tbody>
          </table>
        )}
        <form className="row" style={{ gap: 8, alignItems: 'center', marginTop: 10, flexWrap: 'wrap' }} onSubmit={(e) => { e.preventDefault(); if (state.enabled) create.mutate() }}>
          <input className="input mono" aria-label={`New workspace in ${project.id}`} placeholder="new-workspace" value={name} onChange={(e) => setName(e.target.value)} data-testid={`ws-new-name-${project.id}`} />
          <button type="submit" className="btn btn-primary" disabled={!state.enabled} data-testid={`ws-new-create-${project.id}`}>New workspace</button>
          {state.why && <span className="hint" data-testid={`ws-new-why-${project.id}`}>{state.why}</span>}
        </form>
        <ErrorBanner error={create.error} />
      </div>
    </div>
  )
}

/** The form that makes a project (an administrator's). */
function NewProject({ canWrite }: { canWrite: boolean }) {
  const client = useQueryClient()
  const [open, setOpen] = useState(false)
  const [id, setId] = useState('')
  const [title, setTitle] = useState('')
  const projects = useQuery({ queryKey: ['workspace', 'projects'], queryFn: workspaceApi.projects })
  const create = useMutation({
    mutationFn: () => workspaceApi.createProject(id, title.trim() || null),
    onSuccess: async () => { setId(''); setTitle(''); setOpen(false); await client.invalidateQueries({ queryKey: ['workspace'] }) },
  })
  const state = createState({ name: id, canWrite, busy: create.isPending, what: 'project', taken: (projects.data ?? []).map((p) => p.id) })
  if (!canWrite) return <span className="hint" data-testid="ws-new-project-why">Making a project needs the administrator role.</span>
  if (!open) return <button type="button" className="btn btn-primary" data-testid="ws-new-project-open" onClick={() => setOpen(true)}>New project</button>
  return (
    <form className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }} data-testid="ws-new-project-form" onSubmit={(e) => { e.preventDefault(); if (state.enabled) create.mutate() }}>
      <input className="input mono" aria-label="Project id" placeholder="project-id" value={id} onChange={(e) => setId(e.target.value)} data-testid="ws-new-project-id" />
      <input className="input" aria-label="Project title" placeholder="Title (optional)" value={title} onChange={(e) => setTitle(e.target.value)} data-testid="ws-new-project-title" />
      <button type="submit" className="btn btn-primary" disabled={!state.enabled} data-testid="ws-new-project-create">Make project</button>
      <button type="button" className="btn" data-testid="ws-new-project-cancel" onClick={() => setOpen(false)}>Cancel</button>
      {state.why && <span className="hint" data-testid="ws-new-project-problem">{state.why}</span>}
      <ErrorBanner error={create.error} />
    </form>
  )
}

/** The projects this Workspace server hosts and the workspaces in each: make one, abandon yours, read what happened to it. */
export function WorkspacesPage() {
  const projects = useQuery({ queryKey: ['workspace', 'projects'], queryFn: workspaceApi.projects })
  const canWrite = useIsAdmin()
  return (
    <WorkspaceShell crumbs={[{ label: 'Workspaces', heading: true }]}>
      <div className="pane" data-testid="ws-projects">
        <div className="page-head">
          <h1 className="page-title">Workspaces</h1>
          <div className="row" style={{ gap: 10, alignItems: 'center' }}><NewProject canWrite={canWrite} /></div>
        </div>
        <ErrorBanner error={projects.error} />
        {projects.isLoading && <div className="empty" data-testid="ws-projects-loading">Loading...</div>}
        {projects.data?.length === 0 && <div className="empty" data-testid="ws-projects-empty">This server hosts no project yet.{canWrite ? ' Make one with New project.' : ''}</div>}
        {(projects.data ?? []).map((p) => <ProjectCard key={p.id} project={p} canWrite={canWrite} />)}
      </div>
    </WorkspaceShell>
  )
}
