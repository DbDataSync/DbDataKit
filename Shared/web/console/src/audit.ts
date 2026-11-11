import { request } from '@dbdatakit/shell'

/** One thing the console recorded (`GET /api/audit`, UI05): who did what to what, through which server, and how it ended. Never a value: the path, the method and the status only. */
export interface AuditEntry {
  id: number
  at: string
  /** The person's id, or `anonymous` for a call nobody was signed in for. */
  actor: string
  /** The person's name now, null when the account is gone. */
  actorName: string | null
  verb: string
  outcome: 'ok' | 'refused' | 'failed'
  targetKind: string | null
  targetId: string | null
  /** The name of the person the entry is about (a user's roles changed, a person who redeemed an invitation). */
  targetName: string | null
  detail: { method?: string; path?: string; status?: number; serverName?: string; name?: string } | null
}

export interface AuditList { entries: AuditEntry[]; next: number | null }

/** What the filter on the page narrows by; an empty field does not narrow. */
export interface AuditFilter {
  /** A group of verbs (`relay.`, `server.`), see {@link groups}. */
  verb: string
  outcome: '' | AuditEntry['outcome']
  /** A person's id. */
  actor: string
  /** A date (`2026-11-11`) in the person's time, from the start of that day. */
  from: string
  /** A date, up to the end of that day. */
  to: string
}

export const noFilter: AuditFilter = { verb: '', outcome: '', actor: '', from: '', to: '' }

/** The groups of verbs the console records, as the filter offers them. */
export const groups: { prefix: string; label: string }[] = [
  { prefix: 'signin.', label: 'Sign-ins' },
  { prefix: 'invite.', label: 'Invitations' },
  { prefix: 'user.', label: 'People' },
  { prefix: 'server.', label: 'Servers paired and removed' },
  { prefix: 'relay.', label: 'Writes made on a server' },
  { prefix: 'console.', label: 'Other changes in the console' },
]

/** The start of a local day as an ISO instant, or the start of the next one for `end`; empty for no date or one that is not a date. */
export function dayBound(date: string, end = false): string {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) return ''
  const [y, m, d] = date.split('-').map(Number)
  const at = new Date(y!, m! - 1, d! + (end ? 1 : 0))
  return Number.isNaN(at.getTime()) ? '' : at.toISOString()
}

/** The query string of a read: the filter, the page size and where the page starts (the last id of the page before). */
export function auditQuery(filter: AuditFilter, beforeId?: number | null, limit = 50): string {
  const q = new URLSearchParams()
  if (filter.verb) q.set('verb', filter.verb)
  if (filter.outcome) q.set('outcome', filter.outcome)
  if (filter.actor.trim()) q.set('actor', filter.actor.trim())
  if (dayBound(filter.from)) q.set('from', dayBound(filter.from))
  if (dayBound(filter.to, true)) q.set('to', dayBound(filter.to, true))
  if (beforeId != null) q.set('beforeId', String(beforeId))
  q.set('limit', String(limit))
  return `?${q.toString()}`
}

export const auditApi = {
  list: (filter: AuditFilter, beforeId?: number | null, limit?: number) => request<AuditList>(`/api/audit${auditQuery(filter, beforeId, limit)}`),
}

const outcomeText: Record<AuditEntry['outcome'], string> = { ok: 'Done', refused: 'Refused', failed: 'Failed' }
export const outcomeLabel = (outcome: string) => outcomeText[outcome as AuditEntry['outcome']] ?? outcome

/** Who, as a line says it: the name when the account still has one, else the id. */
export const who = (e: AuditEntry) => (e.actor === 'anonymous' ? 'Nobody signed in' : e.actorName ?? e.actor)

/** What was done, in a sentence a person reads; the verbs are the console's own (`ConsoleAuditTrail`), and one it does not know is shown as it is. */
export function what(e: AuditEntry): string {
  const d = e.detail ?? {}
  const server = d.serverName ?? e.targetId ?? 'a server'
  const person = e.targetName ?? e.targetId ?? 'a person'
  switch (e.verb) {
    case 'signin.passkey': return 'signed in with a passkey'
    case 'signin.windows': return 'signed in with Windows'
    case 'invite.create': return 'made an invitation'
    case 'invite.redeem': return `redeemed an invitation${d.name ? ` as ${d.name}` : ''}`
    case 'user.update': return `changed the roles or details of ${person}`
    case 'user.credential_remove': return `removed a way of signing in of ${person}`
    case 'server.pair': return `paired the server ${d.serverName ?? e.targetId ?? ''}`.trim()
    case 'server.confirm': return `confirmed the waiting server ${server}`
    case 'server.reject': return `rejected the waiting server ${server}`
    case 'server.forget': return `removed the server ${server}`
    case 'server.link': return 'paired a Build server with a Gateway'
    case 'state.connection.save': return `saved the state database ${e.targetId ?? ''}`.trim()
    case 'state.connection.remove': return `removed the state database ${e.targetId ?? ''}`.trim()
    case 'state.move.check': return 'checked a state move before starting it'
    case 'state.move.start': return 'started moving a server\'s state to another database'
    case 'relay.write': return `${d.method ?? 'wrote'} ${relayedPath(d.path)} on ${server}`
    case 'console.write': return `${d.method ?? 'changed'} ${e.targetId ?? d.path ?? ''}`.trim()
    default: return e.verb
  }
}

/** The path a person sent to a server, without the console's `/relay/<server>` in front. */
export const relayedPath = (path: string | undefined) => (path ?? '').replace(/^\/relay\/[^/]+/, '') || '/'

/** The entries of a next page joined to what is shown, without one twice (a page read again after a change). */
export function appendPage(shown: AuditEntry[], page: AuditEntry[]): AuditEntry[] {
  const seen = new Set(shown.map((e) => e.id))
  return [...shown, ...page.filter((e) => !seen.has(e.id))]
}
