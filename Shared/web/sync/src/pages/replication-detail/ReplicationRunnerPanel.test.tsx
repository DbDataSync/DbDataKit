import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { RunnerRow } from './ReplicationRunnerPanel'
import { OWNER_TEXT, isMoveInProgress, movesFrom } from './runnerOwner'
import { ApiError } from '../../api/client'
import type { OwnerView } from '../../api/types'

const view = (patch: Partial<OwnerView>): OwnerView => ({ replication: 'sales', owner: 'runner', link: { server: 'r1', channel: 'prod', package: 'sales-set' }, runner: null, runnerError: null, ...patch })

describe('the moves an owner allows', () => {
  it('observes or cuts over from old, and never rolls back what this server schedules already', () => {
    expect(movesFrom('old')).toEqual({ observe: true, cutover: true, rollback: false })
  })
  it('cuts over or rolls back from observe, but does not observe twice', () => {
    expect(movesFrom('observe')).toEqual({ observe: false, cutover: true, rollback: true })
  })
  it('only rolls back from runner', () => {
    expect(movesFrom('runner')).toEqual({ observe: false, cutover: false, rollback: true })
  })
  it('says in words what each owner does', () => {
    expect(OWNER_TEXT.old).toContain('own worker processes')
    expect(OWNER_TEXT.observe).toContain('nothing runs on the runner')
    expect(OWNER_TEXT.runner).toContain('go to the runner')
  })
})

describe('the runner\'s row of the task', () => {
  it('says the runner has no part while this server schedules', () => {
    expect(renderToStaticMarkup(<RunnerRow view={view({ owner: 'old' })} />)).toContain('data-testid="runner-row-none"')
  })

  it('names what is wrong when the runner could not be asked', () => {
    const html = renderToStaticMarkup(<RunnerRow view={view({ runnerError: 'pairing refused' })} />)
    expect(html).toContain('data-testid="runner-row-error"')
    expect(html).toContain('pairing refused')
  })

  it('says to publish when the runner lists no task', () => {
    expect(renderToStaticMarkup(<RunnerRow view={view({})} />)).toContain('publish the replication set')
  })

  it('shows the owner, the load, the pause and the last run the runner reports', () => {
    const html = renderToStaticMarkup(<RunnerRow view={view({ runner: { task: 'sales', owner: 'runner', nextDue: null, running: 1, queued: 2, paused: true, pausedBy: 'ann', lastRun: { id: 'x', outcome: 'Succeeded', started: '2026-11-09T10:00:00Z', finished: null, attempt: 1, rows: 40 } } })} />)
    expect(html).toContain('1 / 2')
    expect(html).toContain('yes, by ann')
    expect(html).toContain('Succeeded')
    expect(html).toContain('40 rows')
  })
})

describe('a move the console stopped waiting for', () => {
  it('is in progress, and nothing else that fails is', () => {
    expect(isMoveInProgress(new ApiError(504, 'still moving', { inProgress: true }))).toBe(true)
    expect(isMoveInProgress(new ApiError(504, 'gateway timeout'))).toBe(false)
    expect(isMoveInProgress(new ApiError(502, 'did not answer', { inProgress: true }))).toBe(false)
    expect(isMoveInProgress(new ApiError(409, 'refused', { error: 'a mapping is loading' }))).toBe(false)
    expect(isMoveInProgress(new Error('network'))).toBe(false)
  })
})
