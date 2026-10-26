import { request } from '@dbdatakit/shell'
import type { BuildProject, CommandAnswer, CompareResult, CompileSummary, ConnectionStatus, DeployResult, PlanDetail, PlanEntry, ProjectGraph, SampleResult } from './types'

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
  /** Where a connection stands: what exists, what a deploy would do. Reads the target; the connection is the project's default when none is named. */
  status: (id: string, connection?: string) => run<ConnectionStatus>(id, 'connection_status', connection ? { connection } : {}),
  plans: (id: string) => run<{ plans: PlanEntry[] }>(id, 'project_show_plan'),
  plan: (id: string, path: string) => run<PlanDetail>(id, 'project_show_plan', { plan: path }),
  /** Plans a deploy and writes the plan file, then stops: nothing is applied. */
  writePlan: (id: string, connection?: string) => run<DeployResult>(id, 'connection_deploy', { ...(connection ? { connection } : {}), write_plan: true }),
  /** Runs every check of a written plan and prints its statements; executes nothing. */
  dryRun: (id: string, path: string, connection?: string) => run<DeployResult>(id, 'connection_deploy', { ...(connection ? { connection } : {}), apply_plan: path, dry_run: true }),
  /** Applies a written plan. Whoever holds the approver role and sends this is the approver; the server records them. */
  apply: (id: string, path: string, allowances: { risky: boolean; destructive: string[] }, connection?: string) =>
    run<DeployResult>(id, 'connection_deploy', {
      ...(connection ? { connection } : {}),
      apply_plan: path,
      yes: true,
      ...(allowances.risky ? { allow_risky: true } : {}),
      ...(allowances.destructive.length > 0 ? { allow_destructive: allowances.destructive } : {}),
    }),
  /** Compares two tables (of one connection, or of two) by counts and digests; the key defaults to the model's grain. */
  compare: (id: string, o: { connection?: string; table: string; against?: string; againstConnection?: string; key?: string[] }) =>
    run<CompareResult>(id, 'connection_compare', {
      ...(o.connection ? { connection: o.connection } : {}),
      table: o.table,
      ...(o.against ? { against: o.against } : {}),
      ...(o.againstConnection ? { against_connection: o.againstConnection } : {}),
      ...(o.key && o.key.length > 0 ? { key: o.key } : {}),
    }),
  /** Runs the models on generated rows, offline: what they would produce. */
  sample: (id: string, rows?: number) => run<SampleResult>(id, 'project_sample', rows ? { rows } : {}),
}
