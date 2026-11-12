import { request } from '@dbdatakit/shell'
import type { Diagnostic } from './agents'

/*
 * Compare, adopt and publish (plan 27a IX25, docs/channel-bindings.md): the console's bindings and the connections it keeps beside a Runner's or a Gateway's, what differs marked, and per item either
 * ADOPT (the other side's value becomes the console's) or PUBLISH (the console's value goes to the other server, by plan 30's shared route). Plus the one combined, read-only list of the posture
 * choices of the console's table and the Runners'. The server does the comparing; this file is the shapes, the calls and the words.
 */

export type CompareState = 'same' | 'differs' | 'only-here' | 'only-there'
export type CompareFamily = 'binding' | 'connection'

export interface CompareItem {
  id: string
  family: CompareFamily
  name: string
  definitionKind: string | null
  slot: string | null
  channel: string | null
  state: CompareState
  here: string | null
  there: string | null
  hereText: string | null
  thereText: string | null
  canAdopt: boolean
  canPublish: boolean
  problem: string | null
}

export interface CompareView { server: string; serverName: string; product: string; problems: string[]; items: CompareItem[] }

export interface CompareOutcome { done: boolean; item?: CompareItem; error?: string; diagnostics: Diagnostic[] }

export interface PostureEntry { setting: string; scope: string; chosenBy: string; chosenAt: string; note: string | null; heldBy: string[]; default: string | null; effect: string | null }
export interface PostureSource { holder: string; ok: boolean; choices: number; problem: string | null }
export interface PostureView { entries: PostureEntry[]; sources: PostureSource[] }

/** What a person asks of an item: the server, the item and the other side's value as it was shown (the server refuses when it changed since). */
export interface CompareDecision { server: string; family: CompareFamily; name: string; definitionKind: string | null; slot: string | null; channel: string | null; expected: string | null }

export const decisionOf = (server: string, item: CompareItem): CompareDecision =>
  ({ server, family: item.family, name: item.name, definitionKind: item.definitionKind, slot: item.slot, channel: item.channel, expected: item.there })

export const STATE_WORDS: Record<CompareState, string> = {
  same: 'The same',
  differs: 'Differs',
  'only-here': 'Only in the console',
  'only-there': 'Only on the other server',
}

/** The words an item is called by: a binding as its definition, slot and release channel, a connection by its name. */
export const itemTitle = (i: CompareItem): string =>
  i.family === 'connection' ? i.name : `${i.definitionKind ?? ''} ${i.name}: ${i.slot ?? ''} in ${i.channel ?? ''}`.trim()

/** What the adopt button says for an item, and what the publish button says: nothing at all to take or to give is said as such. */
export const adoptWords = (i: CompareItem, serverName: string): string =>
  i.family === 'connection' ? `Keep ${serverName}'s copy as the console's file` : `Use ${serverName}'s value in the console`

export const publishWords = (i: CompareItem, serverName: string): string =>
  i.family === 'binding' && i.here === null ? `Empty it on ${serverName}` : `Send the console's value to ${serverName}`

/** The items that differ (anything but the same), the ones a person looks at first. */
export const differing = (items: CompareItem[]): CompareItem[] => items.filter((i) => i.state !== 'same')

export function summary(items: CompareItem[]): string {
  const by = (s: CompareState) => items.filter((i) => i.state === s).length
  if (items.length === 0) return 'Nothing to compare.'
  const parts = [`${by('differs')} differ`, `${by('only-here')} only in the console`, `${by('only-there')} only on the other server`, `${by('same')} the same`]
  return parts.join(', ')
}

/** A posture choice's holders in words: where the choice is recorded (the console's table, a runner's), once for a choice however many hold it. */
export const heldWords = (e: PostureEntry): string => (e.heldBy.length === 1 ? `held by ${e.heldBy[0]}` : `held by ${e.heldBy.join(' and ')}`)

const json = (method: string, body: unknown): RequestInit => ({ method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })

export const compareApi = {
  compare: (server: string) => request<CompareView>(`/api/bindings/compare?server=${encodeURIComponent(server)}`),
  adopt: (d: CompareDecision) => request<CompareOutcome>('/api/bindings/compare/adopt', json('POST', d)),
  publish: (d: CompareDecision) => request<CompareOutcome>('/api/bindings/compare/publish', json('POST', d)),
  posture: () => request<PostureView>('/api/bindings/posture'),
}
