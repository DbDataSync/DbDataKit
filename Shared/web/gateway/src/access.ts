import type { AccessColumnRule, AccessCoverageRule, AccessRule, AccessState, AccessTable, ExplainForm } from './accessTypes'

/** The words of the access rules screen (plan 22a GP15); no view logic, so a test can read them. */

export function stateLabel(state: AccessState): string {
  switch (state) {
    case 'fresh': return 'Fresh'
    case 'stale': return 'Stale: the last good rules are served'
    case 'expired': return 'Expired: the datasets it filters are refused to whoever has rules in it'
  }
}

export function durationText(seconds: number): string {
  if (seconds < 120) return `${seconds} s`
  if (seconds < 7200) return `${Math.round(seconds / 60)} min`
  if (seconds < 172800) return `${Math.round(seconds / 3600)} h`
  return `${Math.round(seconds / 86400)} days`
}

/** How old a rules table is and when it was last refreshed, in words. A table no producer has reported counts from the day it was first served. */
export function tableAgeText(t: Pick<AccessTable, 'age_seconds' | 'max_age_seconds' | 'last_changed'>): string {
  const refreshed = t.last_changed ? `last refreshed ${new Date(t.last_changed).toISOString().replace('T', ' ').slice(0, 16)} UTC` : 'no refresh reported yet, counted from the day it was first served'
  return `${durationText(t.age_seconds)} old, at most ${durationText(t.max_age_seconds)}; ${refreshed}`
}

/** A rule in one sentence: what it includes or removes and where the people are named. */
export function ruleText(rule: Pick<AccessRule, 'effect' | 'table' | 'subject' | 'subject_kind' | 'columns'>): string {
  const pairs = rule.columns.map((c) => `${c.dataset_column} = ${c.rules_column}`).join(' and ')
  return `${rule.effect === 'include' ? 'Include' : 'Remove'} the rows whose ${pairs} appear in ${rule.table} for the ${rule.subject_kind} named in ${rule.subject}`
}

export function columnRuleText(rule: AccessColumnRule): string {
  const how = rule.treatment === 'mask' ? `mask (${rule.mask ?? 'fixed'})` : rule.treatment
  return `${rule.subject_kind === 'group' ? 'Group' : 'Person'} ${rule.subject}: ${how}`
}

export function valueText(value: string | null): string {
  return value === null ? 'NULL' : value
}

export function valuesText(values: Record<string, string | null>): string {
  return Object.entries(values).map(([k, v]) => `${k} = ${valueText(v)}`).join(', ')
}

/** What the explain query string carries for a form: only the filled row pairs. */
export function explainQuery(model: string, form: ExplainForm): Record<string, string | string[]> {
  const row = form.row.filter((r) => r.column.trim() !== '').map((r) => `${r.column.trim()}=${r.value}`)
  return { subject: form.subject.trim(), kind: form.kind, model, ...(form.dataset ? { dataset: form.dataset } : {}), ...(row.length > 0 ? { row } : {}) }
}

/** Whether the form can be sent: a subject, and a dataset when a row is given (a row is asked about one dataset). */
export function explainReady(form: ExplainForm): boolean {
  return form.subject.trim() !== '' && (form.dataset !== '' || form.row.every((r) => r.column.trim() === ''))
}

/** The coverage of a rule in a line: how many combinations of values no row of the rules table has. */
export function coverageText(rule: Pick<AccessCoverageRule, 'uncovered' | 'problem' | 'effect' | 'table'>): string {
  if (rule.problem) return `Not checked: ${rule.problem}`
  if (rule.uncovered === 0) return `Every value is covered by a row of ${rule.table}.`
  const who = rule.effect === 'include' ? 'nobody this rule would include reads' : 'this rule removes nothing from'
  return `${rule.uncovered} ${rule.uncovered === 1 ? 'combination of values has' : 'combinations of values have'} no row in ${rule.table}: ${who} those rows.`
}

/** The total of uncovered combinations over a coverage report, for the badge on the definition. */
export function uncoveredTotal(rules: Pick<AccessCoverageRule, 'uncovered' | 'effect'>[]): number {
  return rules.filter((r) => r.effect === 'include').reduce((n, r) => n + r.uncovered, 0)
}

export const readsOnlyWarning = 'Row rules limit reads only, not writes'

/** Whether a warning is the one the owner requires to be seen: row rules of a Store entity limit reads only (plan 22a QR6). */
export function isReadsOnly(warning: string): boolean {
  return warning.startsWith(readsOnlyWarning)
}

export const rotateWarning = 'Rotating the key changes every hash mask: a value masked before no longer matches the same value masked after, and the result cache of this instance is cleared.'
