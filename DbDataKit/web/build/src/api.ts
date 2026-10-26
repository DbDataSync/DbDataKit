import { request } from '@dbdatakit/shell'
import type { BuildProject, CommandAnswer, CompileSummary, ProjectGraph } from './types'

const project = (id: string) => `/api/v1/projects/${encodeURIComponent(id)}`

/** Runs one command of a project on the server and returns what it answered. A command that reads is run as a viewer; the server decides what the person may run. */
function run<T>(id: string, tool: string, inputs: Record<string, unknown> = {}) {
  return request<CommandAnswer<T>>(`${project(id)}/commands/${tool}`, { method: 'POST', body: JSON.stringify(inputs) })
}

export const buildApi = {
  projects: () => request<{ projects: BuildProject[] }>('/api/v1/projects').then((r) => r.projects),
  graph: (id: string) => run<ProjectGraph>(id, 'project_show_graph'),
  /** Checks the project without writing anything: compiles it and says what would change. */
  check: (id: string) => run<CompileSummary>(id, 'project_compile', { check: true }),
}
