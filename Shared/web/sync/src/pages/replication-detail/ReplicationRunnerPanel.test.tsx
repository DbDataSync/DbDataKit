import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { RUNNER_TEXT, RunnerRow } from './ReplicationRunnerPanel'
import type { ReplicationRunnerView } from '../../api/types'

const view = (patch: Partial<ReplicationRunnerView>): ReplicationRunnerView => ({ replication: 'sales', task: 'sales', link: { server: 'r1', channel: 'prod', package: 'sales-set' }, runner: null, runnerError: null, ...patch })

describe('who runs a replication (plan 28c E22)', () => {
  it('says the runner runs it and that there is nothing to move', () => {
    expect(RUNNER_TEXT).toContain('The runner schedules this replication')
    expect(RUNNER_TEXT).toContain('starts no process of its own')
  })
})

describe('the runner\'s row of the task', () => {
  it('names what is wrong when the runner could not be asked, or when none is linked', () => {
    const html = renderToStaticMarkup(<RunnerRow view={view({ runnerError: 'No runner is linked' })} />)
    expect(html).toContain('data-testid="runner-row-error"')
    expect(html).toContain('No runner is linked')
  })

  it('says to publish when the runner lists no task', () => {
    expect(renderToStaticMarkup(<RunnerRow view={view({})} />)).toContain('publish the replication set')
  })

  it('shows the load, the pause and the last run the runner reports', () => {
    const html = renderToStaticMarkup(<RunnerRow view={view({ runner: { task: 'sales', owner: 'runner', nextDue: null, running: 1, queued: 2, paused: true, pausedBy: 'ann', lastRun: { id: 'x', outcome: 'Succeeded', started: '2026-11-09T10:00:00Z', finished: null, attempt: 1, rows: 40 } } })} />)
    expect(html).toContain('1 / 2')
    expect(html).toContain('yes, by ann')
    expect(html).toContain('Succeeded')
    expect(html).toContain('40 rows')
  })
})
