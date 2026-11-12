// Releases (plan 21 CH02): a named, ordered list of package promotions with one approval, started, resumed or abandoned from the Releases screen.

export interface ReleaseItemView {
  position: number
  product: string
  target: string
  source: string | null
  kind: string
  name: string
  version: number
  fromChannel: string
  toChannel: string
  state: 'pending' | 'done' | 'failed' | string
  error: string | null
  finishedAt: string | null
}

export interface ReleaseView {
  id: string
  name: string
  state: 'draft' | 'approved' | 'running' | 'stopped' | 'succeeded' | 'abandoned' | string
  createdBy: string
  createdByName: string
  createdAt: string
  approvedBy: string | null
  approvedByName: string
  approvedAt: string | null
  stoppedAt: number | null
  stoppedReason: string | null
  updatedAt: string
  items: ReleaseItemView[]
}

/** The kind of a runner's item (a package), and the kinds of a Gateway's definitions a release promotes (the console checks the same list). */
export const runnerKind = 'integration'
export const gatewayKinds = ['semantic-model', 'data-source', 'entity-model', 'dashboard', 'report', 'report-catalog', 'app', 'app-nav'] as const

/** The product of the server an item of this kind goes to. */
export function productOfKind(kind: string): 'runner' | 'gateway' { return kind === runnerKind ? 'runner' : 'gateway' }

/** The product name a server reports for the kind of item (what the servers list filters by). */
export function serverProductOfKind(kind: string): string { return kind === runnerKind ? 'dbdatarunner' : 'dbdatagateway' }

/** One row of the form that makes a draft; the version is text until it is checked. */
export interface ItemDraft { kind: string; target: string; source: string; name: string; version: string; fromChannel: string; toChannel: string }

export const emptyItem: ItemDraft = { kind: runnerKind, target: '', source: '', name: '', version: '', fromChannel: '', toChannel: '' }

/** What each button of a release is offered for (the console checks again). */
export function actionsFor(state: string): { approve: boolean; start: boolean; resume: boolean; abandon: boolean } {
  return { approve: state === 'draft', start: state === 'approved', resume: state === 'stopped', abandon: state === 'draft' || state === 'approved' || state === 'stopped' }
}

/** Why the draft cannot be made yet, or null. */
export function draftBlock(name: string, items: ItemDraft[]): string | null {
  if (name.trim() === '') return 'Name the release.'
  if (items.length === 0) return 'Add at least one item.'
  for (const [i, item] of items.entries()) {
    const at = `Item ${i + 1}: `
    if (item.target === '') return at + 'choose the server it goes to.'
    if (item.name.trim() === '') return at + 'name the ' + (item.kind === runnerKind ? 'package' : 'definition') + '.'
    if (!/^[1-9][0-9]*$/.test(item.version.trim())) return at + 'the version is a whole number.'
    if (item.fromChannel.trim() === '' || item.toChannel.trim() === '') return at + 'name both channels.'
  }
  return null
}

/** The body of `POST /api/releases` for a draft that has no block. */
export function draftBody(name: string, items: ItemDraft[]) {
  return {
    name: name.trim(),
    items: items.map((i) => ({ product: productOfKind(i.kind), kind: i.kind, target: i.target, ...(i.source === '' ? {} : { source: i.source }), name: i.name.trim(), version: Number(i.version.trim()), fromChannel: i.fromChannel.trim(), toChannel: i.toChannel.trim() })),
  }
}

/** One line for an item: what it moves and where. */
export function itemLine(i: ReleaseItemView, serverName: (id: string) => string): string {
  const from = i.source ? ` from ${serverName(i.source)}` : ''
  return `${i.kind === runnerKind ? '' : i.kind + ' '}${i.name} v${i.version}${from} to ${serverName(i.target)}: ${i.fromChannel} to ${i.toChannel}`
}
