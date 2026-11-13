import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { RunnerStatusRow } from './RunnerStatusRow'
import type { RunnerTaskState } from '../../api/types'

const up: RunnerTaskState = {
  reachable: true, running: true, runningCount: 1, queued: 0, paused: false, pausedBy: null,
  nextDueUtc: null, lastRun: { id: '1', outcome: 'succeeded', started: '2026-11-13T05:00:00Z', finished: '2026-11-13T05:02:00Z', attempt: 1, rows: 40 }, error: null,
}

describe('the runner row of the status card', () => {
  it('draws nothing for a replication this server runs itself', () => {
    expect(renderToStaticMarkup(<RunnerStatusRow runner={null} />)).toBe('')
    expect(renderToStaticMarkup(<RunnerStatusRow runner={undefined} />)).toBe('')
  })

  it('says a drain is running, the last run and its rows', () => {
    const html = renderToStaticMarkup(<RunnerStatusRow runner={up} />)
    expect(html).toContain('a drain is running')
    expect(html).toContain('Last run succeeded, 40 rows')
  })

  it('says queued, and paused by whom', () => {
    const html = renderToStaticMarkup(<RunnerStatusRow runner={{ ...up, running: false, runningCount: 0, queued: 1, paused: true, pausedBy: 'ada', lastRun: null }} />)
    expect(html).toContain('a drain is queued')
    expect(html).toContain('paused by ada')
    expect(html).not.toContain('Next due')
  })

  it('says a runner that could not be asked, never that nothing runs there', () => {
    const html = renderToStaticMarkup(<RunnerStatusRow runner={{ ...up, reachable: false, running: false, runningCount: 0, lastRun: null, error: 'connection refused' }} />)
    expect(html).toContain('runner-status-unreachable')
    expect(html).toContain('connection refused')
    expect(html).not.toContain('no drain is up')
  })
})
