import type { SavedQueryRow } from './runTypes'

/**
 * The owner as the list shows them: the email or UPN the Gateway keys a person by (plan 14a D35), with the display name beside it where the Gateway knows it. A caller the identity home does not know has no email and is keyed by the
 * name in lower case, so the key is then only as exact as that name is.
 */
export function ownerLabel(row: Pick<SavedQueryRow, 'owner' | 'owner_name'>): string {
  const name = row.owner_name?.trim()
  return name && name.toLowerCase() !== row.owner.toLowerCase() ? `${row.owner} (${name})` : row.owner
}

export const rowKey = (r: Pick<SavedQueryRow, 'model' | 'name' | 'owner'>) => `${r.model}/${r.owner}/${r.name}`

/** The text of a saved query in the box: its semantic SQL, or its request as JSON. */
export const queryText = (r: Pick<SavedQueryRow, 'sql' | 'request'>): string => r.sql ?? JSON.stringify(r.request ?? {}, null, 2)

/** The body of a save for the text in the box: a request when it is a JSON object, semantic SQL otherwise. */
export function saveBody(text: string, description: string, expectedVersion?: number): { body: { sql?: string; request?: Record<string, unknown>; description: string | null; expected_version?: number } } | { problem: string } {
  const trimmed = text.trim()
  if (trimmed === '') return { problem: 'Write the statement to keep.' }
  const common = { description: description.trim() === '' ? null : description.trim(), ...(expectedVersion === undefined ? {} : { expected_version: expectedVersion }) }
  if (trimmed.startsWith('{')) {
    try {
      const request = JSON.parse(trimmed)
      if (request === null || typeof request !== 'object' || Array.isArray(request)) return { problem: 'A request is a JSON object.' }
      return { body: { ...common, request } }
    } catch (e) { return { problem: `The request is not valid JSON: ${(e as Error).message}` } }
  }
  return { body: { ...common, sql: trimmed.replace(/;\s*$/, '') } }
}

const EMAIL = /^[^\s@]+@[^\s@]+$/

/**
 * The people a query is shared with, from the lines of the box (or commas): `user:<email or UPN>`, `group:<name>` or `*`. A bare email is a user; an email is lower case and trimmed as the Gateway keeps it. A word that is none of these
 * is a problem, named.
 */
export function parseGrantees(text: string): { grantees: string[]; problems: string[] } {
  const grantees: string[] = []
  const problems: string[] = []
  for (const raw of text.split(/[\n,]+/)) {
    const item = raw.trim()
    if (item === '') continue
    let g: string
    if (item === '*') g = '*'
    else if (/^group:\S+$/i.test(item)) g = 'group:' + item.slice(6)
    else if (/^user:\S+$/i.test(item)) g = 'user:' + item.slice(5).trim().toLowerCase()
    else if (EMAIL.test(item)) g = 'user:' + item.toLowerCase()
    else { problems.push(`'${item}' is not user:<email>, group:<name> or *.`); continue }
    if (!grantees.includes(g)) grantees.push(g)
  }
  return { grantees, problems }
}

export const granteesText = (grantees: string[]) => grantees.join('\n')

/** Who a grantee is, in words. */
export function granteeLine(g: string): string {
  if (g === '*') return 'everyone who can query the model'
  if (g.startsWith('group:')) return `the group ${g.slice(6)}`
  return g.slice(5)
}

/** The explorer's address for a saved query, own or shared. */
export const openLink = (r: Pick<SavedQueryRow, 'model' | 'name' | 'owner' | 'own'>) =>
  `/models/${encodeURIComponent(r.model)}/explore?saved=${encodeURIComponent(r.name)}${r.own ? '' : `&owner=${encodeURIComponent(r.owner)}`}`

/** The person's own first, then what is shared with them; each group by model and name. */
export function ordered(rows: SavedQueryRow[]): SavedQueryRow[] {
  return [...rows].sort((a, b) => Number(b.own) - Number(a.own) || a.model.localeCompare(b.model) || a.name.localeCompare(b.name))
}

/** Own and shared lists of several models merged once each. */
export function merged(lists: SavedQueryRow[][]): SavedQueryRow[] {
  const seen = new Set<string>()
  const all: SavedQueryRow[] = []
  for (const row of lists.flat()) { const k = rowKey(row); if (!seen.has(k)) { seen.add(k); all.push(row) } }
  return ordered(all)
}
