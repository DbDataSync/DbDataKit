import type { InstanceLabel, RunnerInstance } from './api'

/** The most labels an administrator puts on one instance (the runner refuses more). */
export const MAX_ADMIN_LABELS = 32

/** A label is written `name` or `name:value`: letters, digits and . _ : = - with no spaces, at most 64, starting with a letter or digit (the runner's rule, `RunnerGroupApi.IsLabel`). */
const LABEL_RULE = /^[A-Za-z0-9][A-Za-z0-9_.:=-]{0,63}$/

/** Trims the text and removes the spaces around a colon, as the runner does (`network: outside` and `network:outside` are one label). */
export function normalizeLabel(text: string): string {
  const t = text.trim()
  const colon = t.indexOf(':')
  return colon < 0 ? t : `${t.slice(0, colon).trim()}:${t.slice(colon + 1).trim()}`
}

/** The labels an administrator set on the instance, in the order the runner lists them. */
export const adminLabels = (instance: Pick<RunnerInstance, 'labels'>): string[] => instance.labels.filter((l) => l.source === 'admin').map((l) => l.label)

/** The labels the machine reports about itself: read-only. */
export const reportedLabels = (instance: Pick<RunnerInstance, 'labels'>): string[] => instance.labels.filter((l) => l.source !== 'admin').map((l) => l.label)

/** What is wrong with a label about to be added, in words; null when it can be added. The runner checks again. */
export function labelProblem(text: string, draft: string[], reported: string[]): string | null {
  const label = normalizeLabel(text)
  if (label === '') return null
  if (!LABEL_RULE.test(label)) return `"${text.trim()}" is not a label: letters, digits and . _ : = - with no spaces, at most 64, such as gpu or site:oslo.`
  if (reported.includes(label)) return `"${label}" is reported by the runner itself and cannot be set by hand.`
  if (draft.includes(label)) return `The runner has "${label}" in this list already.`
  if (draft.length >= MAX_ADMIN_LABELS) return `An instance takes at most ${MAX_ADMIN_LABELS} labels set by an administrator.`
  return null
}

/** The draft with the typed label added (sorted, as the runner keeps them); the draft unchanged when the text is empty or not addable. */
export function withLabel(draft: string[], text: string, reported: string[]): string[] {
  const label = normalizeLabel(text)
  return label === '' || labelProblem(text, draft, reported) !== null ? draft : [...draft, label].sort()
}

export const withoutLabel = (draft: string[], label: string): string[] => draft.filter((l) => l !== label)

/** Whether the draft is not what the instance has now. */
export const labelsChanged = (instance: Pick<RunnerInstance, 'labels'>, draft: string[]): boolean => {
  const now = [...adminLabels(instance)].sort()
  return now.length !== draft.length || now.some((l, i) => l !== [...draft].sort()[i])
}

/** Why Save cannot be pressed, in words; null when it can. */
export function labelsBlock(canEdit: boolean, instance: Pick<RunnerInstance, 'labels' | 'live'>, draft: string[], typed: string, reported: string[]): string | null {
  if (!canEdit) return 'Changing labels needs the admin role on this server.'
  if (instance.live === false) return 'This runner is gone: it has no labels to change.'
  const problem = labelProblem(typed, draft, reported)
  if (problem) return problem
  if (normalizeLabel(typed) !== '') return 'Add the label you typed to the list first (Add), or clear it.'
  if (!labelsChanged(instance, draft)) return 'Add or remove a label first.'
  return null
}

/** The body of the call: the administrator's labels the instance should have, as a whole. */
export const labelsRequest = (draft: string[]) => ({ labels: [...draft].sort() })

/** What a task that waits for a label needs to be told: which labels no live runner has. */
export function missingLabels(required: string[], instances: Pick<RunnerInstance, 'labels' | 'live'>[]): string[] {
  const held = new Set(instances.filter((i) => i.live !== false).flatMap((i) => i.labels.map((l: InstanceLabel) => l.label)))
  return required.filter((r) => !held.has(r))
}
