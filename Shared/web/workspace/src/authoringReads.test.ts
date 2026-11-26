import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, setApiBase } from '@dbdatakit/shell'
import { catalogReadApi } from './catalogRefresh'
import { authoringReadApi, buildAndStage, describedLine, needsBuildOf, type StageCalls, newestPerChannel, previewLine, previewValue, setUpChannels, setUpOffered, testLine, trialLine, type DraftTestAnswer, type MappingPreviewAnswer, type StrategyTrialAnswer } from './authoringReads'
import type { ChannelList } from './catalogRefresh'
import type { DeployRecord } from './deploys'

const list = (channels: ChannelList['channels']): ChannelList => ({ package: 'crm-to-dw', connection: 'crm', slot: 'crm', runner: 'r-1', runner_name: 'runners-eu', default: null, channels })
const dev = (bound: boolean) => ({ channel: 'dev', production: false, bound, bound_to: bound ? 'crm_dev' : null })
const prod = (bound: boolean) => ({ channel: 'prod', production: true, bound, bound_to: bound ? 'crm' : null })

describe('Set up for development in the channel picker', () => {
  it('is offered where a channel does not bind the slot, production as any other, or where the Runner lists no channel', () => {
    expect(setUpOffered(undefined)).toBe(false)
    expect(setUpOffered(list([dev(true), prod(true)]))).toBe(false)
    expect(setUpOffered(list([dev(false), prod(true)]))).toBe(true)
    expect(setUpOffered(list([dev(true), prod(false)]))).toBe(true)
    expect(setUpOffered(list([prod(false)]))).toBe(true)
    // an older Runner lists no channel of a package it holds no version of
    expect(setUpOffered(list([]))).toBe(true)
  })

  it('offers the unbound channels, the ones that are not production first, and dev when the Runner lists none', () => {
    expect(setUpChannels(list([dev(false), { channel: 'test', production: false, bound: true, bound_to: 'x' }, prod(false)]))).toEqual(['dev', 'prod'])
    expect(setUpChannels(list([prod(false), dev(false)]))).toEqual(['dev', 'prod'])
    expect(setUpChannels(list([prod(false)]))).toEqual(['prod'])
    expect(setUpChannels(list([prod(true)]))).toEqual([])
    expect(setUpChannels(list([]))).toEqual(['dev'])
    expect(setUpChannels(list([dev(true)]))).toEqual([])
  })
})

describe('the words of a test and a description', () => {
  const answer: DraftTestAnswer = {
    package: 'crm-to-dw', connection: 'crm', slot: 'crm', channel: 'prod', production: true, role: 'default', runner: 'r-1', runner_name: 'runners-eu',
    ok: false, step: 'binding', message: "The slot 'crm' is not bound in channel 'prod'.", connected: false, bound_to: null, steps: [],
  }
  it("says the Runner's reason, or where it stopped when it gave none", () => {
    expect(testLine(answer)).toBe("The slot 'crm' is not bound in channel 'prod'.")
    expect(testLine({ ...answer, message: null })).toBe('The test of crm in prod stopped at binding.')
    expect(testLine({ ...answer, ok: true, step: 'connect', message: null, channel: 'dev', bound_to: 'crm_dev' })).toBe('crm opened in dev through crm_dev.')
  })

  it('lists the columns of a query with their types', () => {
    expect(describedLine([{ name: 'id', type: 'int', nullable: false }, { name: 'segment', type: 'nvarchar(20)', nullable: true }])).toBe('2 columns: id int, segment nvarchar(20) null')
    expect(describedLine([])).toBe('The query has no column.')
  })
})

describe('the newest check per channel', () => {
  const record = (id: string, target: string, channel: string, over: Partial<DeployRecord> = {}): DeployRecord => ({
    id, workspace_server: 'ws-1', project: 'crm-to-dw', workspace: 'ws', build: 'b', item: 'crm-to-dw', kind: 'sync', target, target_name: target === 'r-1' ? 'runners-eu' : 'runners-us',
    product: 'runner', route: 'x', name: 'crm-to-dw', channel, version: 2, mode: 'serve', signature: null, deployed_by: 'ana', deployed_at: '2026-11-22T10:00:00Z', outcome: 'ok', error: null, tasks: ['preflight'], ...over,
  })
  it('takes the newest deploy of each Runner and channel, and leaves out refused deploys and Gateways', () => {
    const got = newestPerChannel([
      record('d5', 'r-1', 'dev', { outcome: 'refused' }),
      record('d4', 'r-1', 'prod'),
      record('d3', 'r-1', 'dev'),
      record('d2', 'r-1', 'dev'),
      record('d1', 'g-1', 'prod', { product: 'gateway' }),
      record('d0', 'r-2', 'dev'),
    ])
    expect(got.map((v) => `${v.deploy} ${v.target} ${v.channel}`)).toEqual(['d4 r-1 prod', 'd3 r-1 dev', 'd0 r-2 dev'])
  })
})

describe('the console routes', () => {
  afterEach(() => { setApiBase(''); vi.unstubAllGlobals() })
  it('asks the console, never the Workspace server, even when the screens talk to one through the relay', async () => {
    setApiBase('/relay/ws-1')
    const asked: string[] = []
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      asked.push(`${init?.method ?? 'GET'} ${String(input)}${init?.body ? ` ${String(init.body)}` : ''}`)
      return Promise.resolve(new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } }))
    }))
    await authoringReadApi.test('ws-1', 'crm-to-dw', { runner: 'r-1', channel: 'dev', connection: 'crm' })
    await authoringReadApi.describe('ws-1', 'crm-to-dw', { runner: 'r-1', channel: 'dev', connection: 'crm', sql: 'SELECT 1 AS one' })
    await authoringReadApi.followDescribe('ws-1', 'crm-to-dw', 'run-1', { runner: 'r-1', channel: 'dev', connection: 'crm' })
    await authoringReadApi.newest('d-1', 'preflight', 'preflight-findings')
    // a read or a description by the project's own package names it, and its follow says so too
    await authoringReadApi.followDescribe('ws-1', 'crm-to-dw', 'run-2', { runner: 'r-1', channel: 'dev', connection: 'crm', reader: 'package' })
    await catalogReadApi.follow('ws-1', 'crm-to-dw', 'run-3', { runner: 'r-1', channel: 'dev', connection: 'crm', reader: 'package' })
    await catalogReadApi.follow('ws-1', 'crm-to-dw', 'run-4', { runner: 'r-1', channel: 'dev', connection: 'crm', reader: 'builtin' })
    expect(asked).toEqual([
      'POST /api/workspace-catalog/ws-1/crm-to-dw/binding-test {"runner":"r-1","channel":"dev","connection":"crm"}',
      'POST /api/workspace-catalog/ws-1/crm-to-dw/describe {"runner":"r-1","channel":"dev","connection":"crm","sql":"SELECT 1 AS one"}',
      'GET /api/workspace-catalog/ws-1/crm-to-dw/describe/run-1?runner=r-1&channel=dev&connection=crm',
      'GET /api/deploys/d-1/runs?task=preflight&step=preflight-findings',
      'GET /api/workspace-catalog/ws-1/crm-to-dw/describe/run-2?runner=r-1&channel=dev&connection=crm&reader=package',
      'GET /api/workspace-catalog/ws-1/crm-to-dw/read/run-3?runner=r-1&channel=dev&connection=crm&reader=package',
      'GET /api/workspace-catalog/ws-1/crm-to-dw/read/run-4?runner=r-1&channel=dev&connection=crm',
    ])
  })
})

describe('the words of a preview and a trial', () => {
  const preview = (rows: MappingPreviewAnswer['rows'], more: boolean, source = 'sales.dbo.orders'): MappingPreviewAnswer =>
    ({ replication: 'orders', mapping: 'main', connection: 'erp', source, columns: ['id'], rows, more, limit: 20 })
  it('says how many rows of which source were read and whether more follow', () => {
    expect(previewLine(preview([[1], [2]], true))).toBe('2 rows of sales.dbo.orders on erp, more follow.')
    expect(previewLine(preview([[1]], false, 'query'))).toBe('1 row of the query on erp.')
    expect(previewLine(preview([], false))).toBe('No row of sales.dbo.orders on erp matches the mapping.')
    expect([previewValue(null), previewValue(7), previewValue(false)]).toEqual(['NULL', '7', 'false'])
  })

  it('says how many candidates a strategy proposed and how many it selected', () => {
    const trial = (selected: boolean[]): StrategyTrialAnswer => ({
      replication: 'orders', mapping: 'main', strategy: 'by-month', kind: 'DuckDb', column: 'placed_at', readsADatabase: false,
      candidates: selected.map((s, i) => ({ label: `m${i}`, column: 'placed_at', start: '1', end: '2', segment: `m${i}`, selected: s })),
    })
    expect(trialLine(trial([true, false, true]))).toBe('3 candidates of by-month over placed_at, 2 selected.')
    expect(trialLine(trial([]))).toBe('The strategy by-month proposed no candidate.')
  })

  it('asks the console to wait for the answer of the run it starts', async () => {
    const fetcher = vi.fn(() => Promise.resolve(new Response(JSON.stringify({ state: 'running', run_id: 'r' }), { status: 202, headers: { 'Content-Type': 'application/json' } })))
    vi.stubGlobal('fetch', fetcher)
    await authoringReadApi.answer('d-1', 'preview', { replication: 'orders' })
    expect(fetcher).toHaveBeenCalledWith('/api/deploys/d-1/runs', expect.objectContaining({ method: 'POST', body: JSON.stringify({ task: 'preview', inputs: { replication: 'orders' }, answer: true }) }))
    vi.unstubAllGlobals()
  })
})

describe('a read by the project\'s own package that needs a build', () => {
  const needs = { error: 'runners-eu holds no build of crm-to-dw in \'dev\'.', needs_build: true, package: 'crm-to-dw', task: 'discover', runner: 'r-1', runner_name: 'runners-eu', channel: 'dev', production: false, connection: 'crm' }

  it('is told apart from any other refusal', () => {
    expect(needsBuildOf(new ApiError(409, needs.error, needs))?.channel).toBe('dev')
    expect(needsBuildOf(new ApiError(409, 'held', { error: 'held' }))).toBeNull()
    expect(needsBuildOf(new ApiError(422, 'failed', { ...needs }))).toBeNull()
    expect(needsBuildOf(new Error('x'))).toBeNull()
  })

  it('builds the draft, waits for the build, and stages it in the channel through the console\'s deploy', async () => {
    const asked: string[] = []
    let looks = 0
    const calls: StageCalls = {
      requestBuild: async (project, id) => { asked.push(`build ${project}/${id}`); return { id: 'b-7', workspace: id, status: 'queued', commit: null, requested_by: 'ana', message: null, requested: '', started: null, finished: null } },
      build: async (_p, _i, build) => { looks++; asked.push(`look ${build}`); return { build: { id: build, workspace: 'ws', status: looks < 2 ? 'running' : 'succeeded', commit: null, requested_by: 'ana', message: null, requested: '', started: null, finished: null }, result: null, publishes: [] } },
      deploy: async (body) => { asked.push(`deploy ${JSON.stringify(body)}`); return { steps: ['Staged.'], deployed: {} as never, deploy: {} as never } },
    }
    const steps: string[] = []
    await buildAndStage({ workspaceServer: 'ws-1', project: 'crm-to-dw', draft: 'ana-crm', runner: 'r-1', channel: 'dev' }, (l) => steps.push(l), calls, async () => {})
    expect(asked).toEqual([
      'build crm-to-dw/ana-crm', 'look b-7', 'look b-7',
      'deploy {"workspaceServer":"ws-1","project":"crm-to-dw","workspace":"ana-crm","build":"b-7","target":"r-1","channel":"dev"}',
    ])
    expect(steps).toEqual(['Building the draft...', 'Built as b-7. Staging it in dev...'])

    // a build that fails stops it before anything is deployed
    const failing: StageCalls = { ...calls, requestBuild: async () => ({ id: 'b-8', workspace: 'ana-crm', status: 'failed', commit: null, requested_by: 'ana', message: null, requested: '', started: null, finished: null }) }
    asked.length = 0
    await expect(buildAndStage({ workspaceServer: 'ws-1', project: 'crm-to-dw', draft: 'ana-crm', runner: 'r-1', channel: 'dev' }, undefined, failing, async () => {})).rejects.toThrow('The build b-8 of the draft failed')
    expect(asked).toEqual([])
  })
})
