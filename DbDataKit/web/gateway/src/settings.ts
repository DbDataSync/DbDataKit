import type { Diagnostic, SettingsCheck, SettingsEdit, SettingsFields } from './types'

/** The modules of the serving layer a Gateway instance can serve (`GatewayModules.Known`). */
export const MODULES = ['flightsql', 'identity', 'apps', 'store', 'views'] as const

/** The fields of the form, by the setting path the Gateway edits and reports them under. */
export type FieldKey = 'modules' | 'views.cacheSeconds' | 'views.export.mode' | 'views.export.path' | 'logSql'

/** The form field a diagnostic's key (`Views.CacheSeconds`, `Modules[1]`) belongs to, or null when it is not one of the form's: it is then listed with the file. */
export function fieldOf(diagnostic: Pick<Diagnostic, 'key'>): FieldKey | null {
  const key = (diagnostic.key ?? '').replace(/\[\d+\]/g, '').toLowerCase()
  const known: FieldKey[] = ['modules', 'views.cacheSeconds', 'views.export.mode', 'views.export.path', 'logSql']
  return known.find((f) => f.toLowerCase() === key) ?? null
}

export interface Grouped { byField: Partial<Record<FieldKey, Diagnostic[]>>; other: Diagnostic[]; errors: number }

/** The validator's diagnostics by form field, the rest in the order they came, and how many are errors (a warning does not stop a save). */
export function groupDiagnostics(diagnostics: Diagnostic[]): Grouped {
  const grouped: Grouped = { byField: {}, other: [], errors: diagnostics.filter((d) => d.severity === 'error').length }
  for (const d of diagnostics) {
    const field = fieldOf(d)
    if (field) (grouped.byField[field] ??= []).push(d)
    else grouped.other.push(d)
  }
  return grouped
}

/** Where a diagnostic is in the text: `server.yaml:12`. */
export const placeOf = (d: Pick<Diagnostic, 'file' | 'line'>) => `${d.file.split(/[\\/]/).pop()}${d.line > 0 ? `:${d.line}` : ''}`

export interface SaveState { enabled: boolean; why: string | null }

/** Whether Save is on and, when it is not, why (said beside the button, never only by being grey). */
export function saveState(p: { allowed: boolean; dirty: boolean; checking: boolean; check: SettingsCheck | undefined; checkFailed: boolean; busy: boolean }): SaveState {
  if (!p.allowed) return { enabled: false, why: 'Changing the settings needs the admin role.' }
  if (p.busy) return { enabled: false, why: 'Saving…' }
  if (!p.dirty) return { enabled: false, why: 'Nothing is changed yet.' }
  if (p.checkFailed) return { enabled: false, why: 'The Gateway could not check this text, so it is not saved.' }
  if (p.checking || !p.check) return { enabled: false, why: 'Checking the text…' }
  const errors = p.check.diagnostics.filter((d) => d.severity === 'error').length + p.check.problems.length
  if (!p.check.valid || errors > 0) return { enabled: false, why: errors === 1 ? 'Fix the 1 problem first.' : `Fix the ${Math.max(errors, 1)} problems first.` }
  return { enabled: true, why: null }
}

/** What the person is told about a restart after a save, or after a check of a draft. */
export function restartText(keys: string[], saved: boolean): string | null {
  if (keys.length === 0) return saved ? 'Saved. A running Gateway has read it; nothing here needs a restart.' : null
  const list = keys.join(', ')
  return saved ? `Saved, but a running Gateway reads ${list} only when it starts: restart the Gateway for ${keys.length === 1 ? 'it' : 'them'} to take effect.` : `${list} ${keys.length === 1 ? 'is' : 'are'} read when the Gateway starts: saving this needs a restart to take effect.`
}

/** The module list with one module switched, in the order of `MODULES` (the file's own order is kept when nothing changes it). */
export function toggleModule(current: string[], module: string, on: boolean): string[] {
  const without = current.filter((m) => m !== module)
  return on ? [...MODULES.filter((m) => m === module || without.includes(m)), ...without.filter((m) => !(MODULES as readonly string[]).includes(m))] : without
}

/** The edit the Gateway makes to the draft for a change of one field (it edits the text, so the form and the YAML are one document). */
export function editFor(field: FieldKey, value: string | number | boolean | string[]): SettingsEdit {
  if (field === 'modules') return { list: { modules: value as string[] } }
  if (field === 'views.export.mode' || field === 'views.export.path') return value === '' ? { remove: [field] } : { set: { [field]: value as string } }
  return { set: { [field]: value as number | boolean } }
}

/** The number a field holds, or null when what was typed is not a whole number in its range (the Gateway checks again). */
export function cacheSeconds(text: string): number | null {
  if (!/^\d{1,4}$/.test(text.trim())) return null
  const n = Number(text.trim())
  return n >= 0 && n <= 3600 ? n : null
}

/** The values the form shows: what the draft sets, as the Gateway reads it. */
export const shown = (fields: SettingsFields | undefined) => ({
  modules: fields?.modules ?? [],
  cacheSeconds: fields?.['views.cacheSeconds'] ?? 30,
  exportMode: fields?.['views.export.mode'] ?? '',
  exportPath: fields?.['views.export.path'] ?? '',
  logSql: fields?.logSql ?? true,
})
