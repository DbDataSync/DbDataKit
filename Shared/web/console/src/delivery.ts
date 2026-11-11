import { request } from '@dbdatakit/shell'

/** What a delivery channel name is bound to in one release channel: the connection (its address is never here, only its template), whether it was bound for this release channel or by `*`, and whether it can be used now (plan 22a GP18 fold). */
export interface ChannelBinding {
  release: string
  connection: string
  own: boolean
  kind: string | null
  target: string | null
  ready: boolean
  missing: string[]
  problem: string | null
}

/** A delivery channel of the install's `delivery:` section as the console shows it: the connection it uses in each release channel, never a secret or an address. */
export interface DeliveryChannel {
  name: string
  kind: string
  limitPerMinute: number | null
  ready: boolean
  missing: string[]
  bindings: ChannelBinding[]
  outbox: Record<string, number>
}

/** A connection a message can be sent through: a mail server, or a webhook. */
export interface DeliveryConnection { name: string; kind: string; target: string | null }

export interface ConnectionKindInfo { kind: string; description: string }

export interface DeliveryOverview {
  problem: string | null
  canRender: boolean
  renderNote: string | null
  isAdmin: boolean
  policy: { maxAttempts: number; backoffSeconds: number[]; leaseSeconds: number }
  releaseChannels: string[]
  connections: DeliveryConnection[]
  connectionKinds: ConnectionKindInfo[]
  channels: DeliveryChannel[]
  outbox: Record<string, number>
}

export interface DeliveryMessage {
  id: string
  channel: string
  releaseChannel?: string
  subject: string
  recipients: string[]
  source: string | null
  ownerPerson: string | null
  status: 'pending' | 'sending' | 'sent' | 'dead'
  attempts: number
  nextAttemptAt: string
  lastOutcome: string | null
  lastDetail: string | null
  createdAt: string
  settledAt: string | null
  attachments: { name: string; mediaType: string; size: number }[]
}

export interface DeliveryAttempt { number: number; sender: string; outcome: string; detail: string | null; startedAt: string; finishedAt: string }

export interface Subscription {
  id: string
  name: string
  kind: 'dashboard' | 'report'
  view: string
  parameters: Record<string, unknown> | null
  layout: string | null
  format: 'pdf' | 'csv' | 'xlsx'
  widget: string | null
  channel: string
  recipient: string | null
  schedule: { cron: string; timeZone: string }
  person: string
  personName: string
  owner: string
  status: 'active' | 'paused' | 'stopped'
  statusDetail: string | null
  nextRunAt: string | null
  lastRunAt: string | null
  lastOutcome: string | null
  lastDetail: string | null
  version: number
  channelProblem: string | null
}

export interface SubscriptionRun { outcome: string; detail: string | null; startedAt: string; finishedAt: string; messageId: string | null; runId: string | null }

/** What the subscribe form holds, as typed. */
export interface SubscriptionForm {
  name: string
  kind: 'dashboard' | 'report'
  view: string
  format: 'pdf' | 'csv' | 'xlsx'
  widget: string
  layout: string
  channel: string
  recipient: string
  cron: string
  timeZone: string
  /** The document's parameters as JSON text: an object of values or lists of values. */
  parameters: string
}

export const emptyForm = (channel = ''): SubscriptionForm => ({ name: '', kind: 'dashboard', view: '', format: 'pdf', widget: '', layout: '', channel, recipient: '', cron: '0 7 * * 1', timeZone: 'UTC', parameters: '' })

/** The request the API reads strictly, from the form; a problem the form can see for itself is returned as words (the server checks the rest, in its own). */
export function buildRequest(form: SubscriptionForm): { body: Record<string, unknown> } | { error: string } {
  if (form.name.trim() === '') return { error: 'Give the subscription a name.' }
  if (form.view.trim() === '') return { error: `Name the ${form.kind} to subscribe to.` }
  if (form.channel === '') return { error: 'Choose a delivery channel.' }
  if (form.format === 'csv' && form.widget.trim() === '') return { error: 'A CSV is one widget’s rows: name the widget.' }
  const body: Record<string, unknown> = { name: form.name.trim(), kind: form.kind, view: form.view.trim(), format: form.format, channel: form.channel, schedule: { cron: form.cron.trim(), time_zone: form.timeZone.trim() || 'UTC' } }
  if (form.widget.trim() !== '' && form.format !== 'pdf') body.widget = form.widget.trim()
  if (form.layout.trim() !== '' && form.format === 'pdf') body.layout = form.layout.trim()
  if (form.recipient.trim() !== '') body.recipient = form.recipient.trim()
  if (form.parameters.trim() !== '') {
    try {
      const parsed: unknown = JSON.parse(form.parameters)
      if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) return { error: 'The parameters are an object, for example {"region": ["east"]}.' }
      body.parameters = parsed
    } catch {
      return { error: 'The parameters are not valid JSON; write an object, for example {"region": ["east"]}.' }
    }
  }
  return { body }
}

export const formFromSubscription = (s: Subscription): SubscriptionForm => ({
  name: s.name, kind: s.kind, view: s.view, format: s.format, widget: s.widget ?? '', layout: s.layout ?? '', channel: s.channel, recipient: s.recipient ?? '', cron: s.schedule.cron, timeZone: s.schedule.timeZone,
  parameters: s.parameters ? JSON.stringify(s.parameters) : '',
})

/** A word for a person, not the stored value. */
export const statusWord = (status: DeliveryMessage['status']) => ({ pending: 'Waiting', sending: 'Sending', sent: 'Sent', dead: 'Failed' })[status]

export const subscriptionStatusWord = (s: Pick<Subscription, 'status'>) => ({ active: 'Active', paused: 'Paused', stopped: 'Stopped' })[s.status]

/** What the outbox holds as a line: "3 sent, 1 failed". Nothing for an empty one. */
export function outboxLine(counts: Record<string, number>): string {
  const parts = (['pending', 'sending', 'sent', 'dead'] as const).filter((k) => (counts[k] ?? 0) > 0).map((k) => `${counts[k]} ${({ pending: 'waiting', sending: 'sending', sent: 'sent', dead: 'failed' })[k]}`)
  return parts.length === 0 ? 'nothing yet' : parts.join(', ')
}

/** Why a channel cannot be used now, or null: the references that cannot be read, in the words the server gave. */
export const channelProblem = (c: Pick<DeliveryChannel, 'ready' | 'missing'>): string | null => (c.ready ? null : c.missing.length > 0 ? c.missing.join('; ') : 'Not ready.')

/** Why one binding cannot be used now, or null: the connection's problem, or the references that cannot be read. */
export const bindingProblem = (b: Pick<ChannelBinding, 'ready' | 'missing' | 'problem'>): string | null => (b.ready ? null : b.problem ?? (b.missing.length > 0 ? b.missing.join('; ') : 'Not ready.'))

/** The mapping the screen edits: delivery channel name, then release channel, then connection name. `*` is the entry for every release channel that has none of its own. */
export type ChannelMapping = Record<string, Record<string, string>>

export const mappingOf = (channels: Pick<DeliveryChannel, 'name' | 'bindings'>[]): ChannelMapping =>
  Object.fromEntries(channels.map((c) => [c.name, Object.fromEntries(c.bindings.map((b) => [b.release, b.connection]))]))

/** A release channel's name: letters, digits, dashes and underscores, or `*` for every release channel that has no entry of its own (the settings reader's rule). */
export const isReleaseName = (name: string): boolean => name === '*' || /^[A-Za-z0-9_-]{1,64}$/.test(name)

/** A delivery channel's name (the settings reader's rule). */
export const isChannelName = (name: string): boolean => /^[A-Za-z0-9_-]{1,64}$/.test(name)

/**
 * The body of `PUT /api/delivery/channels` from the mapping and the limits: a channel bound in the production release channel only is written as the connection's name alone, as the file does, any other as release channel to connection.
 * Entries with no connection are left out, and so is a channel with none.
 */
export function mappingBody(mapping: ChannelMapping, limits: Record<string, number | null> = {}): { channels: Record<string, string | Record<string, string>>; limits?: Record<string, number> } {
  const channels: Record<string, string | Record<string, string>> = {}
  for (const [name, releases] of Object.entries(mapping)) {
    const entries = Object.entries(releases).filter(([, connection]) => connection !== '')
    if (entries.length === 0) continue
    channels[name] = entries.length === 1 && entries[0][0] === 'prod' ? entries[0][1] : Object.fromEntries(entries)
  }
  const kept = Object.fromEntries(Object.entries(limits).filter(([name, n]) => n !== null && name in channels) as [string, number][])
  return Object.keys(kept).length > 0 ? { channels, limits: kept } : { channels }
}

/** What is wrong with the mapping the screen can see for itself (the server checks the rest, in its own words), or null. */
export function mappingProblem(mapping: ChannelMapping): string | null {
  for (const [name, releases] of Object.entries(mapping)) {
    if (!isChannelName(name)) return `\`${name}\` is not a channel name: letters, digits, dashes and underscores.`
    for (const release of Object.keys(releases)) if (!isReleaseName(release)) return `\`${release}\` is not a release channel name: letters, digits, dashes and underscores (or * for every release channel).`
  }
  return null
}

/** Whether the mapping is the one the server has (nothing to save). */
export const sameMapping = (a: ChannelMapping, b: ChannelMapping): boolean => JSON.stringify(sorted(a)) === JSON.stringify(sorted(b))
const sorted = (m: ChannelMapping) => Object.keys(m).sort().map((k) => [k, Object.entries(m[k]).sort(([x], [y]) => (x < y ? -1 : 1))])

/** A line for one binding: where it goes and whether it can be used. */
export const bindingLine = (b: ChannelBinding): string =>
  `${b.release === '*' ? 'every other release channel' : b.release} uses ${b.connection}${b.kind ? ` (${b.kind}${b.target ? `, ${b.target}` : ''})` : ''}${b.own || b.release === '*' ? '' : ' (from *)'}`

/** Why "send now" is not offered, or null when it is. */
export function sendNowBlockedReason(canRender: boolean, renderNote: string | null, s: Pick<Subscription, 'channelProblem'>): string | null {
  if (!canRender) return renderNote ?? 'This console cannot draw the document.'
  return s.channelProblem
}

/** The problem a subscription shows, most important first: it stopped, its channel is gone, its last run failed. */
export function subscriptionProblem(s: Pick<Subscription, 'status' | 'statusDetail' | 'channelProblem' | 'lastOutcome' | 'lastDetail'>): string | null {
  if (s.status === 'stopped') return s.statusDetail ?? 'Stopped.'
  if (s.channelProblem) return s.channelProblem
  if (s.statusDetail) return s.statusDetail
  if (s.lastOutcome === 'failed') return s.lastDetail ?? 'The last run failed.'
  return null
}

/** A schedule as words for the common shapes, else the cron itself. */
export function describeSchedule(cron: string, timeZone: string): string {
  const m = /^(\d{1,2}) (\d{1,2}) \* \* (\*|[0-6](?:-[0-6])?|[0-6](?:,[0-6])+)$/.exec(cron.trim())
  const days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
  if (m) {
    const time = `${m[2].padStart(2, '0')}:${m[1].padStart(2, '0')}`
    const d = m[3]
    const when = d === '*' ? 'every day' : /^[0-6]$/.test(d) ? `every ${days[Number(d)]}` : d === '1-5' ? 'on weekdays' : `on ${d.split(/[,-]/).map((x) => days[Number(x)].slice(0, 3)).join(d.includes('-') ? ' to ' : ', ')}`
    return `${when} at ${time} (${timeZone})`
  }
  return `${cron} (${timeZone})`
}

const json = (body: unknown): RequestInit => ({ method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) })

export const deliveryApi = {
  overview: () => request<DeliveryOverview>('/api/delivery'),
  test: (channel: string, recipient: string, release = 'prod') =>
    request<{ id: string; releaseChannel: string; connection: string; message: DeliveryMessage; attempts: DeliveryAttempt[] }>(`/api/delivery/channels/${encodeURIComponent(channel)}/test`, json({ recipient: recipient.trim() === '' ? null : recipient.trim(), channel: release })),
  saveChannels: (body: ReturnType<typeof mappingBody>) => request<{ saved: boolean; releaseChannels: string[]; channels: DeliveryChannel[] }>('/api/delivery/channels', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) }),
  messages: (status: string, channel: string) => request<{ messages: DeliveryMessage[] }>(`/api/delivery/messages?${new URLSearchParams({ ...(status ? { status } : {}), ...(channel ? { channel } : {}), limit: '200' })}`),
  message: (id: string) => request<{ message: DeliveryMessage; attempts: DeliveryAttempt[] }>(`/api/delivery/messages/${id}`),
  requeue: (id: string) => request<{ message: DeliveryMessage }>(`/api/delivery/messages/${id}/requeue`, { method: 'POST' }),
  subscriptions: (all: boolean) => request<{ subscriptions: Subscription[]; channels: string[]; canRender: boolean }>(`/api/delivery/subscriptions${all ? '?all=true' : ''}`),
  subscribe: (body: Record<string, unknown>) => request<{ subscription: Subscription }>('/api/delivery/subscriptions', json(body)),
  update: (id: string, version: number, body: Record<string, unknown>) =>
    request<{ subscription: Subscription }>(`/api/delivery/subscriptions/${id}?version=${version}`, { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) }),
  remove: (id: string) => request<void>(`/api/delivery/subscriptions/${id}`, { method: 'DELETE' }),
  pause: (id: string) => request<{ subscription: Subscription }>(`/api/delivery/subscriptions/${id}/pause`, { method: 'POST' }),
  resume: (id: string) => request<{ subscription: Subscription }>(`/api/delivery/subscriptions/${id}/resume`, { method: 'POST' }),
  run: (id: string) => request<{ result: { outcome: string; detail: string; messageId: string | null }; subscription: Subscription }>(`/api/delivery/subscriptions/${id}/run`, { method: 'POST' }),
  runs: (id: string) => request<{ runs: SubscriptionRun[] }>(`/api/delivery/subscriptions/${id}/runs`),
}
