import { describe, expect, it } from 'vitest'
import { buildRow, duration, moveBlock, ownerSentence, type PlanMove, LAG_LIMIT_MS, loadRows, NOTICE_WINDOW_MS, gatewayRow, runnerGoneRow, runnerTaskRow, sortRows, syncRow } from './monitoring'
import type { InstanceSummary } from './api'

const server = (id: string, name: string, product: string): InstanceSummary => ({ id, name, url: 'https://x', product, version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null })
const sync = server('s1', 'Sync prod', 'dbdatasync')
const build = server('b1', 'Build prod', 'dbdatabuild')
const status = { running: false, enabled: true, paused: false }

const gateway = server('p1', 'Gateway prod', 'dbdatagateway')

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
    // a hold on a plan that also failed is still a hold: the row stays a failure, and the button releases the hold
    expect(buildRow(build, 'sales', { ...job, last_exit: 1, paused: true })).toMatchObject({ state: 'failed', held: true })
    expect(buildRow(build, 'sales', job).held).toBe(false)
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

  it('shows a Gateway notice as a row of its own, with no buttons to act on, after what failed and what lags', () => {
    const notice = gatewayRow(gateway, { id: 7, kind: 'LoginLockedOut', created: '2026-10-30T10:00:00Z', message: 'The user reader from 10.0.0.5 is locked out for 15 minutes.' })
    expect(notice).toMatchObject({ state: 'event', kind: 'event', product: 'dbdatagateway', subject: 'Login locked out', target: null, key: 'p1/notice/7' })
    expect(gatewayRow(gateway, { id: 8, kind: 'SomethingNew', created: '', message: 'm' }).subject).toBe('SomethingNew')
    const rows = sortRows([
      syncRow(sync, 'quiet', status, { highestLagMs: 0 }, { runs: [] }),
      notice,
      syncRow(sync, 'orders', status, { highestLagMs: LAG_LIMIT_MS * 2 }, { runs: [] }),
      buildRow(build, 'sales', { connection: 'pg', plan: 'hourly', schedule: '', next_due: null, last_started: null, last_exit: 2, paused: true }),
    ])
    expect(rows.map((r) => r.state)).toEqual(['failed', 'lagging', 'event', 'ok'])
  })
})

describe('loading the rows of a Gateway', () => {
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
      const { rows, problems } = await loadRows([gateway], now)
      expect(asked).toEqual(['/relay/p1/api/v1/notifications'])
      expect(problems).toEqual([])
      expect(rows.map((r) => r.detail)).toEqual(['newer', 'older'])
      expect(NOTICE_WINDOW_MS).toBe(86_400_000)
    } finally { globalThis.fetch = real }
  })
})

describe('runner rows', () => {
  const runner = server('r1', 'Runner prod', 'dbdatarunner')
  const task = { package: 'orders', channel: 'prod', task: 'extract', paused: false, running: 0, last_run: { outcome: 'succeeded' } }

  it('shows a task that failed, waits, has a problem, is held or runs', () => {
    expect(runnerTaskRow(runner, task)).toMatchObject({ state: 'ok', subject: 'orders/prod/extract', target: { runnerTask: { package: 'orders', channel: 'prod', task: 'extract' } } })
    expect(runnerTaskRow(runner, { ...task, last_run: { outcome: 'failed' } })).toMatchObject({ state: 'failed', detail: 'The last run failed' })
    expect(runnerTaskRow(runner, { ...task, last_run: { outcome: 'completedwithfailures' } }).detail).toBe('The last run completed with failures')
    expect(runnerTaskRow(runner, { ...task, waiting_reason: "No live runner has the label 'gpu'" })).toMatchObject({ state: 'failed', detail: "No live runner has the label 'gpu'" })
    expect(runnerTaskRow(runner, { ...task, problem: 'cannot start' })).toMatchObject({ state: 'failed', detail: 'cannot start' })
    expect(runnerTaskRow(runner, { ...task, paused: true, paused_by: 'Ada' })).toMatchObject({ state: 'paused', held: true, detail: 'Paused by Ada' })
    expect(runnerTaskRow(runner, { ...task, running: 1 }).state).toBe('running')
    expect(runnerTaskRow(runner, { ...task, last_run: null })).toMatchObject({ state: 'ok', detail: 'No run yet' })
  })

  it('shows a runner that is gone right after the failures, with no buttons', () => {
    const gone = runnerGoneRow(runner, { id: 'i2', host: 'box-2', live: false, state: 'Gone' })
    expect(gone).toMatchObject({ state: 'gone', kind: 'instance', target: null, detail: 'The runner stopped' })
    expect(runnerGoneRow(runner, { id: 'i3', host: 'box-3', live: false, state: 'Running' }).detail).toBe('The runner stopped reporting')
    const rows = sortRows([runnerTaskRow(runner, task), syncRow(sync, 'orders', status, { highestLagMs: LAG_LIMIT_MS * 2 }, { runs: [] }), gone, runnerTaskRow(runner, { ...task, last_run: { outcome: 'failed' } })])
    expect(rows.map((r) => r.state)).toEqual(['failed', 'gone', 'lagging', 'ok'])
  })
})


describe('who schedules a plan (plan 33, UE06)', () => {
  const job = { connection: 'pg', plan: 'hourly', schedule: '15 * * * *', next_due: '2026-10-27T13:15:00Z', last_started: null, last_exit: null }

  it('says nothing more for a plan this server schedules, and names the runner for the one the runner schedules', () => {
    expect(buildRow(build, 'sales', job)).toMatchObject({ owner: 'old' })
    expect(buildRow(build, 'sales', job).detail).not.toContain('Scheduled by')
    const runner = { server: 'runner-x', channel: 'prod', last_run: null, error: null }
    const owned = buildRow(build, 'sales', { ...job, owner: 'runner', runner })
    expect(owned).toMatchObject({ owner: 'runner', runner: { server: 'runner-x', channel: 'prod', error: null } })
    expect(owned.detail).toContain('Scheduled by the runner runner-x (prod)')
    expect(buildRow(build, 'sales', { ...job, owner: 'observe', runner }).detail).toContain('observed by the runner runner-x')
  })

  it('says when the runner that schedules a plan did not answer, and keeps the failure of its last run', () => {
    const row = buildRow(build, 'sales', { ...job, owner: 'runner', last_exit: 1, runner: { server: 'runner-x', channel: 'prod', error: 'no route to host' } })
    expect(row.state).toBe('failed')
    expect(row.detail).toContain('The runner did not answer: no route to host')
  })

  it('shows a runner task the runner does not schedule as the publishing product\'s', () => {
    const runnerServer = server('r1', 'Runner', 'dbdatarunner')
    const task = { package: 'sales', channel: 'prod', task: 'refresh', paused: false, running: false, last_run: null }
    expect(runnerTaskRow(runnerServer, task)).toMatchObject({ owner: 'runner' })
    expect(runnerTaskRow(runnerServer, { ...task, owner: 'old' }).detail).toContain('Scheduled by the publishing product, not by this runner')
    expect(runnerTaskRow(runnerServer, { ...task, owner: 'observe' }).detail).toContain('observed by this runner')
  })

  it('words the owner', () => {
    expect(ownerSentence('old')).toBe('Scheduled by this server')
    expect(ownerSentence('runner')).toBe('Scheduled by the runner')
  })

  it('asks for what a move lacks: a reason, and a runner and channel when this server has none written down', () => {
    const row = buildRow(build, 'sales', job)
    const move = (over: Partial<PlanMove> = {}): PlanMove => ({ owner: 'observe', reason: 'trial', runner: 'runner-x', channel: 'prod', ...over })
    expect(moveBlock(row, move())).toBeNull()
    expect(moveBlock(row, move({ reason: ' ' }))).toContain('Say why')
    expect(moveBlock(row, move({ owner: 'old' }))).toContain('already')
    expect(moveBlock(row, move({ runner: '' }))).toContain('Name the runner')
    expect(moveBlock(row, move({ channel: undefined }))).toContain('Name the runner')
    const known = buildRow(build, 'sales', { ...job, owner: 'observe', runner: { server: 'runner-x', channel: 'prod' } })
    expect(moveBlock(known, move({ owner: 'runner', runner: undefined, channel: undefined }))).toBeNull()   // the server's own link is used
    const owned = buildRow(build, 'sales', { ...job, owner: 'runner', runner: { server: 'runner-x', channel: 'prod' } })
    expect(moveBlock(owned, move({ owner: 'observe' }))).toContain('roll it back first')
    expect(moveBlock(owned, move({ owner: 'old' }))).toBeNull()
    expect(moveBlock({ ...row, kind: 'replication' }, move())).toContain('Only a Build plan')
  })
})
