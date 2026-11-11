import { parseYaml } from '@dbdatakit/shell'
import type { CheckDiagnostic } from './api'

/** One model of a Build project: its stem and folder, and the files that make it (`orders.sql` and `orders.yml`). */
export interface ModelEntry { name: string; folder: string; files: string[] }

/** The models of a Build project from its file list: `models/<folder>/<name>.sql` with the `.yml` or `.yaml` that describes it, a file that is only a folder's setting (`_dbdatabuild.yml`) left out. */
export function modelsOf(paths: readonly string[]): ModelEntry[] {
  const byStem = new Map<string, ModelEntry>()
  for (const p of paths) {
    const m = /^models\/((?:[^/]+\/)*)([^/]+)\.(sql|ya?ml)$/i.exec(p)
    if (!m || m[2].startsWith('_')) continue
    const folder = m[1].replace(/\/$/, '')
    const key = `${folder}/${m[2]}`
    const entry = byStem.get(key) ?? { name: m[2], folder, files: [] }
    entry.files.push(p)
    byStem.set(key, entry)
  }
  return [...byStem.values()].sort((a, b) => (a.folder + '/' + a.name).localeCompare(b.folder + '/' + b.name))
}

/** The findings of a check that name one of a model's files: how many errors and warnings it has. */
export function findingsOfModel(model: ModelEntry, diagnostics: readonly CheckDiagnostic[]): { errors: number; warnings: number } {
  const own = diagnostics.filter((d) => d.file !== null && model.files.includes(d.file))
  return { errors: own.filter((d) => d.severity === 'error').length, warnings: own.filter((d) => d.severity === 'warning').length }
}

/** A connection's compiled plan: the files `project compile` wrote under `rendered/<connection>/` (the loads and each refresh plan), committed like code. */
export interface PlanConnection { connection: string; loads: string | null; plans: { name: string; path: string }[] }

export function planFilesOf(paths: readonly string[]): PlanConnection[] {
  const by = new Map<string, PlanConnection>()
  for (const p of paths) {
    const m = /^rendered\/([^/]+)\/(loads\.ya?ml|refresh\/([^/]+)\.ya?ml)$/i.exec(p)
    if (!m) continue
    const entry = by.get(m[1]) ?? { connection: m[1], loads: null, plans: [] }
    if (m[3]) entry.plans.push({ name: m[3], path: p })
    else entry.loads = p
    by.set(m[1], entry)
  }
  const all = [...by.values()].sort((a, b) => a.connection.localeCompare(b.connection))
  for (const c of all) c.plans.sort((a, b) => a.name.localeCompare(b.name))
  return all
}

/** What a plan file says in a line: how many members it has and what they are called, read loosely (`dbdatabuild project show refresh` is the authority); null when the text is not a plan. */
export function planSummary(text: string): { members: string[] } | null {
  const parsed = parseYaml(text)
  if (parsed.errors.length > 0 || typeof parsed.value !== 'object' || parsed.value === null) return null
  const value = parsed.value as Record<string, unknown>
  const list = Array.isArray(value.members) ? value.members : Array.isArray(value.loads) ? value.loads : null
  if (!list) return null
  return { members: list.map((m, i) => (typeof m === 'string' ? m : m && typeof m === 'object' ? String((m as Record<string, unknown>).model ?? (m as Record<string, unknown>).name ?? (m as Record<string, unknown>).load ?? i + 1) : String(i + 1))) }
}

export const planWord = (summary: { members: string[] } | null): string =>
  summary === null ? 'not a plan file' : `${summary.members.length} ${summary.members.length === 1 ? 'load' : 'loads'}`
