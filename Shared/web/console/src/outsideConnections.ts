import { request } from '@dbdatakit/shell'
import type { Diagnostic } from './agents'

/** The kinds of connection the console keeps itself (plan 27a IX01): an API, and a model provider. A database connection is kept by the Sync or Gateway server that uses it. */
export const OUTSIDE_KINDS = ['http', 'model-provider'] as const
export type OutsideKind = (typeof OUTSIDE_KINDS)[number]

export const KIND_LABEL: Record<string, string> = { database: 'database', http: 'API (http)', 'model-provider': 'model provider' }

/** What each kind is, in a sentence the new-connection form shows. */
export const KIND_MEANING: Record<OutsideKind, string> = {
  http: 'An HTTP API: an address and how to sign in to it (an API key, basic, OAuth 2 client credentials or a client certificate).',
  'model-provider': 'A model an agent can use: its provider, the models it allows with their prices, and the key as a secret reference.',
}

export const SIGN_IN_LABEL: Record<string, string> = {
  none: 'no sign-in', api_key: 'API key', basic: 'user and password', oauth2_client_credentials: 'OAuth 2 client credentials', client_certificate: 'client certificate',
}

/** What a typed secret is called on the screen, by the field it is kept under. */
export const SECRET_LABEL: Record<string, string> = { key: 'API key', password: 'Password', client_secret: 'Client secret', certificate: 'Certificate (PKCS#12, Base64)', certificate_password: 'Certificate password' }

/** One secret a connection names: where the reference is written, the reference (never a value) and whether it can be read now. */
export interface ConnectionSecret { field: string; path: string; reference: string; available: boolean; problem: string | null }

export interface OutsideConnection {
  name: string
  file: string
  yaml: string
  kind: string | null
  endpoint: string | null
  signIn: string | null
  cloud: boolean | null
  secrets: ConnectionSecret[]
  diagnostics: Diagnostic[]
}

const json = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) })
const enc = encodeURIComponent

export const outsideApi = {
  list: () => request<{ kinds: string[]; connections: OutsideConnection[] }>('/api/outside-connections'),
  template: (kind: string, name: string) => request<{ yaml: string }>(`/api/outside-connections/template?kind=${enc(kind)}&name=${enc(name)}`),
  check: (name: string, yaml: string) => request<{ diagnostics: Diagnostic[] }>(`/api/outside-connections/${enc(name)}/check`, json('POST', { yaml })),
  save: (name: string, yaml: string) => request<{ saved: boolean; diagnostics: Diagnostic[] }>(`/api/outside-connections/${enc(name)}`, json('PUT', { yaml })),
  remove: (name: string) => request<void>(`/api/outside-connections/${enc(name)}`, json('DELETE')),
  setSecret: (name: string, field: string, value: string) => request<{ stored: boolean; reference: string }>(`/api/outside-connections/${enc(name)}/secrets/${enc(field)}`, json('PUT', { value })),
}

/** A connection's name: a letter first, then letters, digits, '-', '_' and '.', at most 64 (the shared connection format's rule). */
export const isConnectionName = (name: string): boolean => /^[A-Za-z][A-Za-z0-9_.-]{0,63}$/.test(name)

/** A line for the list: the address, how it signs in, and for a model provider whether it is a cloud or a local one. */
export function outsideDetail(c: OutsideConnection): string {
  if (c.kind === null) return 'the file does not read'
  const parts = [c.endpoint ?? 'no address', SIGN_IN_LABEL[c.signIn ?? 'none'] ?? c.signIn ?? 'no sign-in']
  if (c.cloud !== null) parts.push(c.cloud ? 'cloud' : 'local')
  return parts.join(', ')
}

/** The secrets a connection names that cannot be read now, as one sentence each; empty when all can. */
export const secretProblems = (c: OutsideConnection): string[] => c.secrets.filter((s) => !s.available).map((s) => s.problem ?? `${s.reference} is not set.`)

/** Whether the secret a reference names is one the console keeps for this connection (so a typed value can be kept here under it). */
export const keptHere = (name: string, s: ConnectionSecret): boolean => s.reference === `secret:dbdata:console:connection:${name}:${s.field}`

/** The diagnostics a refused save carries in its body, shown as a check shows them. */
export const refusedDiagnostics = (e: unknown): Diagnostic[] => (e as { body?: { diagnostics?: Diagnostic[] } } | null)?.body?.diagnostics ?? []
