import { describe, expect, it } from 'vitest'
import { buildRow, duration, LAG_LIMIT_MS, loadRows, NOTICE_WINDOW_MS, proxyRow, sortRows, syncRow } from './monitoring'
import type { InstanceSummary } from './api'

const server = (id: string, name: string, product: string): InstanceSummary => ({ id, name, url: 'https://x', product, version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null })
const sync = server('s1', 'Sync prod', 'dbdatasync')
const build = server('b1', 'Build prod', 'dbdatabuild')
const status = { running: false, enabled: true, paused: false }

const proxy = server('p1', 'Proxy prod', 'dbdataproxy')

describe('monitoring rows', () => {
  it('shows a failed last run, a lag past the limit and a hold from what the servers said', () => {
    expect(syncRow(sync, 'orders', status, { highestLagMs: 1000 }, { runs: [{ status: 'Failed' }] }).state).toBe('failed')
    const lagging = syncRow(sync, 'orders', status, { highestLagMs: LAG_LIMIT_MS + 5 * 60_000 }, { runs: [{ status: 'Succeeded' }] })
    expect(lagging.state).toBe('lagging')
    expect(lagging.detail).toBe('20 min behind')
    expect(syncRow(sync, 'orders', { ...status, paused: true, pauseNote: 'maintenance' }, { highestLagMs: 0 }, { runs: [] })).toMatchObject({ state: 'paused', detail: 'Paused: maintenance' })
    expect(syncRow(sync, 'orders', status, { highestLagMs: null }, { runs: [] })).toMatchObject({ state: 'ok', detail: 'No lag figure yet' })
    expect(syncRow(sync, 'orders', { ...status, running: true }, { highestLagMs: 0 }, { runs: [] }).state).toBe('running')
  })

  it('shows a Build plan that failed or is held', () => {
    const job = { connection: 'pg', plan: 'hourly', schedule: '15 * * * *', next_due: '2026-10-27T13:15:00Z', last_started: null, last_exit: null }
    expect(buildRow(build, 'sales', job).state).toBe('ok')
    expect(buildRow(build, 'sales', { ...job, last_exit: 1 })).toMatchObject({ state: 'failed', detail: 'The last run ended with exit code 1' })
    expect(buildRow(build, 'sales', { ...job, paused: true, paused_by: 'Ada' })).toMatchObject({ state: 'paused', detail: 'Paused by Ada' })
  })

  it('puts a failed Build run and a lagging Sync replication together at the top, ahead of what is fine', () => {
    const rows = sortRows([
      syncRow(sync, 'quiet', status, { highestLagMs: 0 }, { runs: [] }),
      syncRow(sync, 'orders', status, { highestLagMs: LAG_LIMIT_MS * 2 }, { runs: [] }),
      buildRow(build, 'sales', { connection: 'pg', plan: 'hourly', schedule: '', next_due: null, last_started: null, last_exit: 2 }),
    ])
    expect(rows.map((r) => r.state)).toEqual(['failed', 'lagging', 'ok'])
  })

  it('writes durations a person reads', () => {
    expect(duration(10_000)).toBe('under a minute')
    expect(duration(5 * 60_000)).toBe('5 min')
    expect(duration(3 * 3_600_000)).toBe('3 h')
    expect(duration(72 * 3_600_000)).toBe('3 days')
  })

  it('shows a Proxy notice as a row of its own, with no buttons to act on, after what failed and what lags', () => {
    const notice = proxyRow(proxy, { id: 7, kind: 'LoginLockedOut', created: '2026-10-30T10:00:00Z', message: 'The user reader from 10.0.0.5 is locked out for 15 minutes.' })
    expect(notice).toMatchObject({ state: 'event', kind: 'event', product: 'dbdataproxy', subject: 'Login locked out', target: null, key: 'p1/notice/7' })
    expect(proxyRow(proxy, { id: 8, kind: 'SomethingNew', created: '', message: 'm' }).subject).toBe('SomethingNew')
    const rows = sortRows([
      syncRow(sync, 'quiet', status, { highestLagMs: 0 }, { runs: [] }),
      notice,
      syncRow(sync, 'orders', status, { highestLagMs: LAG_LIMIT_MS * 2 }, { runs: [] }),
      buildRow(build, 'sales', { connection: 'pg', plan: 'hourly', schedule: '', next_due: null, last_started: null, last_exit: 2, paused: true }),
    ])
    expect(rows.map((r) => r.state)).toEqual(['failed', 'lagging', 'event', 'ok'])
  })
})

describe('loading the rows of a Proxy', () => {
  const now = Date.parse('2026-10-30T12:00:00Z')
  const feed = [
    { id: 1, kind: 'StatementRefused', created: '2026-10-28T12:00:00Z', message: 'too old' },
    { id: 2, kind: 'LimitReached', created: '2026-10-30T09:00:00Z', message: 'older' },
    { id: 3, kind: 'LoginLockedOut', created: '2026-10-30T11:00:00Z', message: 'newer' },
  ]

  it('asks the feed through the relay and keeps the last day, newest first', async () => {
    const asked: string[] = []
    const real = globalThis.fetch
    globalThis.fetch = (async (url: string) => { asked.push(String(url)); return new Response(JSON.stringify({ notifications: feed }), { status: 200, headers: { 'content-type': 'application/json' } }) }) as typeof fetch
    try {
      const { rows, problems } = await loadRows([proxy], now)
      expect(asked).toEqual(['/relay/p1/api/v1/notifications'])
      expect(problems).toEqual([])
      expect(rows.map((r) => r.detail)).toEqual(['newer', 'older'])
      expect(NOTICE_WINDOW_MS).toBe(86_400_000)
    } finally { globalThis.fetch = real }
  })
})
