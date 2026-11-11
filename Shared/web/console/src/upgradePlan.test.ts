import { describe, expect, it } from 'vitest'
import { groupOf, planUpgrade, runUpgradePlan, type PlanApi, type PlanEntry } from './upgradePlan'
import type { ServerAvailability } from './upgradeApi'
import type { PrepareReport, UpgradeStatus } from './upgradeRun'

const row = (name: string, product: string, over: Partial<ServerAvailability> = {}): ServerAvailability => ({
  id: `${name}-id`, name, product, version: '1.0.0', generation: 1, newest: '2.0.0', newerAvailable: true, newestGeneration: 1, notesUrl: null, outsideAfterRelease: null, ...over,
})

const status = (over: Partial<UpgradeStatus> = {}): UpgradeStatus => ({ product: 'x', version: '1.0.0', generation: 1, install_method: 'Archive', can_apply: true, reason: null, state: 'none', problem: null, draining: false, active_work: 0, ...over })
const prepared: PrepareReport = { state: 'prepared', product: 'x', from: '1.0.0', to: '2.0.0', generation: 1, install_method: 'Archive', feed: 'f', can_apply: true, message: 'ready' }

/** A fake fleet: each server's answers are scripted by name, and every call is kept. */
function fleet(script: Record<string, { can_apply?: boolean; reason?: string; prepare?: Partial<PrepareReport>; end?: UpgradeStatus['state']; silentFor?: number; message?: string }>) {
  const calls: string[] = []
  const silent: Record<string, number> = {}
  const api: PlanApi = {
    status: async (id) => {
      const name = id.replace('-id', '')
      calls.push(`status ${name}`)
      if ((silent[name] ?? 0) > 0) { silent[name]--; throw new Error('connection refused') }
      const s = script[name]
      return status({ can_apply: s.can_apply ?? true, reason: s.reason ?? null, state: calls.includes(`apply ${name}`) ? (s.end ?? 'committed') : 'none', message: s.message, plan: { id: 'p', from: '1.0.0', to: '2.0.0', state: 'x', reason: null, steps: [] } })
    },
    prepare: async (id) => { const name = id.replace('-id', ''); calls.push(`prepare ${name}`); return { ...prepared, ...script[name].prepare } },
    apply: async (id) => { const name = id.replace('-id', ''); calls.push(`apply ${name}`); silent[name] = script[name].silentFor ?? 0; return status({ state: 'applying' }) },
  }
  return { api, calls }
}

describe('planUpgrade (plan 24a UC10, QU4)', () => {
  it('puts the serving layer before the back-ends, by name inside a group, and lists what it will not do and why', async () => {
    const { api } = fleet({ gw: {}, build: {}, store: { can_apply: false, reason: 'it runs in a container' } })
    const plan = await planUpgrade([
      row('store', 'dbdatastore'), row('sync', 'dbdatasync'), row('gw', 'dbdatagateway'), row('build', 'dbdatabuild'),
      row('old', 'dbdatabuild', { newerAvailable: false }), row('far', 'dbdatarunner', { outsideAfterRelease: 'it is generation 5 and this console 2' }),
    ], api)
    expect(plan.map((e) => `${e.group}:${e.name}:${e.action}`)).toEqual([
      'serving:gw:upgrade', 'back-end:build:upgrade', 'back-end:far:blocked', 'back-end:old:current', 'back-end:store:instruction', 'back-end:sync:manual',
    ])
    expect(plan.find((e) => e.name === 'store')!.note).toBe('it runs in a container')
    expect(plan.find((e) => e.name === 'far')!.note).toContain('Upgrade the console first')
    expect(groupOf('dbdatagateway')).toBe('serving')
    expect(groupOf(null)).toBe('back-end')
  })

  it('does not stop the plan for a server that will not say how it is installed', async () => {
    const api: PlanApi = { status: async () => { throw new Error('nothing answered') }, prepare: async () => prepared, apply: async () => status() }
    const plan = await planUpgrade([row('build', 'dbdatabuild')], api)
    expect(plan[0]).toMatchObject({ action: 'manual' })
    expect(plan[0].note).toContain('nothing answered')
  })
})

describe('runUpgradePlan (plan 24a UC10)', () => {
  const entries = (...names: [string, PlanEntry['action']?][]): PlanEntry[] =>
    names.map(([name, action]) => ({ id: `${name}-id`, name, product: 'dbdatabuild', from: '1.0.0', to: '2.0.0', group: 'back-end', action: action ?? 'upgrade', note: null }))

  it('upgrades one server at a time in the order given and leaves a server that is not an upgrade alone', async () => {
    const { api, calls } = fleet({ a: {}, b: {} })
    const results = await runUpgradePlan(entries(['a'], ['img', 'instruction'], ['b']), api, { sleep: async () => {} })
    expect(results.map((r) => `${r.name}:${r.outcome}`)).toEqual(['a:committed', 'b:committed'])
    expect(calls.filter((c) => c.startsWith('prepare') || c.startsWith('apply'))).toEqual(['prepare a', 'apply a', 'prepare b', 'apply b'])   // one finished before the next began
  })

  it('stops at the first server that is rolled back and the rest stay on the old version', async () => {
    const { api, calls } = fleet({ a: {}, b: { end: 'rolled_back', message: 'the new version did not become healthy' }, c: {} })
    const results = await runUpgradePlan(entries(['a'], ['b'], ['c']), api, { sleep: async () => {} })
    expect(results.map((r) => `${r.name}:${r.outcome}`)).toEqual(['a:committed', 'b:failed', 'c:left'])
    expect(results[1]).toMatchObject({ reason: 'the new version did not become healthy' })
    expect(calls).not.toContain('prepare c')
    expect(calls).not.toContain('apply c')
  })

  it('stops when a server refuses the check, before anything is applied to it', async () => {
    const { api, calls } = fleet({ a: { prepare: { state: 'refused', can_apply: false, message: 'the download does not match its checksum' } }, b: {} })
    const results = await runUpgradePlan(entries(['a'], ['b']), api, { sleep: async () => {} })
    expect(results.map((r) => `${r.name}:${r.outcome}`)).toEqual(['a:failed', 'b:left'])
    expect(results[0]).toMatchObject({ reason: 'the download does not match its checksum' })
    expect(calls).not.toContain('apply a')
  })

  it('waits through the restart (a server that does not answer is not a failure) and gives up on one that never comes back', async () => {
    const slow = fleet({ a: { silentFor: 3 } })
    const ok = await runUpgradePlan(entries(['a']), slow.api, { sleep: async () => {}, maxSilent: 10 })
    expect(ok.map((r) => r.outcome)).toEqual(['committed'])

    const gone = fleet({ a: { silentFor: 50 }, b: {} })
    const results = await runUpgradePlan(entries(['a'], ['b']), gone.api, { sleep: async () => {}, maxSilent: 5 })
    expect(results.map((r) => `${r.name}:${r.outcome}`)).toEqual(['a:failed', 'b:left'])
    expect(results[0]).toMatchObject({ reason: 'The server did not answer again after it was restarted.' })
  })

  it('reports progress as it goes', async () => {
    const { api } = fleet({ a: {} })
    const seen: string[] = []
    await runUpgradePlan(entries(['a']), api, { sleep: async () => {}, onProgress: (id, s) => seen.push(`${id}:${s?.state ?? 'starting'}`) })
    expect(seen[0]).toBe('a-id:starting')
    expect(seen).toContain('a-id:committed')
  })
})
