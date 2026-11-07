import type { DeployedChannel, DeployedInstance, RunnerHolding } from './api'

/** The runners of a channel that are not on its active version in the state `active` (ready on another version, failed, still preparing), gone ones left out. */
export function behind(channel: DeployedChannel): DeployedInstance[] {
  return channel.instances.filter((i) => i.live && !i.on_active)
}

/** One line for a channel: `v2 active, v1 previous, v3 staged`; a version the channel does not have is left out. */
export function versionLine(channel: Pick<DeployedChannel, 'active' | 'previous' | 'staged'>): string {
  const parts: string[] = []
  if (channel.active != null) parts.push(`v${channel.active} active`)
  if (channel.previous != null) parts.push(`v${channel.previous} previous`)
  if (channel.staged != null) parts.push(`v${channel.staged} staged`)
  return parts.length > 0 ? parts.join(', ') : 'no version served'
}

/** What a runner says about its place: `v1 ready (the channel is on v2)` for one that lags, `v2 active` for one that is current. */
export function holdingLine(h: RunnerHolding): string {
  const base = `v${h.version} ${h.state}`
  return h.behind && h.channel_active != null ? `${base} (the channel is on v${h.channel_active})` : base
}

/** Who did what and when, as one sentence; empty when nothing was recorded. */
export function lastChange(channel: Pick<DeployedChannel, 'last_event' | 'last_by' | 'last_at'>): string {
  if (!channel.last_event) return ''
  const when = channel.last_at ? new Date(channel.last_at).toISOString().replace('T', ' ').slice(0, 16) + ' UTC' : 'at an unknown time'
  return `${channel.last_event} by ${channel.last_by ?? 'someone'}, ${when}`
}
