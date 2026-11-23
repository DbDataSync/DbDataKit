import { describe, expect, it } from 'vitest'
import { apiUrl, setApiBase } from '@dbdatakit/shell'
import { deployBody, deployLine, deployTargets, hostedServer, runnerVersionOf, type DeployRecord } from './deploys'

const servers = [
  { id: 'ws-1', name: 'Workspace', product: 'dbdataworkspace', reachable: true },
  { id: 'r-1', name: 'jobs', product: 'dbdatarunner', reachable: true },
  { id: 'g-1', name: 'edge', product: 'dbdatagateway', reachable: false },
  { id: 'x-1', name: 'old', product: null, reachable: false },
]
const kind = (...products: string[]) => ({ name: 'k', title: 'K', description: '', buildable: false, publishes: products.map((product) => ({ product, what: 'x', description: '' })) })

describe('the console\'s deploy, as the Workspace\'s screens ask for it', () => {
  it('offers the Runners and Gateways of the console, each accepted when the kind goes to its product', () => {
    expect(deployTargets(servers, kind('runner'))).toEqual([
      { id: 'g-1', name: 'edge', product: 'gateway', reachable: false, accepts: false },
      { id: 'r-1', name: 'jobs', product: 'runner', reachable: true, accepts: true },
    ])
    expect(deployTargets(servers, kind('runner', 'gateway')).every((t) => t.accepts)).toBe(true)
    expect(deployTargets(servers, undefined).some((t) => t.accepts)).toBe(false)
  })

  it('sends the build\'s place, the target and the form\'s options in the console\'s names, and leaves out what is empty', () => {
    const at = { workspaceServer: 'ws-1', project: 'orders', workspace: 'd1', build: 'b1' }
    expect(deployBody(at, 'r-1', '', { channel: 'dev', bind: { source: 'erp' } })).toEqual({ ...at, target: 'r-1', channel: 'dev', bind: { source: 'erp' } })
    expect(deployBody(at, 'g-1', 'first', { data_source: 'mdm', adopt: ['mdm'], bind: {}, serve: false })).toEqual({ ...at, target: 'g-1', note: 'first', dataSource: 'mdm', adopt: ['mdm'] })
    expect(deployBody(at, 'r-1', '', { channel: 'prod', serve: true, model: 'sales' })).toEqual({ ...at, target: 'r-1', channel: 'prod', serve: true, model: 'sales' })
  })

  it('says a kept deploy in a sentence, or the refusal in the target\'s words', () => {
    const record: DeployRecord = { id: 'd1', workspace_server: 'ws-1', project: 'orders', workspace: 'd1', build: 'b1', item: 'package', kind: 'sync', target: 'r-1', target_name: 'jobs', product: 'runner', route: 'package',
      name: 'orders', channel: 'dev', version: 3, mode: 'stage', signature: 'verified', deployed_by: 'ana', deployed_at: '', outcome: 'ok', error: null }
    expect(deployLine(record)).toBe('Staged orders on dev of jobs as version 3.')
    expect(deployLine({ ...record, mode: 'serve' })).toBe('Published orders on dev of jobs as version 3.')
    expect(deployLine({ ...record, product: 'gateway', target_name: 'edge', name: 'sales', channel: null, version: null })).toBe('Published sales to edge.')
    expect(deployLine({ ...record, outcome: 'refused', error: 'This build is failed, so there is nothing to publish.' })).toBe('This build is failed, so there is nothing to publish.')
  })

  it('reads the Workspace server from the relay the screens are hosted on, and keeps the deploys on the host', () => {
    expect(hostedServer('/relay/ws-1')).toBe('ws-1')
    expect(hostedServer('')).toBeNull()
    setApiBase('/relay/ws-1')
    try {
      expect(apiUrl('/api/deploys?workspace_server=ws-1')).toBe('/api/deploys?workspace_server=ws-1')
      expect(apiUrl('/api/instances')).toBe('/api/instances')
      expect(apiUrl('/api/deploys/d1/runs/r1?step=pass-record')).toBe('/api/deploys/d1/runs/r1?step=pass-record')
      expect(apiUrl('/api/v1/projects')).toBe('/relay/ws-1/api/v1/projects')
    } finally { setApiBase('') }
  })
})

describe('the version a deploy put on a Runner, which the Runs, Provisioning and Deploy tabs run', () => {
  const record = (over: Partial<DeployRecord>): DeployRecord => ({ id: 'd1', workspace_server: 'ws-1', project: 'orders', workspace: 'w', build: 'b1', item: 'package', kind: 'sync', target: 'r-1', target_name: 'jobs', product: 'runner',
    route: 'package', name: 'orders', channel: 'dev', version: 3, mode: 'stage', signature: 'verified', deployed_by: 'ana', deployed_at: '', outcome: 'ok', error: null, tasks: ['orders', 'provision'], ...over })

  it('takes the newest deploy to a Runner that the Runner took, never a refused one or one to a Gateway', () => {
    const newestFirst = [record({ id: 'd4', outcome: 'refused', error: 'no', version: null }), record({ id: 'd3', product: 'gateway', target: 'g-1', channel: null, version: 1 }), record({ id: 'd2', build: 'b2', version: 4, mode: 'serve' }), record({})]
    expect(runnerVersionOf(newestFirst)).toEqual({ deploy: 'd2', build: 'b2', target: 'r-1', targetName: 'jobs', package: 'orders', channel: 'dev', version: '4', mode: 'serve', tasks: ['orders', 'provision'] })
    expect(runnerVersionOf([record({ outcome: 'refused', error: 'no' })])).toBeNull()
  })

  it('takes the deploy of the package named, for a Store build that stages one package per model', () => {
    const newestFirst = [record({ id: 'd2', name: 'model-regions', item: 'model-package/regions' }), record({ id: 'd1', name: 'model-sales', item: 'model-package/sales' })]
    expect(runnerVersionOf(newestFirst, 'model-sales')?.deploy).toBe('d1')
    expect(runnerVersionOf(newestFirst, 'model-other')).toBeNull()
  })
})
