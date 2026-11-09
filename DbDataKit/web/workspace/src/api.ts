import { request } from '@dbdatakit/shell'

/** A project the server hosts (`GET /api/v1/projects`): its kind (an app today) and how many workspaces it has now. */
export interface ProjectSummary { id: string; kind: string; title: string | null; workspaces: number; created: string }

/** A workspace: one person's draft of a project. `mine` is whether the signed-in person owns it, and only they abandon it. */
export interface WorkspaceSummary { id: string; name: string; owner: string; mine: boolean; branch: string; base: string; created: string }

/** One thing that happened to a workspace. */
export interface WorkspaceEvent { kind: string; actor: string; detail: string | null; at: string }

/** A file of a draft (`GET .../files`): its path (`/` between the parts) and its size in bytes. */
export interface WorkspaceFile { path: string; size: number }
export interface FileList { files: WorkspaceFile[]; truncated: boolean; max_file_bytes: number }

/** One file that differs from where the draft started (`GET .../changes`): both texts when `sides`, otherwise only the patch (a binary or very large file). */
export interface Change { path: string; status: string; patch: string; before: string | null; after: string | null; sides: boolean }
export interface Changes { changes: Change[]; uncommitted: boolean }
export interface Commit { sha: string; message: string; author: string; when: string }
export interface Pushed { pushed: boolean; branch: string; pull_request_url: string | null }
export interface Merged { merged: boolean; sha: string | null; fast_forward: boolean }

const enc = encodeURIComponent
/** A file's path as it goes in an address: each part escaped, the `/` between them kept. */
export const filePath = (path: string) => path.split('/').map(enc).join('/')
const ws = (project: string, id: string) => `/api/v1/projects/${enc(project)}/workspaces/${enc(id)}`
const json = (body: unknown): RequestInit => ({ method: 'POST', body: JSON.stringify(body), headers: { 'Content-Type': 'application/json' } })

/** The Workspace server's calls through the console's relay, as the signed-in person; the server checks the role on every call. */
export const workspaceApi = {
  projects: () => request<{ projects: ProjectSummary[] }>('/api/v1/projects').then((r) => r.projects),
  createProject: (id: string, title: string | null) => request<ProjectSummary>('/api/v1/projects', json(title ? { id, title } : { id })),
  workspaces: (project: string) => request<{ workspaces: WorkspaceSummary[] }>(`/api/v1/projects/${enc(project)}/workspaces`).then((r) => r.workspaces),
  createWorkspace: (project: string, name: string) => request<WorkspaceSummary>(`/api/v1/projects/${enc(project)}/workspaces`, json({ name })),
  abandon: (project: string, id: string) => request<void>(`/api/v1/projects/${enc(project)}/workspaces/${enc(id)}`, { method: 'DELETE' }),
  files: (project: string, id: string) => request<FileList>(`${ws(project, id)}/files`),
  readFile: (project: string, id: string, path: string) => request<{ path: string; content: string }>(`${ws(project, id)}/files/${filePath(path)}`).then((r) => r.content),
  writeFile: (project: string, id: string, path: string, content: string) => request<{ path: string; created: boolean; size: number }>(`${ws(project, id)}/files/${filePath(path)}`, { method: 'PUT', body: JSON.stringify({ content }) }),
  deleteFile: (project: string, id: string, path: string) => request<void>(`${ws(project, id)}/files/${filePath(path)}`, { method: 'DELETE' }),
  moveFile: (project: string, id: string, from: string, to: string) => request<{ from: string; to: string }>(`${ws(project, id)}/move`, json({ from, to })),
  changes: (project: string, id: string) => request<Changes>(`${ws(project, id)}/changes`),
  history: (project: string, id: string) => request<{ commits: Commit[] }>(`${ws(project, id)}/history`).then((r) => r.commits),
  commit: (project: string, id: string, message: string) => request<{ committed: boolean; sha: string | null }>(`${ws(project, id)}/commit`, json({ message })),
  push: (project: string, id: string) => request<Pushed>(`${ws(project, id)}/push`, { method: 'POST' }),
  merge: (project: string, id: string) => request<Merged>(`${ws(project, id)}/merge`, { method: 'POST' }),
  events: (project: string, id: string) => request<{ events: WorkspaceEvent[] }>(`/api/v1/projects/${enc(project)}/workspaces/${enc(id)}/events`).then((r) => r.events),
}
