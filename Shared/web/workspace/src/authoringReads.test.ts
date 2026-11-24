import { afterEach, describe, expect, it, vi } from 'vitest'
import { setApiBase } from '@dbdatakit/shell'
import { authoringReadApi, bindingLeftLine, describedLine, newestPerChannel, setUpChannels, setUpOffered, testLine, type DraftTestAnswer } from './authoringReads'
import type { ChannelList } from './catalogRefresh'
import type { DeployRecord } from './deploys'

const list = (channels: ChannelList['channels']): ChannelList => ({ package: 'crm-to-dw', connection: 'crm', slot: 'crm', runner: 'r-1', runner_name: 'runners-eu', default: null, channels })
const dev = (bound: boolean) => ({ channel: 'dev', production: false, bound, bound_to: bound ? 'crm_dev' : null })
const prod = (bound: boolean) => ({ channel: 'prod', production: true, bound, bound_to: bound ? 'crm' : null })

describe('Set up for development in the channel picker', () => {
  it('is offered where a channel other than production does not bind the slot, or where nothing binds it', () => {
    expect(setUpOffered(undefined)).toBe(false)
    expect(setUpOffered(list([dev(true), prod(true)]))).toBe(false)
    expect(setUpOffered(list([dev(false), prod(true)]))).toBe(true)
    expect(setUpOffered(list([prod(false)]))).toBe(true)
    // the Runner holds no version of the package yet: it lists no channel
    expect(setUpOffered(list([]))).toBe(true)
  })

  it('offers the unbound channels that are not production, and dev when the Runner lists none', () => {
    expect(setUpChannels(list([dev(false), { channel: 'test', production: false, bound: true, bound_to: 'x' }, prod(false)]))).toEqual(['dev'])
    expect(setUpChannels(list([prod(true)]))).toEqual(['dev'])
    expect(setUpChannels(list([]))).toEqual(['dev'])
    expect(setUpChannels(list([dev(true)]))).toEqual([])
  })

  it('says what is left once the login is stored and the channel does not bind the slot yet', () => {
    expect(bindingLeftLine('crm', 'dev', 'runners-eu', 'dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password')).toBe(
      'The slot crm is not bound in dev yet. An administrator binds it on the Packages page of runners-eu (Bindings), to a connection whose password names the secret dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password; the picker offers dev once it binds the slot.')
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
    expect(asked).toEqual([
      'POST /api/workspace-catalog/ws-1/crm-to-dw/binding-test {"runner":"r-1","channel":"dev","connection":"crm"}',
      'POST /api/workspace-catalog/ws-1/crm-to-dw/describe {"runner":"r-1","channel":"dev","connection":"crm","sql":"SELECT 1 AS one"}',
      'GET /api/workspace-catalog/ws-1/crm-to-dw/describe/run-1?runner=r-1&channel=dev&connection=crm',
      'GET /api/deploys/d-1/runs?task=preflight&step=preflight-findings',
    ])
  })
})
