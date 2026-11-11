import type { SemanticModelRow } from './types'

export interface ServedLabel { text: string; tone: 'ok' | 'warn' | 'bad' }

/** Where a model stands on this instance: serving the group's current version, still on an earlier one (the instance looks again within a poll), or unable to load the new one (the failure is shown). */
export function servedLabel(row: { current_version: number | null; served_version: number | null; failed_version: number | null; failure: string | null }): ServedLabel {
  if (row.failed_version !== null) return { text: `version ${row.failed_version} did not load${row.served_version !== null ? `; still serving ${row.served_version}` : '; nothing is served'}${row.failure ? `: ${row.failure}` : ''}`, tone: 'bad' }
  if (row.served_version === null) return { text: 'not served on this instance yet', tone: 'warn' }
  if (row.served_version !== row.current_version) return { text: `serving ${row.served_version}; the group is on ${row.current_version}, this instance catches up at its next look`, tone: 'warn' }
  return { text: `serving version ${row.served_version}`, tone: 'ok' }
}

export const sortModels = <T extends { name: string }>(rows: T[]): T[] => [...rows].sort((a, b) => a.name.localeCompare(b.name))

/** Why "serve this version" is off for a row, or null. */
export function serveBlock(row: SemanticModelRow, busy: boolean): string | null {
  if (busy) return 'Another change is under way.'
  if (row.versions < 2) return 'There is no earlier version to go back to.'
  return null
}

export const removeWarning = (kind: 'semantic' | 'entity', name: string): string =>
  kind === 'semantic'
    ? `Stops serving ${name} here and on every instance of the group. Its versions are removed with it; a dashboard or query that reads it stops working until it is published again.`
    : `Stops serving ${name} here and on every instance of the group. The Store still holds the model and its data; publish it again to serve it.`

export const ownerLabel = (managedBy: string | null): string => (managedBy ? `published by a paired server (${managedBy})` : 'made by an administrator')
