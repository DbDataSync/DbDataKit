import { request } from '@dbdatakit/shell'
import type { Diagnostic } from './agents'

/** The kinds of connection the console keeps itself (plan 27a IX01): an API, a model provider, and (the delivery channels, plan 22a GP18) a mail server. A database connection is kept by the Sync or Gateway server that uses it. A webhook is an `http` connection with a `webhook:` section. */
export const OUTSIDE_KINDS = ['http', 'model-provider', 'smtp'] as const
export type OutsideKind = (typeof OUTSIDE_KINDS)[number]

export const KIND_LABEL: Record<string, string> = { database: 'database', http: 'API (http)', 'model-provider': 'model provider', smtp: 'mail server (smtp)' }

/** What each kind is, in a sentence the new-connection form shows. */
export const KIND_MEANING: Record<OutsideKind, string> = {
  http: 'An HTTP API: an address and how to sign in to it (an API key, basic, OAuth 2 client credentials or a client certificate).',
  'model-provider': 'A model an agent can use: its provider, the models it allows with their prices, and the key as a secret reference.',
  smtp: 'A mail server that delivery channels send mail through: its host, how the connection is protected (STARTTLS or TLS), the sender address and the login as a secret reference.',
}

/** What a person can start from: a kind, or a webhook (an `http` connection with a `webhook:` section) of one of the formats delivery channels send. */
export interface NewChoice { id: string; label: string; kind: OutsideKind; flavor?: 'slack' | 'teams' | 'signed'; meaning: string }

export const NEW_CHOICES: NewChoice[] = [
  { id: 'http', label: KIND_LABEL.http, kind: 'http', meaning: KIND_MEANING.http },
  { id: 'model-provider', label: KIND_LABEL['model-provider'], kind: 'model-provider', meaning: KIND_MEANING['model-provider'] },
  { id: 'smtp', label: KIND_LABEL.smtp, kind: 'smtp', meaning: KIND_MEANING.smtp },
  { id: 'slack', label: 'webhook: Slack', kind: 'http', flavor: 'slack', meaning: 'A Slack incoming webhook (Block Kit). Its address carries a token: paste it once and only the template stays in the file.' },
  { id: 'teams', label: 'webhook: Teams', kind: 'http', flavor: 'teams', meaning: 'A Teams Workflows webhook (Adaptive Card). The flow and the signature in its address are secret parts: paste the address once.' },
  { id: 'signed', label: 'webhook: signed', kind: 'http', flavor: 'signed', meaning: 'A generic webhook, a JSON document signed with HMAC-SHA256 and a timestamp with a secret you share with the receiver.' },
]

export const SIGN_IN_LABEL: Record<string, string> = {
  none: 'no sign-in', api_key: 'API key', basic: 'user and password', oauth2_client_credentials: 'OAuth 2 client credentials', client_certificate: 'client certificate', signed_webhook: 'signed webhook (HMAC)',
}

/** What a typed secret is called on the screen, by the field it is kept under; the name of a placeholder of the address is its own label. */
export const SECRET_LABEL: Record<string, string> = { key: 'API key', password: 'Password', client_secret: 'Client secret', certificate: 'Certificate (PKCS#12, Base64)', certificate_password: 'Certificate password' }

export const secretLabel = (field: string, path = ''): string => SECRET_LABEL[field] ?? (path.startsWith('endpoint_secrets.') ? `Secret part {${field}} of the address` : path === 'endpoint' ? 'The whole address (a secret)' : field)

/** One secret a connection names: where the reference is written, the reference (never a value) and whether it can be read now. */
export interface ConnectionSecret { field: string; path: string; reference: string; available: boolean; problem: string | null; keptHere?: boolean }

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

/** What Test found (plan 27a IX02): whether the system was reached (null: not tried), whether it accepted the sign-in, a sentence that says what to do, and the failure's kind and whether a retry can help. Never a secret. */
export interface OutsideTest { ok: boolean; reached: boolean | null; signIn: string; message: string; kind: string | null; temporary: boolean; status: number | null; milliseconds: number }

const json = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) })
const enc = encodeURIComponent

export const outsideApi = {
  list: () => request<{ kinds: string[]; connections: OutsideConnection[] }>('/api/outside-connections'),
  template: (kind: string, name: string, flavor?: string) => request<{ yaml: string }>(`/api/outside-connections/template?kind=${enc(kind)}&name=${enc(name)}${flavor ? `&flavor=${enc(flavor)}` : ''}`),
  webhook: (name: string, body: { url: string; pathSegments?: number[]; queryKeys?: string[] }) => request<WebhookSplit>(`/api/outside-connections/${enc(name)}/webhook`, json('POST', body)),
  check: (name: string, yaml: string) => request<{ diagnostics: Diagnostic[] }>(`/api/outside-connections/${enc(name)}/check`, json('POST', { yaml })),
  save: (name: string, yaml: string) => request<{ saved: boolean; diagnostics: Diagnostic[] }>(`/api/outside-connections/${enc(name)}`, json('PUT', { yaml })),
  remove: (name: string) => request<void>(`/api/outside-connections/${enc(name)}`, json('DELETE')),
  test: (name: string) => request<OutsideTest>(`/api/outside-connections/${enc(name)}/test`, json('POST')),
  setSecret: (name: string, field: string, value: string) => request<{ stored: boolean; reference: string }>(`/api/outside-connections/${enc(name)}/secrets/${enc(field)}`, json('PUT', { value })),
}

/** A connection's name: a letter first, then letters, digits, '-', '_' and '.', at most 64 (the shared connection format's rule). */
export const isConnectionName = (name: string): boolean => /^[A-Za-z][A-Za-z0-9_.-]{0,63}$/.test(name)

/** A pasted webhook address as the server split it: the service it recognised, the format it takes (null: none of ours), the template for the file and the references of the secret parts (never the parts). */
export interface WebhookSplit { detected: boolean; service: string; format: string | null; template: string; secrets: Record<string, string> }

/** What the person can mark as secret when the editor does not know the service: how many path segments the address has and the keys of its query. The values never come back. */
export interface WebhookChoices { pathSegments: number; queryKeys: string[] }

/** The file the editor writes for a split address: the template and the references of its parts, in the format the service takes (an unknown one is a signed webhook, whose key is then written by hand). */
export function webhookYaml(name: string, split: Pick<WebhookSplit, 'format' | 'template' | 'secrets'>): string {
  const lines = ['schema: connection/2', 'kind: http', `name: ${name}`, `endpoint: "${split.template}"`, 'endpoint_secrets:', ...Object.entries(split.secrets).map(([part, ref]) => `  ${part}: ${ref}`), 'webhook:', `  format: ${split.format ?? 'signed'}`]
  if (split.format === null) lines.push('# a signed webhook is signed with a secret you share with the receiver; add its reference:', '# sign_in: { method: signed_webhook, key_secret: secret:... }')
  return lines.join('\n') + '\n'
}

/** The sentence under a split: what was recognised, what stayed visible and which parts are secrets now. Never a part. */
export const splitSummary = (s: WebhookSplit): string =>
  `Recognised as ${s.service}${s.format ? ` (format ${s.format})` : ''}. The file keeps ${s.template}; ${Object.keys(s.secrets).map((p) => `{${p}}`).join(', ')} ${Object.keys(s.secrets).length === 1 ? 'is' : 'are'} kept as secret${Object.keys(s.secrets).length === 1 ? '' : 's'} in the console's secret store.`

/** The format a file says in its `webhook:` section, or null (what a mismatch with the recognised service is checked against). */
export const yamlWebhookFormat = (yaml: string): string | null => /^webhook:[ \t]*(?:\n[ \t]+format:[ \t]*(\w+)|\{[^}]*format:[ \t]*(\w+))/m.exec(yaml)?.slice(1).find((x) => x) ?? null

/** What the form can see of a pasted address for itself: the number of path segments and the query keys (what the person marks as secret when the service is unknown), or null when it is not an http or https address. */
export function webhookChoices(url: string): WebhookChoices | null {
  const m = /^https?:\/\/[^/?#]+(\/[^?#]*)?(?:\?([^#]*))?$/i.exec(url.trim())
  if (!m) return null
  return { pathSegments: (m[1] ?? '').split('/').filter((x) => x !== '').length, queryKeys: (m[2] ?? '').split('&').filter((x) => x !== '').map((p) => p.split('=')[0]) }
}

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
export const keptHere = (name: string, s: ConnectionSecret): boolean => s.keptHere ?? s.reference === `secret:dbdatakit:server:connection:${name}:${s.field}`

/** The diagnostics a refused save carries in its body, shown as a check shows them. */
export const refusedDiagnostics = (e: unknown): Diagnostic[] => (e as { body?: { diagnostics?: Diagnostic[] } } | null)?.body?.diagnostics ?? []

/** Test's answer in one line for the screen: the verdict, then whether the system was reached and what became of the sign-in. */
export function testSummary(t: OutsideTest): string {
  const reached = t.reached === null ? 'not reached or not tried' : t.reached ? 'reached' : 'not reached'
  const retry = t.ok ? '' : t.temporary ? ' A retry can help.' : t.kind ? ' A retry will not help.' : ''
  return `${t.ok ? 'Works' : 'Does not work'}: ${reached}, sign-in ${t.signIn}.${retry}`
}
