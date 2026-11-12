import { request } from '@dbdatakit/shell'
import type { InstanceSummary } from './api'

/** A position in a stream of Build's tracking rows: the ordering value, the row key and the time the value stands for. */
export interface TrackingPoint { order: number; key: string; time: string | null }

/** A divergence as `GET /api/v1/tracking` lists it (plan 28c E18): the copy parted from its primary at `common`. */
export interface TrackingDivergence {
  id: number; identity: string; stream: string; status: string; open: boolean; reason: string
  common: TrackingPoint | null; primary_high: TrackingPoint | null; copy_high: TrackingPoint | null
  diverged_at: string | null; resolved_at: string | null; resolved_by: string | null
}

export interface TrackingStream { stream: string; rows: number; checkpoint: TrackingPoint | null; diverged: boolean }

/** The document of `GET /api/v1/tracking`, from the Build server and from the runner alike (`holder` says whose copy it is). */
export interface TrackingDocument {
  holder?: 'build-server' | 'runner' | 'sync-server'
  mode: 'target' | 'state' | 'both'
  primary: 'target' | 'state' | null
  copies: { identity: string; streams: TrackingStream[]; replication?: string; mode?: TrackingDocument['mode']; primary?: TrackingDocument['primary'] }[]
  divergences: TrackingDivergence[]
}

/** One copy of one connection's tracking tables, on one server, as one line of the page. */
export interface CopyRow {
  key: string
  server: string
  serverName: string
  product: 'dbdatabuild' | 'dbdatarunner' | 'dbdatasync'
  identity: string
  /** The connection the identity ends with (`...@sales`). */
  connection: string
  mode: TrackingDocument['mode']
  primary: TrackingDocument['primary']
  streams: number
  rows: number
  /** The time of the newest row the copy holds (the latest checkpoint of its streams); null when it holds none. */
  newest: string | null
  open: TrackingDivergence[]
  /** True for a row kept from the last time its server answered, while it does not: what it says may be old. */
  stale?: boolean
}

/** The connection a tracking identity (`buildtracking:<engine>://<server>/<database>.<schema>.tracking@<connection>`) is kept for. */
export const connectionOf = (identity: string): string => identity.slice(identity.lastIndexOf('@') + 1)

const timeOf = (text: string | null): number => {
  if (!text) return Number.NaN
  // the server writes seven fractional digits; a browser reads three
  return Date.parse(text.replace(/(\.\d{3})\d+Z$/, '$1Z'))
}

/** The copy's rows of one server's document: every identity it holds or has a divergence for. */
export function copyRows(server: InstanceSummary, doc: TrackingDocument): CopyRow[] {
  const product = server.product === 'dbdatarunner' ? 'dbdatarunner' : server.product === 'dbdatasync' ? 'dbdatasync' : 'dbdatabuild'
  return doc.copies.map((copy) => {
    const times = copy.streams.map((s) => s.checkpoint?.time ?? null).filter((t): t is string => t !== null)
    const newest = times.length === 0 ? null : times.reduce((a, b) => (timeOf(a) >= timeOf(b) ? a : b))
    return {
      key: `${server.id}/${copy.identity}`, server: server.id, serverName: server.name, product, identity: copy.identity, connection: copy.replication ?? connectionOf(copy.identity),
      mode: copy.mode ?? doc.mode, primary: copy.primary !== undefined ? copy.primary : doc.primary, streams: copy.streams.length, rows: copy.streams.reduce((n, s) => n + s.rows, 0), newest,
      open: doc.divergences.filter((d) => d.open && d.identity === copy.identity),
    }
  })
}

/** How old the newest copied row is, in words: the copy's lag as far as the copy itself can tell (what the primary holds is only known to the follower while it runs). */
export function lagText(newest: string | null, now: number): string {
  const at = timeOf(newest)
  if (Number.isNaN(at)) return 'Nothing copied yet'
  const minutes = Math.max(0, Math.round((now - at) / 60000))
  if (minutes < 1) return 'Newest row under a minute old'
  if (minutes < 120) return `Newest row ${minutes} min old`
  const hours = Math.round(minutes / 60)
  return hours < 48 ? `Newest row ${hours} h old` : `Newest row ${Math.round(hours / 24)} days old`
}

/** Where the two sides part, in words: the last row they share. */
export function commonText(d: TrackingDivergence): string {
  if (!d.common) return 'They share no row: the copy starts again from the beginning.'
  if (d.common.time === null || Number.isNaN(timeOf(d.common.time))) return `The last row both hold is ${d.common.key}.`
  return `The last row both hold is ${d.common.key} (${new Date(timeOf(d.common.time)).toLocaleString()}).`
}

/** What a resolve does, in the words next to its button. */
export const resolutionText = { archive: 'Archive: the copy keeps its later rows apart, and copies the target again from the common point.', reset: 'Reset: the copy deletes its later rows, and copies the target again from the common point.' } as const

export type Resolution = keyof typeof resolutionText

export interface Loaded { rows: CopyRow[]; divergences: { server: InstanceSummary; divergence: TrackingDivergence }[]; problems: { server: string; message: string }[] }

/** The copies of every reachable Build server and runner. A server that does not answer is a problem beside the rows (and names which side is down), never a row, and never stops the others. */
export async function loadTracking(servers: InstanceSummary[]): Promise<Loaded> {
  const out: Loaded = { rows: [], divergences: [], problems: [] }
  await Promise.all(servers.filter((s) => s.product === 'dbdatabuild' || s.product === 'dbdatarunner' || s.product === 'dbdatasync').map(async (server) => {
    if (!server.reachable) { out.problems.push({ server: server.name, message: server.problem ?? 'It does not answer' }); return }
    try {
      const doc = await request<TrackingDocument>(`/relay/${server.id}/api/v1/tracking`)
      out.rows.push(...copyRows(server, doc))
      out.divergences.push(...doc.divergences.map((divergence) => ({ server, divergence })))
    } catch (error) {
      out.problems.push({ server: server.name, message: error instanceof Error ? error.message : 'No answer' })
    }
  }))
  out.rows.sort((a, b) => a.serverName.localeCompare(b.serverName) || a.connection.localeCompare(b.connection) || a.identity.localeCompare(b.identity))
  out.divergences.sort((a, b) => Number(b.divergence.open) - Number(a.divergence.open) || b.divergence.id - a.divergence.id)
  return out
}

/** Resolves an open divergence of the copy on this server (an administrator's call, audited by the server). */
export const resolveDivergence = (server: InstanceSummary, id: number, resolution: Resolution) =>
  request<{ resolved: boolean; divergence: TrackingDivergence }>(`/relay/${server.id}/api/v1/tracking/divergences/${id}/resolve`, { method: 'POST', body: JSON.stringify({ resolution }) })

/** The rows of a server that answered before and does not now stay on the page, marked stale, so the side that is down is seen and a switch of its primary can say so. */
export function keepStale(previous: CopyRow[], loaded: Loaded): Loaded {
  const down = new Set(loaded.problems.map((p) => p.server))
  const kept = previous.filter((r) => down.has(r.serverName)).map((r) => ({ ...r, stale: true }))
  return { ...loaded, rows: [...loaded.rows, ...kept] }
}

/**
 * Whether the primary of a copy may be switched from here, and if not why, in a sentence. Switching is refused while a side is down (the server that holds the copy does not answer, so what the other side holds cannot be compared), while a
 * divergence is open (the sides have to agree first), with one side only, for Sync (its primary is a setting of the replication) and for a runner (its primary is the project's, in the published package). Nothing is changed by this call; null means the
 * checks that can be made here pass and the server is asked (it compares the two sides itself and refuses in a sentence too).
 */
export function switchRefusal(row: CopyRow, problems: Loaded['problems']): string | null {
  if (row.stale || problems.some((p) => p.server === row.serverName)) return `Not switched: ${row.serverName} does not answer, so the two sides cannot be compared. Try again when it is back.`
  if (row.open.length > 0) return `Not switched: ${row.connection} has an open divergence (${row.open.map((d) => d.stream).join(', ')}). Resolve it first, so both sides hold the same rows.`
  if (row.mode !== 'both') return `Not switched: the mode is ${row.mode}, so there is one side only; switching needs both.`
  // Sync's primary is a setting of the replication (which side a pass resumes from), not a state of the copy: it is changed where the replication is edited
  if (row.product === 'dbdatasync') return `Not switched here: ${row.connection}'s primary is the \`tracking.primary\` of the replication on ${row.serverName}, which decides where a pass resumes. Change it on the replication's Overview tab, Tracking card.`
  // a runner takes the primary from the project in the package it runs (`tracking.mode` and `primary` of dbdatabuild.yml), so a switch there is a new version of the package
  if (row.product === 'dbdatarunner') return `Not switched here: the primary of ${row.connection} on ${row.serverName} is the project's \`tracking.primary\` in the package it runs. Publish a version of the project with the key changed.`
  return null
}

/** What a switch came to: whether it went through, and the sentence to show either way. */
export interface SwitchResult { done: boolean; message: string }

/** The refusal as the page shows it, for a switch that is refused before the server is asked (the same sentence as \`switchRefusal\`; `done` is always false). */
export function switchPrimary(row: CopyRow, problems: Loaded['problems']): { done: false; message: string } {
  return { done: false, message: switchRefusal(row, problems) ?? `Ask ${row.serverName} to make the ${row.primary === 'state' ? 'target' : 'state'} the primary.` }
}

/**
 * Switches the primary of a Build server's copy (an administrator's call, audited by the server): after the checks that can be made here, the server brings its copy level with the target and refuses in a sentence while a side is down or the two
 * sides differ. From the target to the state, Build writes its records to the state first and to the target second from its next run; back, the target first again.
 */
export async function switchPrimaryOf(server: InstanceSummary, row: CopyRow, problems: Loaded['problems']): Promise<SwitchResult> {
  const refusal = switchRefusal(row, problems)
  if (refusal) return { done: false, message: refusal }
  const to = row.primary === 'state' ? 'target' : 'state'
  try {
    await request<{ switched: boolean }>(`/relay/${server.id}/api/v1/tracking/primary`, { method: 'POST', body: JSON.stringify({ primary: to }) })
    return { done: true, message: `Switched: ${server.name} now writes Build's records to the ${to} first${to === 'state' ? ' and to the target second, from the next run it starts' : ', as it did before'}.` }
  } catch (error) {
    return { done: false, message: error instanceof Error ? error.message : `Not switched: ${server.name} did not answer.` }
  }
}
