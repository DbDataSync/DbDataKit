import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { BuildSummary, StagedRun, StagedRunStarted } from '../api'
import { DeployResult, StoreDeployPanel, canApply, deployReportOf, followBuild, modelsOf, packageOf, reportLine, storageConnectionOf, type DeployReport } from './StoreDeployPanel'

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
