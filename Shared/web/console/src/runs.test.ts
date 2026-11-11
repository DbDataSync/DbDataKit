import { describe, expect, it } from 'vitest'
import { buildRunRow, loadRuns, runnerRunRow, sortRuns, syncRunRow, took, visible, type BuildPlanRun, type RunnerRun, type SyncRun } from './runs'
import type { InstanceSummary } from './api'

const server = (id: string, name: string, product: string): InstanceSummary => ({ id, name, url: 'https://x', product, version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null })
const sync = server('s1', 'Sync prod', 'dbdatasync')
const runner = server('r1', 'Runner prod', 'dbdatarunner')

const syncRun = (over: Partial<SyncRun> = {}): SyncRun => ({ runId: 'a', taskName: 'orders', status: 'Succeeded', runKind: 'Primary', mappingName: 'orders', segmentLabel: null, enqueuedAtUtc: '2026-11-10T10:00:00Z', startedAtUtc: '2026-11-10T10:00:05Z', endedAtUtc: '2026-11-10T10:01:05Z', rowsRead: 10, rowsWritten: 9, errorSummary: null, ...over })
const runnerRun = (over: Partial<RunnerRun> = {}): RunnerRun => ({ id: 'x', package: 'etl', channel: 'prod', task: 'extract', outcome: 'succeeded', started: '2026-11-10T11:00:00Z', finished: '2026-11-10T11:00:30Z', rows: 3, failed_item: null, failure: null, ...over })

describe('a Sync run', () => {
  it('says what it read and wrote when it succeeded, and the failure in its own words when it failed', () => {
    expect(syncRunRow(sync, 'orders', syncRun())).toMatchObject({ state: 'succeeded', detail: '10 read, 9 written', subject: 'orders orders', product: 'dbdatasync', link: '/servers/s1/replications/orders/monitoring/history' })
    expect(syncRunRow(sync, 'orders', syncRun({ status: 'Failed', errorSummary: 'Login failed for user' }))).toMatchObject({ state: 'failed', detail: 'Login failed for user' })
    expect(syncRunRow(sync, 'orders', syncRun({ status: 'Failed' })).detail).toBe('The run failed')
  })

  it('names a bulk load or a reconcile and a segment, and shows queued, going and cancelled runs', () => {
    expect(syncRunRow(sync, 'orders', syncRun({ runKind: 'BulkLoad', segmentLabel: 'part 2' })).subject).toBe('orders orders part 2 (bulk load)')
    expect(syncRunRow(sync, 'orders', syncRun({ runKind: 'ReconcileDeletes' })).subject).toBe('orders orders (reconcile)')
    expect(['Queued', 'Pending', 'Running', 'Cancelled'].map((s) => syncRunRow(sync, 'o', syncRun({ status: s as SyncRun['status'] })).state)).toEqual(['queued', 'queued', 'running', 'stopped'])
  })
})

describe('a runner run', () => {
  it('shows a failure, a run completed with failures and an interrupted run as failed, each with what is known', () => {
    expect(runnerRunRow(runner, runnerRun({ outcome: 'failed', failure: { kind: 'Timeout', message: 'too slow' } }))).toMatchObject({ state: 'failed', detail: 'Timeout: too slow', link: '/servers/r1/runs', subject: 'etl/prod/extract' })
    expect(runnerRunRow(runner, runnerRun({ outcome: 'completedwithfailures', failed_item: 'row 7' }))).toMatchObject({ state: 'failed', detail: 'Completed with failures, the first item was row 7' })
    expect(runnerRunRow(runner, runnerRun({ outcome: 'interrupted' })).state).toBe('failed')
    expect(runnerRunRow(runner, runnerRun())).toMatchObject({ state: 'succeeded', detail: '3 items processed' })
    expect(runnerRunRow(runner, runnerRun({ outcome: 'running', finished: null })).state).toBe('running')
    expect(runnerRunRow(runner, runnerRun({ outcome: 'stopped' })).state).toBe('stopped')
  })
})

describe('the list', () => {
  it('is newest first across servers, with a run of unknown time last', () => {
    const rows = sortRuns([
      syncRunRow(sync, 'old', syncRun({ runId: '1', startedAtUtc: '2026-11-10T09:00:00Z' })),
      runnerRunRow(runner, runnerRun()),
      syncRunRow(sync, 'none', syncRun({ runId: '2', startedAtUtc: null, enqueuedAtUtc: null })),
    ])
    expect(rows.map((r) => r.product)).toEqual(['dbdatarunner', 'dbdatasync', 'dbdatasync'])
    expect(rows[2]!.subject).toContain('none')
  })

  it('shows only the failures when asked', () => {
    const rows = [syncRunRow(sync, 'a', syncRun()), syncRunRow(sync, 'b', syncRun({ runId: 'f', status: 'Failed' }))]
    expect(visible(rows, false)).toHaveLength(2)
    expect(visible(rows, true).map((r) => r.state)).toEqual(['failed'])
  })

  it('says how long a run took', () => {
    expect(took({ started: '2026-11-10T10:00:00Z', queued: null, ended: '2026-11-10T10:00:42Z' })).toBe('42 s')
    expect(took({ started: '2026-11-10T10:00:00Z', queued: null, ended: '2026-11-10T10:30:00Z' })).toBe('30 min')
    expect(took({ started: '2026-11-10T10:00:00Z', queued: null, ended: '2026-11-10T14:00:00Z' })).toBe('4 h')
    expect(took({ started: '2026-11-10T10:00:00Z', queued: null, ended: null })).toBe('')
  })
})

describe('loading', () => {
  it('asks each replication and each runner through the relay, and names a server that does not answer', async () => {
    const asked: string[] = []
    const real = globalThis.fetch
    const answer = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
    globalThis.fetch = (async (url: string) => {
      asked.push(String(url))
      if (url === '/relay/s1/api/replications') return answer(['orders'])
      if (url.startsWith('/relay/s1/api/replications/orders/runs')) return answer({ runs: [syncRun()] })
      if (url.startsWith('/relay/r1/api/v1/runs')) return answer({ runs: [runnerRun()] })
      return answer({ error: 'down' }, 503)
    }) as typeof fetch
    try {
      const down = server('s2', 'Sync down', 'dbdatasync')
      const { rows, problems } = await loadRuns([sync, runner, down])
      expect(asked).toContain('/relay/s1/api/replications/orders/runs?limit=10')
      expect(asked).toContain('/relay/r1/api/v1/runs?limit=100')
      expect(rows.map((r) => r.product)).toEqual(['dbdatarunner', 'dbdatasync'])
      expect(problems.map((p) => p.server)).toEqual(['Sync down'])
    } finally { globalThis.fetch = real }
  })
})

describe('a Build plan run (UI09)', () => {
  const build = server('b1', 'Build prod', 'dbdatabuild')
  const planRun = (over: Partial<BuildPlanRun> = {}): BuildPlanRun => ({ id: 'p1', project: 'sales', plan: 'sqlserver/hourly', started: '2026-11-11T12:15:00Z', finished: '2026-11-11T12:15:40Z', exit: 0, outcome: 'succeeded', ...over })

  it('is a row with the project and the plan, the time it took, and the Build project as the screen to open', () => {
    const row = buildRunRow(build, planRun())
    expect(row).toMatchObject({ product: 'dbdatabuild', subject: 'sales sqlserver/hourly', state: 'succeeded', detail: '', link: '/servers/b1/projects/sales' })
    expect(took(row)).toBe('40 s')
  })

  it('says a failed run failed with its exit code, and one that could not be started says so', () => {
    expect(buildRunRow(build, planRun({ exit: 2, outcome: 'failed' }))).toMatchObject({ state: 'failed', detail: 'The refresh ended with exit code 2' })
    expect(buildRunRow(build, planRun({ exit: -1, outcome: 'failed' })).detail).toBe('The scheduled run could not be started')
  })

  it('is asked of a Build server through the relay', async () => {
    const real = globalThis.fetch
    const asked: string[] = []
    globalThis.fetch = (async (url: string) => { asked.push(String(url)); return new Response(JSON.stringify({ runs: [planRun()] }), { status: 200, headers: { 'content-type': 'application/json' } }) }) as typeof fetch
    try {
      const { rows } = await loadRuns([build])
      expect(asked).toEqual(['/relay/b1/api/v1/plan-runs?limit=50'])
      expect(rows.map((r) => r.product)).toEqual(['dbdatabuild'])
    } finally { globalThis.fetch = real }
  })
})
