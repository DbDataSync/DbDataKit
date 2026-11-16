import { describe, expect, it } from 'vitest'
import { loadRuns, runnerRunRow, sortRuns, took, visible, type RunnerRun } from './runs'
import type { InstanceSummary } from './api'

const server = (id: string, name: string, product: string): InstanceSummary => ({ id, name, url: 'https://x', product, version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null })
const runner = server('r1', 'Runner prod', 'dbdatarunner')

const runnerRun = (over: Partial<RunnerRun> = {}): RunnerRun => ({ id: 'x', package: 'etl', channel: 'prod', task: 'extract', outcome: 'succeeded', started: '2026-11-10T11:00:00Z', finished: '2026-11-10T11:00:30Z', rows: 3, failed_item: null, failure: null, ...over })

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
  it('is newest first across runners, with a run of unknown time last', () => {
    const other = server('r2', 'Runner test', 'dbdatarunner')
    const rows = sortRuns([
      runnerRunRow(other, runnerRun({ id: '1', started: '2026-11-10T09:00:00Z', task: 'old' })),
      runnerRunRow(runner, runnerRun()),
      runnerRunRow(other, runnerRun({ id: '2', started: '', task: 'none' })),
    ])
    expect(rows.map((r) => r.subject)).toEqual(['etl/prod/extract', 'etl/prod/old', 'etl/prod/none'])
  })

  it('shows only the failures when asked', () => {
    const rows = [runnerRunRow(runner, runnerRun()), runnerRunRow(runner, runnerRun({ id: 'f', outcome: 'failed' }))]
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
  it('asks each runner through the relay, names a runner that does not answer, and asks no other server', async () => {
    const asked: string[] = []
    const real = globalThis.fetch
    const answer = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
    globalThis.fetch = (async (url: string) => {
      asked.push(String(url))
      if (url.startsWith('/relay/r1/api/v1/runs')) return answer({ runs: [runnerRun()] })
      return answer({ error: 'down' }, 503)
    }) as typeof fetch
    try {
      const down = server('r2', 'Runner down', 'dbdatarunner')
      const { rows, problems } = await loadRuns([runner, down, server('g1', 'Gateway', 'dbdatagateway')])
      expect(asked).toContain('/relay/r1/api/v1/runs?limit=100')
      expect(asked.some((u) => u.startsWith('/relay/g1'))).toBe(false)
      expect(rows.map((r) => r.product)).toEqual(['dbdatarunner'])
      expect(problems.map((p) => p.server)).toEqual(['Runner down'])
    } finally { globalThis.fetch = real }
  })
})
