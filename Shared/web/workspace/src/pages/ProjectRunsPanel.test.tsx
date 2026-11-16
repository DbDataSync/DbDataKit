import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { BuildDetail, StagedRun } from '../api'
import { ProjectRunsPanel, bulkLoadRequests, mappingsOfReplication, passesOf, publishedVersionOf, runnerScreens, segmentingProblem, syncTaskOf } from './ProjectRunsPanel'

const detail = (id: string, publishes: BuildDetail['publishes']): BuildDetail => ({ build: { id } as BuildDetail['build'], result: null, publishes })

describe('the runs of what a draft published (plan 33 SC20o, SC22l)', () => {
  it('finds the newest publish to a Runner that worked, with its tasks and mode; a preview or a failure is not one', () => {
    const found = publishedVersionOf([
      detail('b2', [{ at: '', by: 'g', target: 'jobs', ok: false, error: 'no' }, { at: '', by: 'g', target: 'gw', ok: true, preview: 'x', answer: { package: 'p', channel: 'c', version: 9 } }]),
      detail('b1', [{ at: '', by: 'g', target: 'jobs', ok: true, answer: { package: 'orders-set', channel: 'prod', version: 3, mode: 'serve', tasks: ['orders', 'provision'] } }]),
    ])
    expect(found).toEqual({ build: 'b1', target: 'jobs', package: 'orders-set', channel: 'prod', version: '3', mode: 'serve', tasks: ['orders', 'provision'] })
    expect(publishedVersionOf([detail('b', [])])).toBeNull()
  })
  it('names a replication\'s task as the Runner does', () => {
    expect(syncTaskOf('Orders Daily')).toBe('orders-daily')
    expect(syncTaskOf('_sales.eu')).toBe('sales-eu')
  })
  it('lists the mappings of one replication only', () => {
    const paths = ['config/replications/orders/task.yaml', 'config/replications/orders/table-mappings/lines.yaml', 'config/replications/orders/table-mappings/head.yaml', 'config/replications/stock/table-mappings/items.yaml']
    expect(mappingsOfReplication(paths, 'orders')).toEqual(['head', 'lines'])
  })
  it('asks a bulk load as a person thinks of it; the drain divides it (an empty list is the default segmenting)', () => {
    expect(JSON.parse(bulkLoadRequests('lines', { how: 'default' }))).toEqual([{ kind: 'BulkLoad', mapping: 'lines', segments: '[]' }])
    expect(JSON.parse(JSON.parse(bulkLoadRequests('lines', { how: 'full' }))[0].segments)).toEqual([{ mode: 'full' }])
    expect(JSON.parse(JSON.parse(bulkLoadRequests('lines', { how: 'auto', column: ' Id ', buckets: 8 }))[0].segments)).toEqual([{ mode: 'auto', column: 'Id', bucketCount: 8 }])
    expect(segmentingProblem({ how: 'auto', column: '', buckets: 8 })).toContain('column')
    expect(segmentingProblem({ how: 'auto', column: 'Id', buckets: 1 })).toContain('2 to 1000')
    expect(segmentingProblem({ how: 'default' })).toBeNull()
  })
  it('links to the Runner\'s tasks, runs and one run in the console', () => {
    const s = runnerScreens('run-1', 'orders-set')
    expect(s.tasks).toBe('/servers/run-1/?package=orders-set')
    expect(s.runs).toBe('/servers/run-1/runs?package=orders-set')
    expect(s.run('abc')).toBe('/servers/run-1/runs?run=abc')
  })
  it('reads what each pass of a drain did from its records', () => {
    const run: StagedRun = { id: 'r', task: 'orders', outcome: 'failed', started: '', finished: '', version: null, failure: null, steps: [
      { key: 'a', started: '', completed: '', result: { mapping: 'lines', kind: 'Primary', status: 'Failed', failureKind: 'TargetTableMissing', summary: 'The target table x does not exist yet' } },
      { key: 'b', started: '', completed: '', result: null },
    ] }
    expect(passesOf(run)).toHaveLength(1)
    expect(passesOf(run)[0].failureKind).toBe('TargetTableMissing')
  })
  it('says what to do when nothing is published yet', () => {
    const html = renderToStaticMarkup(
      <QueryClientProvider client={new QueryClient()}><MemoryRouter><ProjectRunsPanel project="p" id="d" kind="sync" paths={['config/replications/orders/task.yaml']} /></MemoryRouter></QueryClientProvider>,
    )
    expect(html).toContain('data-testid="wsp-runs"')
    expect(html).toContain('apply the setup on the Provisioning tab')
  })
})
