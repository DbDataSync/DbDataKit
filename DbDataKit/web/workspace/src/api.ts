import { request } from '@dbdatakit/shell'

/** A project the server hosts (`GET /api/v1/projects`): its kind (an app today) and how many workspaces it has now. */
export interface ProjectSummary { id: string; kind: string; title: string | null; workspaces: number; created: string }

/** A workspace: one person's draft of a project. `mine` is whether the signed-in person owns it, and only they abandon it. */
export interface WorkspaceSummary { id: string; name: string; owner: string; mine: boolean; branch: string; base: string; created: string }

/** One thing that happened to a workspace. */
export interface WorkspaceEvent { kind: string; actor: string; detail: string | null; at: string }

const enc = encodeURIComponent
const json = (body: unknown): RequestInit => ({ method: 'POST', body: JSON.stringify(body), headers: { 'Content-Type': 'application/json' } })

/** The Workspace server's calls through the console's relay, as the signed-in person; the server checks the role on every call. */
export const workspaceApi = {
  projects: () => request<{ projects: ProjectSummary[] }>('/api/v1/projects').then((r) => r.projects),
  createProject: (id: string, title: string | null) => request<ProjectSummary>('/api/v1/projects', json(title ? { id, title } : { id })),
  workspaces: (project: string) => request<{ workspaces: WorkspaceSummary[] }>(`/api/v1/projects/${enc(project)}/workspaces`).then((r) => r.workspaces),
  createWorkspace: (project: string, name: string) => request<WorkspaceSummary>(`/api/v1/projects/${enc(project)}/workspaces`, json({ name })),
  abandon: (project: string, id: string) => request<void>(`/api/v1/projects/${enc(project)}/workspaces/${enc(id)}`, { method: 'DELETE' }),
  events: (project: string, id: string) => request<{ events: WorkspaceEvent[] }>(`/api/v1/projects/${enc(project)}/workspaces/${enc(id)}/events`).then((r) => r.events),
}
