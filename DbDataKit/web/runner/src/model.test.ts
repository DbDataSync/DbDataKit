import { describe, expect, it } from 'vitest'
import { runsQuery, type StagingInfo, type TaskStatus } from './api'
import { activationBody, capabilityLines, headText, isGone, outcomeText, taskState } from './model'

const task = (over: Partial<TaskStatus> = {}): TaskStatus => ({
  package: 'sample', channel: 'dev', task: 'ok', version: 1, next_due: null, last_run: null, running: 0, queued: 0, next_attempt_at: null,
  paused: false, paused_by: null, problem: null, required_labels: [], waiting_reason: null, ...over,
})

const staging = (over: Partial<StagingInfo> = {}): StagingInfo => ({ channel: 'dev', version: 2, summary: '1 of 1 ready', all_ready: true, failed: [], losses: [], needs_confirmation: false, ...over })

describe('taskState', () => {
  it('puts running before a hold, a hold before a wait and a wait before a problem', () => {
    expect(taskState(task({ running: 1, paused: true }))).toBe('running')
    expect(taskState(task({ paused: true, waiting_reason: 'x' }))).toBe('paused')
    expect(taskState(task({ waiting_reason: "No live runner has the label 'gpu'", problem: 'p' }))).toBe('waiting')
    expect(taskState(task({ problem: 'p', queued: 1 }))).toBe('problem')
    expect(taskState(task({ queued: true }))).toBe('queued')
    expect(taskState(task())).toBe('idle')
  })
})

describe('words', () => {
  it('names outcomes, channels and capabilities', () => {
    expect(outcomeText('completedwithfailures')).toBe('Completed with failures')
    expect(outcomeText('failed')).toBe('Failed')
    expect(headText({ channel: 'prod', active: 3, previous: 2, staged: 4 })).toBe('v3 active, v2 previous, v4 staged')
    expect(headText({ channel: 'prod', active: null, previous: null, staged: 1 })).toBe('nothing served, v1 staged')
    expect(capabilityLines({ memory_limit: 'enforced', cpu_limit: 'watched', network: 'outside', os: 'linux' })).toEqual(['memory limit enforced', 'cpu limit watched', 'network outside', 'linux'])
  })

  it('says an instance is gone only when the runner says it is not live', () => {
    const base = { id: 'a', host: 'h', version: '1', started: '', heartbeat: '', this: false, labels: [], capabilities: {}, running: 0 }
    expect(isGone({ ...base })).toBe(false)
    expect(isGone({ ...base, live: true })).toBe(false)
    expect(isGone({ ...base, live: false, state: 'Gone' })).toBe(true)
  })
})

describe('runsQuery', () => {
  it('sends only the filters that are set', () => {
    expect(runsQuery({})).toBe('?limit=100')
    expect(runsQuery({ package: 'sample', outcome: 'failed' })).toBe('?package=sample&outcome=failed&limit=100')
  })
})

describe('activationBody', () => {
  it('sends the expected version and nothing else when the version loses nothing', () => {
    expect(activationBody(staging(), 1, { confirmed: false, anyway: false, reason: '' })).toEqual({ channel: 'dev', expected_version: 1 })
    expect(activationBody(staging(), null, { confirmed: false, anyway: false, reason: '' })?.expected_version).toBe(0)
  })

  it('needs the confirmation of the losses and echoes the list the runner named', () => {
    const lossy = staging({ needs_confirmation: true, losses: ['tally: seen'] })
    expect(activationBody(lossy, 1, { confirmed: false, anyway: false, reason: '' })).toBeNull()
    expect(activationBody(lossy, 1, { confirmed: true, anyway: false, reason: '' })).toEqual({ channel: 'dev', expected_version: 1, confirm_losses: true, losses: ['tally: seen'] })
  })

  it('needs a reason to go ahead anyway', () => {
    const waiting = staging({ all_ready: false })
    expect(activationBody(waiting, 1, { confirmed: false, anyway: true, reason: '  ' })).toBeNull()
    expect(activationBody(waiting, 1, { confirmed: false, anyway: true, reason: ' runner 2 is being replaced ' })).toEqual({ channel: 'dev', expected_version: 1, anyway: true, reason: 'runner 2 is being replaced' })
  })
})
