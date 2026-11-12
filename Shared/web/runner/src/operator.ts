import type { DeadLetterRecord, InDoubtStep, PostureChoiceRecord, PostureSettingInfo, RunUnit, UnitsSummary } from './api'

/** How a unit's state reads on screen. */
export const unitStateText = (state: string): string =>
  ({ started: 'running', completed: 'done', failed: 'failed', skipped: 'not run (the run stopped)' })[state] ?? state

/** A unit's duration: `120 ms`, `3.4 s`, `2 min`; empty while it runs. */
export function durationText(ms: number | null): string {
  if (ms === null) return ''
  return ms < 1000 ? `${ms} ms` : ms < 60_000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.round(ms / 60_000)} min`
}

/** The summary of a run's units as one line: `3 units: 1 done, 1 running, 1 failed`. */
export function unitsLine(s: UnitsSummary): string {
  if (s.total === 0) return 'The run kept no units.'
  const parts = [`${s.done} done`, ...(s.running > 0 ? [`${s.running} running`] : []), ...(s.failed > 0 ? [`${s.failed} failed`] : []), ...(s.skipped > 0 ? [`${s.skipped} not run`] : [])]
  return `${s.total} unit${s.total === 1 ? '' : 's'}: ${parts.join(', ')}`
}

/** Failed first, then running, then the rest in the order they started. */
export function sortUnits(units: RunUnit[]): RunUnit[] {
  const rank = (u: RunUnit) => (u.state === 'failed' ? 0 : u.state === 'started' ? 1 : 2)
  return units.map((u, i) => ({ u, i })).sort((a, b) => rank(a.u) - rank(b.u) || a.i - b.i).map((x) => x.u)
}

export const stepLabel = (step: InDoubtStep) => (step.key === '' ? step.name : `${step.name} (${step.key})`)

export interface StepForm { happened: boolean; result: string; retry: boolean }
export const emptyStepForm: StepForm = { happened: false, result: '', retry: true }

/** Parses what the operator typed as the result of a step that happened: JSON, empty meaning no value. */
export function parseStepResult(text: string): { ok: true; value: unknown } | { ok: false; problem: string } {
  const trimmed = text.trim()
  if (trimmed === '') return { ok: true, value: null }
  try { return { ok: true, value: JSON.parse(trimmed) } } catch { return { ok: false, problem: 'The result is not JSON: write a value such as 42, "text" or {"id": 7}.' } }
}

/** Why the answer cannot be sent yet, or null. */
export function stepBlock(form: StepForm): string | null {
  if (!form.happened) return null
  const parsed = parseStepResult(form.result)
  return parsed.ok ? null : parsed.problem
}

export const deadLetterText = (d: DeadLetterRecord) => `${d.source}: ${d.reason}`

export const deadLetterReplayHint = (d: DeadLetterRecord) =>
  d.has_body ? "Replaying puts the record back in the task's inbox once and queues a run to take it." : 'This record kept no body, so there is nothing to send again: discard it.'

/** The choice that stands for a setting and a scope, if any. */
export const choiceFor = (choices: PostureChoiceRecord[], setting: string, scope: string) => choices.find((c) => c.setting === setting && c.scope === scope) ?? null

/** Settings an administrator can choose here: the choice of a production binding in a non-production channel is made where the binding is made. */
export const choosableSettings = (settings: PostureSettingInfo[]) => settings.filter((s) => s.name !== 'production_binding_on_non_production')

export interface PostureForm { setting: string; scope: string; note: string; confirmed: boolean }
export const emptyPostureForm = (settings: PostureSettingInfo[]): PostureForm => ({ setting: choosableSettings(settings)[0]?.name ?? '', scope: '', note: '', confirmed: false })

/** Why the choice cannot be recorded yet, or null: a package (or `*`) is named and the effect is confirmed. */
export function postureBlock(form: PostureForm): string | null {
  if (form.setting === '') return 'Pick the default to loosen.'
  if (form.scope.trim() === '') return 'Name the package, or * for every package.'
  if (!/^(\*|[a-z0-9][a-z0-9._-]*)$/.test(form.scope.trim())) return 'A package name is lower case letters, digits, dots, dashes and underscores.'
  if (!form.confirmed) return 'Confirm that you accept the effect shown.'
  return null
}
