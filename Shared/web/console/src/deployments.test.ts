import { describe, expect, it } from 'vitest'
import type { DeployedChannel } from './api'
import { behind, holdingLine, lastChange, versionLine } from './deployments'

const channel: DeployedChannel = {
  group: 'main', kind: 'integration', name: 'orders', channel: 'prod', active: 2, previous: 1,
  last_event: 'activate', last_by: 'grace', last_at: '2026-11-07T09:10:00.0000000Z',
  instances: [
    { instance: 'a', host: 'host-a', version: 2, state: 'active', reported_at: '2026-11-07T09:11:00Z', live: true, on_active: true },
    { instance: 'b', host: 'host-b', version: 1, state: 'ready', reported_at: '2026-11-07T09:11:00Z', live: true, on_active: false },
    { instance: 'c', host: 'host-c', version: 1, state: 'active', reported_at: '2026-11-07T08:00:00Z', live: false, on_active: false },
  ],
}

describe('deployments', () => {
  it('names the runners that are not on the active version, and leaves out the ones that are gone', () => {
    expect(behind(channel).map((i) => i.host)).toEqual(['host-b'])
  })

  it('says the versions of a channel', () => {
    expect(versionLine(channel)).toBe('v2 active, v1 previous')
    expect(versionLine({ active: 3, staged: 4 })).toBe('v3 active, v4 staged')
    expect(versionLine({})).toBe('no version served')
  })

  it('says what a runner holds and that the channel is ahead of it', () => {
    expect(holdingLine({ kind: 'integration', name: 'orders', channel: 'prod', version: 1, state: 'ready', reported_at: '', channel_active: 2, behind: true })).toBe('v1 ready (the channel is on v2)')
    expect(holdingLine({ kind: 'integration', name: 'orders', channel: 'prod', version: 2, state: 'active', reported_at: '', channel_active: 2, behind: false })).toBe('v2 active')
  })

  it('says who changed a channel and when', () => {
    expect(lastChange(channel)).toBe('activate by grace, 2026-11-07 09:10 UTC')
    expect(lastChange({})).toBe('')
  })
})
