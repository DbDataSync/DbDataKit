import type { OwnerBody, TaskDecision, TaskSchedule } from './api'

export type Owner = 'old' | 'observe' | 'runner'
export const OWNERS: Owner[] = ['old', 'observe', 'runner']

/** Who schedules a task, in words. `old` is the product that published the task (a Build server's scheduler, for a refresh plan), which keeps scheduling it until it is cut over. */
export const ownerText: Record<Owner, string> = {
  old: 'The publishing product',
  observe: 'The publishing product, observed here',
  runner: 'This runner',
}

export const ownerLabel = (owner: string | undefined): string => (owner && owner in ownerText ? ownerText[owner as Owner] : owner ? owner : 'This runner')

/** A task of a runner that does not report an owner is scheduled by the runner, as before ownership existed. */
export const effectiveOwner = (owner: string | undefined): Owner => (owner === 'old' || owner === 'observe' ? owner : 'runner')

export interface OwnerForm {
  owner: Owner
  reason: string
  /** A time (what the browser's datetime-local gives, or an ISO time): the position of the old scheduler, up to which its times are dealt with. */
  handledUpTo: string
}

/** The time as the runner reads it (`2026-11-07T12:00:00Z`), from what a person typed or the runner gave; null when it is not a time. */
export function isoOf(text: string): string | null {
  const trimmed = text.trim()
  if (trimmed === '') return null
  const withZone = /(Z|[+-]\d\d:?\d\d)$/.test(trimmed) ? trimmed : `${trimmed}Z`
  const ms = Date.parse(withZone)
  return Number.isNaN(ms) ? null : new Date(ms).toISOString()
}

/** The form a task opens with: the move the next step usually is (the publishing product observed, then cut over), and the position the runner already has. */
export function openOwnerForm(current: Owner, schedule: TaskSchedule | undefined): OwnerForm {
  const next: Owner = current === 'old' ? 'observe' : current === 'observe' ? 'runner' : 'old'
  return { owner: next, reason: '', handledUpTo: schedule?.handled_up_to ?? '' }
}

/** Why a move cannot be sent yet, in words; null when it can. The runner decides again (the operator role, the position). */
export function ownerBlock(current: Owner, form: OwnerForm): string | null {
  if (form.owner === current) return `The task is already ${current === 'runner' ? "this runner's" : current === 'observe' ? 'observed' : "the publishing product's"}.`
  if (form.reason.trim() === '') return 'Say why: the reason is recorded with who moved it.'
  if (form.owner === 'runner' && current !== 'runner') {
    if (form.handledUpTo.trim() === '') return 'Give the time up to which the publishing product has dealt with: this runner takes the schedule from there, so no time is skipped or run twice.'
    if (isoOf(form.handledUpTo) === null) return 'The position is not a time (for example 2026-11-08T12:00:00Z).'
  }
  return null
}

/** The body of the owner call for a form, or null while `ownerBlock` has something to say. */
export function ownerBody(current: Owner, form: OwnerForm): OwnerBody | null {
  if (ownerBlock(current, form) !== null) return null
  const body: OwnerBody = { owner: form.owner, reason: form.reason.trim() }
  if (form.owner === 'runner' && current !== 'runner') body.handled_up_to = isoOf(form.handledUpTo)!
  return body
}

/** The `name=value` lines of the run-now box as the inputs of a run: the first line that is not one is named, nothing is sent half-read. A name is given once. */
export function parseInputs(text: string): { inputs: Record<string, string>; problem: string | null } {
  const inputs: Record<string, string> = {}
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim()
    if (line === '') continue
    const eq = line.indexOf('=')
    if (eq <= 0) return { inputs: {}, problem: `"${line}" is not name=value.` }
    const name = line.slice(0, eq).trim()
    if (!/^[A-Za-z_][A-Za-z0-9_.-]*$/.test(name)) return { inputs: {}, problem: `"${name}" is not an input name.` }
    if (name in inputs) return { inputs: {}, problem: `"${name}" is given twice.` }
    inputs[name] = line.slice(eq + 1).trim()
  }
  return { inputs, problem: null }
}

/** What an observed task's decisions come to: how many times this runner would have queued, how many the product did, and what is not matched within the tolerance (the same pairing the Build server's `compare` makes, here a plain count for a first look). */
export function decisionSummary(decisions: TaskDecision[], toleranceMs = 90_000): { runner: number; old: number; unmatched: number } {
  const runner = decisions.filter((d) => d.source === 'runner').sort((a, b) => Date.parse(a.at) - Date.parse(b.at))
  const old = decisions.filter((d) => d.source === 'old').sort((a, b) => Date.parse(a.at) - Date.parse(b.at))
  const pool = [...old]
  let unmatched = 0
  for (const d of runner) {
    const i = pool.findIndex((o) => o.paused === d.paused && Math.abs(Date.parse(o.at) - Date.parse(d.at)) <= toleranceMs)
    if (i < 0) unmatched++
    else pool.splice(i, 1)
  }
  return { runner: runner.length, old: old.length, unmatched: unmatched + pool.length }
}
