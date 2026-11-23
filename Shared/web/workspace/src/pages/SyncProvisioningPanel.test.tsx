import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ApiError } from '@dbdatakit/shell'
import type { ProvisioningPlan, StagedRun } from '../api'
import { ProvisioningPlanView, SyncProvisioningPanel, followStagedRun, reportedSteps, stateWord } from './SyncProvisioningPanel'

const plan: ProvisioningPlan = {
  replication: 'orders', cache_generated_at: '2026-11-12T10:00:00Z', cache_server: 'prod-gateway',
  mappings: [
    {
      mapping: 'orders', source: { connection: 'source-db', table: 'dbo.Orders' }, target: { connection: 'target-db', table: 'public.orders' },
      source_plan: { action: 'enableSourceChangeCapture', state: 'unknown', steps: [], warnings: ['needs the source\'s own settings'] },
      target_plan: { action: 'createTargetTable', state: 'missing', steps: [{ title: 'Create the table', command_text: 'CREATE TABLE "public"."orders" ("id" bigint)', rationale: 'it is not there', scope: 'table' }], warnings: [] },
      excluded: null,
    },
    { mapping: 'many', source: null, target: null, source_plan: null, target_plan: null, excluded: 'The mapping has 2 source(s) and 1 target(s): provisioning only plans 1:1 mappings.' },
  ],
}

const panel = (paths: string[]) => renderToStaticMarkup(
  <QueryClientProvider client={new QueryClient()}><SyncProvisioningPanel project="p" id="d" paths={paths} /></QueryClientProvider>,
)

describe('the provisioning plan of a Sync draft', () => {
  it('says each state in words, and an unknown is never "nothing to do"', () => {
    expect(stateWord('satisfied')).toBe('nothing to do')
    expect(stateWord('missing')).toBe('would change')
    expect(stateWord('unsupported')).toBe('cannot be planned')
    expect(stateWord('unknown')).toBe('not known from the cache')
  })
  it('draws each mapping with both sides, the statements as the server would run them, the warnings and where the cache came from', () => {
    const html = renderToStaticMarkup(<ProvisioningPlanView answer={{ ok: true, diagnostics: [], data: plan }} />)
    expect(html).toContain('From the catalog cache made 2026-11-12T10:00:00Z by prod-gateway: no connection was opened.')
    expect(html.match(/data-testid="wsp-prov-mapping"/g)).toHaveLength(2)
    expect(html.match(/data-testid="wsp-prov-side"/g)).toHaveLength(2)
    expect(html).toContain('data-state="unknown"')
    expect(html).toContain('data-state="missing"')
    expect(html).toContain('CREATE TABLE &quot;public&quot;.&quot;orders&quot; (&quot;id&quot; bigint)')
    expect(html).toContain('needs the source&#x27;s own settings')
    expect(html).toContain('Not planned: The mapping has 2 source(s)')
  })
  it('shows the programs findings when it did not answer, and no plan', () => {
    const html = renderToStaticMarkup(<ProvisioningPlanView answer={{ ok: false, diagnostics: [{ severity: 'error', code: 'DBD-WS-SYNC-FAILED', message: 'catalog-cache.yaml is not in .', file: null, line: null }], data: null }} />)
    expect(html).toContain('DBD-WS-SYNC-FAILED: catalog-cache.yaml is not in .')
    expect(html).not.toContain('wsp-prov-mapping')
  })
  it('says there is no mapping, and never offers to apply', () => {
    const html = renderToStaticMarkup(<ProvisioningPlanView answer={{ ok: true, diagnostics: [], data: { ...plan, mappings: [] } }} />)
    expect(html).toContain('wsp-prov-none')
    expect(html.toLowerCase()).not.toContain('apply</button')
  })
  it('lists the replications of the draft to plan, or says there is none', () => {
    const html = panel(['config/replications/orders/task.yaml', 'config/replications/stock/task.yaml'])
    expect(html).toContain('data-testid="wsp-prov-run"')
    expect(html.match(/<option/g)).toHaveLength(2)
    expect(panel(['README.md'])).toContain('wsp-prov-no-replication')
  })

  it('offers Apply on the Runner only for a side that would change or is not known, never for one with nothing to do', () => {
    const html = renderToStaticMarkup(<ProvisioningPlanView answer={{ ok: true, diagnostics: [], data: plan }} onApply={() => {}} />)
    expect(html.match(/data-testid="wsp-prov-apply"/g)).toHaveLength(2)
    const satisfied = { ...plan, mappings: [{ ...plan.mappings[0]!, source_plan: { ...plan.mappings[0]!.source_plan!, state: 'satisfied' as const }, target_plan: null }] }
    expect(renderToStaticMarkup(<ProvisioningPlanView answer={{ ok: true, diagnostics: [], data: satisfied }} onApply={() => {}} />)).not.toContain('wsp-prov-apply')
  })
})

describe('applying a draft on the Runner (plan 28c E21, step e)', () => {
  it('follows the run to its end and reads the statements it reports', async () => {
    const running: StagedRun = { id: 'r', task: 'provision', outcome: 'running', started: 's', finished: null, version: '2', failure: null }
    const done: StagedRun = { ...running, outcome: 'failed', finished: 'f', failure: { kind: 'error', message: 'a step failed' }, steps: [{ key: 'orders', started: 's', completed: 'f', result: { steps: [{ title: 'Create the table', succeeded: false, error: 'denied' }] } }] }
    const reads = [running, running, done]
    const run = await followStagedRun(async () => reads.shift()!, 1, 10)
    expect(run.outcome).toBe('failed')
    expect(reportedSteps(run)).toEqual([{ title: 'Create the table', succeeded: false, error: 'denied' }])
    let n = 0
    expect((await followStagedRun(async () => { n++; return running }, 1, 3)).finished).toBeNull()
    expect(n).toBe(3)
  })
  it('reads a run the Runner has queued but not started again until it is on the Runner\'s record, and gives up on one that never is', async () => {
    const queued = () => new ApiError(404, 'Runner one has no run r.')
    const done: StagedRun = { id: 'r', task: 'provision', outcome: 'succeeded', started: 's', finished: 'f', version: '2', failure: null }
    const reads: (StagedRun | ApiError)[] = [queued(), queued(), done]
    const run = await followStagedRun(async () => { const next = reads.shift()!; if (next instanceof ApiError) throw next; return next }, 1, 10)
    expect(run.outcome).toBe('succeeded')
    await expect(followStagedRun(async () => { throw queued() }, 1, 10, 3)).rejects.toThrow('has no run')
    await expect(followStagedRun(async () => { throw new ApiError(403, 'refused') }, 1, 10, 3)).rejects.toThrow('refused')
  })
})
