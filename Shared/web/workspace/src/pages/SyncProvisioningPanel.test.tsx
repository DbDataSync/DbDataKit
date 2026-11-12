import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ProvisioningPlan } from '../api'
import { ProvisioningPlanView, SyncProvisioningPanel, stateWord } from './SyncProvisioningPanel'

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
})
