import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { BuildSummary, StagedRun, StagedRunStarted } from '../api'
import { ContractResult, DeployResult, StoreDeployPanel, allowancesOf, canApply, canContract, contractReportOf, deployReportOf, followBuild, modelsOf, packageOf, reportLine, storageConnectionOf, type ContractReport, type DeployReport } from './StoreDeployPanel'

const report = (over: Partial<DeployReport> = {}): DeployReport =>
  ({ model: 'territories', schema: 'territories', engine: 'postgres', action: 'create', dryRun: true, applied: false, statements: ['create table territory', 'create table territory_history'], blocked: [], notes: [], ...over })

const run = (r: DeployReport | null, over: Partial<StagedRun> = {}): StagedRun => ({
  id: '11111111-2222-3333-4444-555555555555', task: 'deploy', outcome: 'succeeded', started: '2026-11-15T10:00:00Z', finished: '2026-11-15T10:00:02Z', version: '3', failure: null,
  steps: r ? [{ key: r.schema, started: '2026-11-15T10:00:01Z', completed: '2026-11-15T10:00:01Z', result: r }] : [], ...over,
})

const started: StagedRunStarted = { run_id: '11111111-2222-3333-4444-555555555555', task: 'deploy', package: 'model-territories', channel: 'prod', version: '3', target: 'jobs', runner: 'https://runner:5443', instance: 'runner-1', on_behalf_of: 'grace' }

describe('deploying a Store model on a Runner (plan 33 SC24)', () => {
  it('lists the models of a draft from models/, and names each package as the Runner library does', () => {
    expect(modelsOf(['README.md', 'models/territories.yaml', 'models/regions.yml', 'models/sub/x.yaml', 'models/notes.txt'])).toEqual(['regions', 'territories'])
    expect(packageOf('Sales.Territories')).toBe('model-sales-territories')
    expect(packageOf('x'.repeat(80))).toHaveLength(64)
  })
  it('reads the connection the model stores its rows in, or nothing from a text that is not a model yet', () => {
    expect(storageConnectionOf('schemaVersion: 2\nname: t\nstorage: {connection: mdm}\n')).toBe('mdm')
    expect(storageConnectionOf('name: [')).toBe('')
    expect(storageConnectionOf('name: t\n')).toBe('')
  })
  it('allows the deploy only after a dry run that plans a change and has nothing blocked', () => {
    expect(canApply(report())).toBe(true)
    expect(canApply(report({ blocked: ['dropping the column note is a contract step'] }))).toBe(false)
    expect(canApply(report({ action: 'none', statements: [] }))).toBe(false)
    expect(canApply(report({ dryRun: false, applied: true }))).toBe(false)
    expect(canApply(null)).toBe(false)
  })
  it('says what a report means in a sentence', () => {
    expect(reportLine(report())).toBe('A deploy would create the tables of territories in territories (postgres): 2 statement(s).')
    expect(reportLine(report({ action: 'expand', statements: ['add column note'], blocked: ['drop column x'] }))).toBe('A deploy would expand the tables of territories in territories (postgres): 1 statement(s), 1 blocked.')
    expect(reportLine(report({ dryRun: false, applied: true }))).toBe('Deployed: created the tables of territories in territories (postgres).')
    expect(reportLine(report({ action: 'none', statements: [] }))).toContain('nothing to do')
    expect(reportLine(report({ dryRun: false, applied: false, action: 'expand' }))).toContain('Not deployed')
  })
  it('reads the report a run recorded, and none from a run that failed before it planned', () => {
    expect(deployReportOf(run(report()))?.statements).toHaveLength(2)
    expect(deployReportOf(run(null, { outcome: 'failed' }))).toBeNull()
  })
  it('draws the run with its statements, what is blocked and a link to the run on the Runner\'s screens', () => {
    const html = renderToStaticMarkup(<MemoryRouter><DeployResult result={{ build: 'b', started, run: run(report({ blocked: ['drop column x'], notes: ['a note'] })) }} /></MemoryRouter>)
    expect(html).toContain('data-dry="true"')
    expect(html.match(/data-testid="wsp-deploy-statement"/g)).toHaveLength(2)
    expect(html).toContain('Blocked: drop column x')
    expect(html).toContain('a note')
    expect(html).toContain('href="/servers/runner-1/runs?run=11111111-2222-3333-4444-555555555555"')
  })
  it('says the failure of a run that failed', () => {
    const html = renderToStaticMarkup(<MemoryRouter><DeployResult result={{ build: 'b', started, run: run(null, { outcome: 'failed', failure: { kind: 'Permanent', message: 'The login was refused.' } }) }} /></MemoryRouter>)
    expect(html).toContain('data-outcome="failed"')
    expect(html).toContain('The login was refused.')
  })
  it('waits for a build to end', async () => {
    const states = ['queued', 'running', 'succeeded']
    const b = await followBuild(async () => ({ id: 'b', status: states.shift() ?? 'succeeded' }) as BuildSummary, 0)
    expect(b.status).toBe('succeeded')
  })
  it('says a draft with no model has nothing to deploy, and offers the deploy for one with a model', () => {
    const panel = (paths: string[]) => renderToStaticMarkup(<MemoryRouter><QueryClientProvider client={new QueryClient()}><StoreDeployPanel project="p" id="d" paths={paths} /></QueryClientProvider></MemoryRouter>)
    expect(panel(['README.md'])).toContain('data-testid="wsp-deploy-no-model"')
    const html = panel(['models/territories.yaml'])
    expect(html).toContain('data-testid="wsp-deploy-dry"')
    // the deploy itself waits for a dry run
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*data-testid="wsp-deploy-apply"/)
  })
})

describe('the contract step of a Store model on a Runner (plan 33 SC24)', () => {
  const plan = (over: Partial<ContractReport> = {}): ContractReport => ({
    model: 'territories', schema: 'territories', engine: 'postgres', hash: 'abcdef0123456789'.repeat(4), dryRun: true, applied: false,
    steps: [{ object: 'territory.note', description: 'drop column territory.note', text: 'ALTER TABLE "territories"."territory" DROP COLUMN "note"', requiresEveryRowToHaveAValue: false },
      { object: 'territory.region', description: 'make territory.region required', text: 'ALTER TABLE ... SET NOT NULL', requiresEveryRowToHaveAValue: true }],
    blocked: [], notes: [], dropped: [{ description: "drop the column 'note' of 'territories.territory'", columnId: 'postgres://db/x#note', datasetId: 'postgres://db/x' }], ...over,
  })
  const contractRun = (r: ContractReport | null, over: Partial<StagedRun> = {}) => ({ build: 'b', started: { ...started, task: 'contract' }, run: run(null, { task: 'contract', steps: r ? [{ key: r.schema, started: '', completed: '', result: r }] : [], ...over }) })

  it('approves only a plan that has steps and nothing blocked', () => {
    expect(canContract(plan())).toBe(true)
    expect(canContract(plan({ blocked: ['history keys'] }))).toBe(false)
    expect(canContract(plan({ steps: [], dropped: [] }))).toBe(false)
    expect(canContract(plan({ dryRun: false, applied: true }))).toBe(false)
    expect(canContract(null)).toBe(false)
  })
  it('reads the allowances as typed, comma or space separated', () => {
    expect(allowancesOf(' sales@prod, gateway:edge  report@test ')).toEqual(['sales@prod', 'gateway:edge', 'report@test'])
    expect(allowancesOf('')).toEqual([])
  })
  it('draws the plan with each statement, the rows that must have a value, the hash and the columns the Gateways are asked about', () => {
    expect(contractReportOf(contractRun(plan()).run)?.hash).toHaveLength(64)
    const html = renderToStaticMarkup(<MemoryRouter><ContractResult result={contractRun(plan())} /></MemoryRouter>)
    expect(html).toContain('would run 2 step(s). Plan abcdef012345.')
    expect(html.match(/data-testid="wsp-contract-step"/g)).toHaveLength(2)
    expect(html).toContain('(every row must have a value first)')
    expect(html).toContain('the paired Gateways are asked who reads: drop the column &#x27;note&#x27; of &#x27;territories.territory&#x27;')
    expect(html).toContain('of contract of model-territories')
  })
  it('says what an approved contract did, and what blocks a plan', () => {
    expect(renderToStaticMarkup(<MemoryRouter><ContractResult result={contractRun(plan({ dryRun: false, applied: true }))} /></MemoryRouter>)).toContain('Contracted: 2 step(s) ran on territories.')
    expect(renderToStaticMarkup(<MemoryRouter><ContractResult result={contractRun(plan({ blocked: ['the history keys change'] }))} /></MemoryRouter>)).toContain('Blocked: the history keys change')
    expect(renderToStaticMarkup(<MemoryRouter><ContractResult result={contractRun(plan({ steps: [], dropped: [] }))} /></MemoryRouter>)).toContain('have nothing to contract')
  })
  it('offers to plan the contract beside the deploy, and the approval only after a plan', () => {
    const html = renderToStaticMarkup(<MemoryRouter><QueryClientProvider client={new QueryClient()}><StoreDeployPanel project="p" id="d" paths={['models/territories.yaml']} /></QueryClientProvider></MemoryRouter>)
    expect(html).toContain('data-testid="wsp-contract-plan"')
    expect(html).not.toContain('data-testid="wsp-contract-apply"')
  })
})
