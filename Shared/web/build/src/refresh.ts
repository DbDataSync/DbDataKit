import { ApiError } from '@dbdatakit/shell'
import type { CommandAnswer, RefreshPlanEntry, RefreshResult } from './types'

/** What a person chose for one run of a refresh plan (plan 33 UE09). */
export interface RefreshChoice {
  connection: string
  plan: string
  /** Model names and selectors (`tag:`, `+model`, `load:name` ...), blank-separated or one per line; none runs the whole plan. */
  selectors: string
  onlyFailed: boolean
  /** One `[model.]name=value` per line. */
  inputs: string
  dryRun: boolean
}

export const splitWords = (text: string): string[] => text.split(/[\s,]+/).map((w) => w.trim()).filter((w) => w !== '')
export const splitLines = (text: string): string[] => text.split('\n').map((l) => l.trim()).filter((l) => l !== '')

/** The first line of an inputs box that is not `name=value`, or null when every line is. */
export function badInput(text: string): string | null {
  return splitLines(text).find((l) => !/^([A-Za-z_][\w.]*)=.*$/.test(l)) ?? null
}

/** The body of `connection_refresh` for a choice: only what was chosen is sent (the server sets where the checks are recorded). */
export function refreshBody(c: RefreshChoice): Record<string, unknown> {
  const selectors = splitWords(c.selectors)
  const inputs = splitLines(c.inputs)
  return {
    ...(c.connection ? { connection: c.connection } : {}),
    ...(c.plan ? { plan: c.plan } : {}),
    ...(selectors.length > 0 ? { selectors } : {}),
    ...(c.onlyFailed ? { only_failed: true } : {}),
    ...(inputs.length > 0 ? { input: inputs } : {}),
    ...(c.dryRun ? { dry_run: true } : {}),
  }
}

/** The same choice without its dry-run flag: a real run is offered after a dry run of exactly this. */
export const choiceKey = (c: RefreshChoice): string => JSON.stringify(refreshBody({ ...c, dryRun: false }))

/** Why the buttons are off, in words; null when a dry run may be asked. */
export function dryRunBlock(c: RefreshChoice, allowed: boolean | undefined, busy: boolean): string | null {
  if (allowed === false) return 'Running a refresh needs the operator role on this server.'
  if (busy) return 'A run is under way. The server answers when it has ended.'
  if (c.plan === '') return 'Choose a refresh plan first.'
  const bad = badInput(c.inputs)
  if (bad) return `"${bad}" is not an input: write name=value, or model.name=value, one per line.`
  return null
}

/** Why the real run is off: a dry run of the same choice comes first (it checks everything and executes nothing), and it must have passed. */
export function runBlock(c: RefreshChoice, allowed: boolean | undefined, busy: boolean, dry: { key: string; ok: boolean } | null): string | null {
  const first = dryRunBlock(c, allowed, busy)
  if (first) return first
  if (dry === null || dry.key !== choiceKey(c)) return 'Run a dry run of this choice first. It checks everything and executes nothing.'
  if (!dry.ok) return 'The dry run did not pass; fix what it says, then dry-run again.'
  return null
}

/** What a refusal or failure of the call says, in words: the role, the server's own sentence, or that it did not answer. */
export function refreshFailure(error: unknown): string {
  if (error instanceof ApiError) return error.status === 403 ? `Refused: ${error.message}` : error.message
  return 'The server did not answer. Try again; if a run was under way, look at the plan on the Status tab before running it again.'
}

export interface RefreshSummary {
  headline: string
  tone: 'ok' | 'warn' | 'bad'
  done: number
  failed: number
  skipped: number
  notRun: number
}

/** The one line a run comes to: how it ended and how many loads ended each way. A dry run says it executed nothing. */
export function summarize(answer: CommandAnswer<RefreshResult>): RefreshSummary | null {
  const data = answer.document?.data
  if (!data) return null
  const results = data.results ?? []
  const count = (s: string) => results.filter((r) => r.state === s).length
  const [done, failed, skipped, notRun] = [count('done'), count('failed'), count('skipped'), count('not run')]
  if (data.dry_run) return { headline: answer.exit === 0 ? 'The dry run passed every check. Nothing was executed.' : 'The dry run found problems. Nothing was executed.', tone: answer.exit === 0 ? 'ok' : 'bad', done, failed, skipped, notRun }
  const outcome = (data.outcome ?? (answer.exit === 0 ? 'completed' : 'failed')).replace(/_/g, ' ')
  const tone = data.outcome === 'completed' ? 'ok' : data.outcome === 'completed_with_failures' ? 'warn' : answer.exit === 0 ? 'ok' : 'bad'
  return { headline: `The refresh ${outcome}.`, tone, done, failed, skipped, notRun }
}

/** The plans of one connection, for the chooser; `connection` empty offers every connection's. */
export const plansOf = (plans: RefreshPlanEntry[], connection: string): RefreshPlanEntry[] => plans.filter((p) => connection === '' || p.connection === connection)

/** A number as a check result shows it. */
export const measure = (value: number | null | undefined): string => (value === null || value === undefined ? '-' : String(value))
