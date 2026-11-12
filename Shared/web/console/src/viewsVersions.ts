import type { DocumentVersions, ViewKind } from '@dbdatakit/visuals'

/** One line of the versions table of a published document (plan 33, UE04): what to show and whether it may be served. */
export interface VersionRow {
  version: number
  who: string
  when: string
  note: string
  served: boolean
  /** Why the version cannot be served from here, or null when it can. */
  switchBlocked: string | null
}

/** The line shown for a time the server gave in ISO form; an unreadable one is shown as it came. */
export function whenText(iso: string): string {
  const t = Date.parse(iso)
  return Number.isNaN(t) ? iso : new Date(t).toISOString().replace('T', ' ').replace(/:\d\d(\.\d+)?Z$/, ' UTC')
}

/** The rows, newest first (the order the server gives, sorted again so a client of another order shows the same), and what each may do. `allowed` is whether the signed-in person may change what is served. */
export function versionRows(versions: DocumentVersions, allowed: boolean): VersionRow[] {
  return [...versions.versions].sort((a, b) => b.version - a.version).map((v) => ({
    version: v.version,
    who: v.publishedBy,
    when: whenText(v.publishedAt),
    note: v.note ?? '',
    served: versions.active === v.version,
    switchBlocked: !allowed ? 'Serving another version needs the author role.' : versions.active === v.version ? 'This version is served now.' : null,
  }))
}

/** Whether "roll back" can be offered: the registry remembers the version before the served one. */
export function rollbackState(versions: DocumentVersions, allowed: boolean): { version: number | null; blocked: string | null } {
  if (!allowed) return { version: null, blocked: 'Rolling back needs the author role.' }
  if (versions.active === null) return { version: null, blocked: 'Nothing is served, so there is nothing to go back from; serve a version instead.' }
  if (versions.previous === null || versions.previous === versions.active) return { version: null, blocked: 'There is no earlier version to return to.' }
  return { version: versions.previous, blocked: null }
}

/** What a person is told after a lifecycle call went through. */
export function lifecycleMessage(kind: ViewKind, name: string, action: 'switch' | 'rollback' | 'retire', version: number | null, moved: string[]): string {
  if (action === 'retire') return `The ${kind} ${name} is no longer served to the group. Its versions are kept; publishing it again brings it back.`
  const others = moved.filter((m) => !m.startsWith(`${kind}:${name}@`))
  const extra = others.length > 0 ? ` The model versions it was published against moved with it: ${others.join(', ')}.` : ''
  return `${action === 'rollback' ? 'Rolled back: version' : 'Version'} ${version} of ${name} is served now.${extra}`
}

/**
 * The versions a prune dialog preselects (plan 30 PB05, default taken): all but the served one, the one before it and the three newest. The Gateway also keeps what a channel serves and what another version pins; this
 * saves the person from ticking those.
 */
export function pruneCandidates(versions: DocumentVersions, keepNewest = 3): number[] {
  const numbers = versions.versions.map((v) => v.version).sort((a, b) => a - b)
  const newest = new Set([...numbers].reverse().slice(0, keepNewest))
  return numbers.filter((v) => v !== versions.active && v !== versions.previous && !newest.has(v))
}
