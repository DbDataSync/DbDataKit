import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { ProvisioningPlan } from '@dbdatakit/workspace'
import { groupPlan, scriptFor } from './provisioningGroups'
import { ReplicationProvisioningPanel } from './ReplicationProvisioningPanel'

const create = (table: string) => ({ title: `Create ${table}`, command_text: `CREATE TABLE ${table} (id int);`, rationale: 'The target table does not exist.', scope: 'table' })
const schema = { title: 'Create schema dbo', command_text: 'CREATE SCHEMA dbo;', rationale: null, scope: 'database' }

const plan: ProvisioningPlan = {
  replication: 'orders', cache_generated_at: '2026-11-20T10:00:00Z', cache_server: 'runners-eu',
  mappings: [
    { mapping: 'dbo.orders', source: { connection: 'erp', table: 'dbo.orders' }, target: { connection: 'dw', table: 'dbo.orders' },
      source_plan: { action: 'EnableChangeTracking', state: 'unknown', steps: [], warnings: ['Whether change tracking is on is the live server\'s to say.'] },
      target_plan: { action: 'CreateTargetTable', state: 'missing', steps: [schema, create('dbo.orders')], warnings: [] }, excluded: null },
    { mapping: 'dbo.lines', source: { connection: 'erp', table: 'dbo.lines' }, target: { connection: 'dw', table: 'dbo.lines' },
      source_plan: null, target_plan: { action: 'CreateTargetTable', state: 'missing', steps: [schema, create('dbo.lines')], warnings: [] }, excluded: null },
    { mapping: 'dbo.done', source: { connection: 'erp', table: 'dbo.done' }, target: { connection: 'dw', table: 'dbo.done' },
      source_plan: null, target_plan: { action: 'CreateTargetTable', state: 'satisfied', steps: [], warnings: [] }, excluded: null },
    { mapping: 'dbo.query', source: null, target: null, source_plan: null, target_plan: null, excluded: 'The source is a query.' },
  ],
}

describe('a replication\'s plan in the old layout', () => {
  it('groups the steps by connection and side, each statement once with the mappings that need it', () => {
    const { groups } = groupPlan(plan)
    expect(groups).toHaveLength(1)
    const [dw] = groups
    expect(dw).toMatchObject({ connection: 'dw', side: 'target' })
    expect(dw!.steps.map((s) => [s.commandText, s.mappings])).toEqual([
      ['CREATE SCHEMA dbo;', ['dbo.orders', 'dbo.lines']],
      ['CREATE TABLE dbo.orders (id int);', ['dbo.orders']],
      ['CREATE TABLE dbo.lines (id int);', ['dbo.lines']],
    ])
    expect(dw!.apply).toEqual([{ mapping: 'dbo.orders', action: 'CreateTargetTable' }, { mapping: 'dbo.lines', action: 'CreateTargetTable' }])
  })

  it('lists apart a mapping that was not planned and a side the cache cannot say anything about, with why; a satisfied side is not listed', () => {
    expect(groupPlan(plan).excluded).toEqual([
      { mapping: 'dbo.orders', side: 'source', reason: 'Whether change tracking is on is the live server\'s to say.' },
      { mapping: 'dbo.query', side: null, reason: 'The source is a query.' },
    ])
  })

  it('copies a group as a script with a header naming the replication, the connection and the side', () => {
    const script = scriptFor('orders', groupPlan(plan).groups[0]!, '2026-11-23T00:00:00Z')
    expect(script).toBe('-- DbDataKit Sync provisioning script\n-- Replication: orders\n-- Connection: dw (target)\n-- Generated: 2026-11-23T00:00:00Z\n\nCREATE SCHEMA dbo;\nCREATE TABLE dbo.orders (id int);\nCREATE TABLE dbo.lines (id int);')
  })

  it('is drawn with the cache it was made from, the groups, Apply per mapping only when asked for, and the excluded', () => {
    const answer = { ok: true, diagnostics: [], data: plan }
    const html = renderToStaticMarkup(<ReplicationProvisioningPanel replication="orders" answer={answer} />)
    expect(html).toContain('Planned from the catalog cache made 2026-11-20T10:00:00Z by runners-eu; no connection was opened.')
    expect(html).toContain('data-testid="provisioning-group" data-connection="dw" data-side="target"')
    expect(html.match(/data-testid="provisioning-step"/g)).toHaveLength(3)
    expect(html).not.toContain('provisioning-apply')
    expect(html).toContain('data-testid="provisioning-excluded-row" data-mapping="dbo.query"')
    expect(renderToStaticMarkup(<ReplicationProvisioningPanel replication="orders" answer={answer} onApply={() => {}} />)).toContain('data-mapping="dbo.lines" data-action="CreateTargetTable"')
  })

  it('says there is nothing to provision, and shows the findings of a draft that did not plan', () => {
    const none = renderToStaticMarkup(<ReplicationProvisioningPanel replication="orders" answer={{ ok: true, diagnostics: [], data: { ...plan, mappings: [] } }} />)
    expect(none).toContain('Nothing to provision.')
    const failed = renderToStaticMarkup(<ReplicationProvisioningPanel replication="orders" answer={{ ok: false, diagnostics: [{ severity: 'error', code: 'DBS-001', message: 'no such replication', file: null, line: null }], data: null }} />)
    expect(failed).toContain('DBS-001: no such replication')
  })
})
