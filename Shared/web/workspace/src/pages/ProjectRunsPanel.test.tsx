import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { StagedRun } from '../api'
import { ProjectRunsPanel, checkResultOf, checkSummary, buildConnectionsOf, isBuildView, viewProblem, viewResultOf, syncSlotOf, bulkLoadRequests, mappingsOfReplication, passesOf, runnerScreens, segmentingProblem, syncTaskOf } from './ProjectRunsPanel'


describe('the runs of what a draft published (plan 33 SC20o, SC22l)', () => {
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
  it('names a connection\'s slot as the Sync package does (plan 33 SC22h)', () => {
    expect(syncSlotOf('source-db')).toBe('source_db')
    expect(syncSlotOf('CRM')).toBe('crm')
    expect(syncSlotOf('1st')).toBe('c_1st')
  })
  it('says a connection is tested after publish only (plan 33 SC22h)', () => {
    const html = renderToStaticMarkup(
      <QueryClientProvider client={new QueryClient()}><MemoryRouter><ProjectRunsPanel project="p" id="d" kind="sync" paths={['config/connections/source-db.yaml']} /></MemoryRouter></QueryClientProvider>,
    )
    expect(html).toContain('data-testid="wsp-runs-connections"')
    expect(html).toContain('after publish')
    expect(html).not.toContain('data-testid="wsp-runs-test-connection"')
  })
  it('knows the read-only views of a Build package and what each needs (plan 33 SC20p)', () => {
    expect(['tests', 'status', 'compare', 'sqlserver-hourly'].filter(isBuildView)).toEqual(['tests', 'status', 'compare'])
    expect(viewProblem('status', {})).toContain('connection')
    expect(viewProblem('status', { connection: 'sqlserver' })).toBeNull()
    expect(viewProblem('compare', { connection: 'sqlserver', table: 'dbo.orders' })).toContain('compared with')
    expect(viewProblem('compare', { connection: 'sqlserver', table: 'dbo.orders', against_schema: 'dev' })).toBeNull()
    expect(viewProblem('tests', {})).toBeNull()
    expect(buildConnectionsOf(['connections/sqlserver.yaml', 'models/a.yml', 'connections/pg.yml'])).toEqual(['pg', 'sqlserver'])
  })
  it('reads the document a view recorded, whole or its head', () => {
    const run: StagedRun = { id: 'r', task: 'status', outcome: 'succeeded', started: '', finished: '', version: null, failure: null, steps: [{ key: 'status', started: '', completed: '', result: { view: 'status', exit_code: 1, document: { data: { drift: 2 } } } }] }
    expect(viewResultOf(run)?.document).toEqual({ data: { drift: 2 } })
    expect(viewResultOf({ ...run, steps: [] })).toBeNull()
  })
  it('reads what a re-check recorded and says whether anything still blocks (plan 35 I3a)', () => {
    const result = { task: 'check' as const, dataset: null, evaluated: 4, passed: 3, failed: 1, errors: 0, blocking: 1, notes: [], results: [{ dataset: 'mssql://x/db.marts.orders', name: 'marts.orders', check: 'customer_present', status: 'fail' as const, blocking: true }] }
    const run: StagedRun = { id: 'r', task: 'check', outcome: 'succeeded', started: '', finished: '', version: null, failure: null, steps: [{ key: 'check', started: '', completed: '', result: result }] }
    expect(checkResultOf(run)?.results?.[0].name).toBe('marts.orders')
    expect(checkSummary(result)).toContain('1 still blocks')
    expect(checkSummary({ ...result, failed: 0, passed: 4, blocking: 0 })).toContain('Nothing blocks')
    expect(checkResultOf({ ...run, steps: [{ key: 'status', started: '', completed: '', result: { view: 'status' } }] })).toBeNull()
  })
  it('says what to do when nothing is published yet', () => {
    const html = renderToStaticMarkup(
      <QueryClientProvider client={new QueryClient()}><MemoryRouter><ProjectRunsPanel project="p" id="d" kind="sync" paths={['config/replications/orders/task.yaml']} /></MemoryRouter></QueryClientProvider>,
    )
    expect(html).toContain('data-testid="wsp-runs"')
    expect(html).toContain('apply the setup on the Provisioning tab')
  })
  it('says for a Build project that its views run read only on the Runner and what is not built (plan 33 SC20o, SC20p)', () => {
    const html = renderToStaticMarkup(
      <QueryClientProvider client={new QueryClient()}><MemoryRouter><ProjectRunsPanel project="p" id="d" kind="build" paths={['dbdatabuild.yml']} /></MemoryRouter></QueryClientProvider>,
    )
    expect(html).toContain('The tasks of the package this draft published')
    expect(html).toContain('run there too, read only')
    expect(html).not.toContain('wsp-runs-replication')
  })
})
