import { afterEach, describe, expect, it, vi } from 'vitest'

const run = vi.fn()
vi.mock('./client', () => ({ api: { provisioning: { run: (...args: unknown[]) => run(...args) } } }))

const { followProvisioningRun, provisioningRunPoll } = await import('./hooks')

// plan 28c E21, step d: an Apply starts the `provision` task on the runner and the screen follows that run to its end
describe('followProvisioningRun', () => {
  afterEach(() => run.mockReset())

  it('polls the run until it ended and answers the steps it reported', async () => {
    provisioningRunPoll.intervalMs = 1
    run
      .mockResolvedValueOnce({ runId: 'r1', outcome: 'running', started: 't', finished: null, failure: null, steps: null })
      .mockResolvedValueOnce({
        runId: 'r1', outcome: 'succeeded', started: 't', finished: 't2', failure: null,
        steps: [{ title: 'Create table', succeeded: true, error: null }],
      })

    const result = await followProvisioningRun('orders', { runId: 'r1', task: 'provision', onBehalfOf: 'Ada' })

    expect(run).toHaveBeenCalledTimes(2)
    expect(run).toHaveBeenCalledWith('orders', 'r1')
    expect(result).toEqual({ runId: 'r1', outcome: 'succeeded', steps: [{ title: 'Create table', succeeded: true, error: null }] })
  })

  it('says why when the run ended without reporting its steps', async () => {
    run.mockResolvedValueOnce({ runId: 'r2', outcome: 'failed', started: 't', finished: 't2', failure: 'no run login is bound', steps: null })

    const result = await followProvisioningRun('orders', { runId: 'r2', task: 'provision', onBehalfOf: 'Ada' })

    expect(result.steps).toEqual([{ title: 'The provisioning run', succeeded: false, error: 'no run login is bound' }])
  })

  it('refuses when a provisioning run is already queued or running', async () => {
    await expect(followProvisioningRun('orders', { runId: null, task: 'provision', onBehalfOf: 'Ada' })).rejects.toThrow(/already queued or running/)
    expect(run).not.toHaveBeenCalled()
  })
})
