import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { TrackingCard, modeOf, noStateTableReason, retentionText, withMode, withRetention } from './TrackingCard'
import type { ReplicationTaskConfig } from '../types'

const task = (tracking: ReplicationTaskConfig['tracking'], target: string | null = null): ReplicationTaskConfig => ({
  name: 'orders', enabled: true, endpoints: { source: null, target: target ? { connectionName: target, database: null } : null },
  scheduling: { mode: 'Continuous', frequencySeconds: 60, cronExpression: null },
  changeProcessing: { reader: { kind: 'BatchReload', options: {} }, cache: { kind: 'StagingTable', options: {} }, writer: { kind: 'DeleteInsert', options: {} }, degreeOfParallelism: 4, bulkLoadDegreeOfParallelism: 4 },
  tracking,
})

describe('the tracking settings', () => {
  it('reads a file without the section as the default, and the default removes the section', () => {
    expect(modeOf(task(undefined))).toBe('default')
    expect(withMode(task({ mode: 'target', retention: { entries: 50 } }), 'default').tracking).toBeNull()
  })

  it('takes a primary only for both, starting from the target, and keeps the retention where a table is kept on the target', () => {
    expect(withMode(task(null), 'both').tracking).toEqual({ mode: 'both', primary: 'target', retention: null })
    expect(withMode(task({ mode: 'both', primary: 'state', retention: { days: 3 } }), 'target').tracking).toEqual({ mode: 'target', primary: null, retention: { days: 3 } })
    expect(withMode(task({ mode: 'target', retention: { days: 3 } }), 'state').tracking).toEqual({ mode: 'state', primary: null, retention: null })
  })

  it('writes a retention number as typed, drops an emptied one, and drops the retention when neither is left', () => {
    const t = task({ mode: 'target', retention: null })
    expect(withRetention(t, 'entries', '200').tracking?.retention).toEqual({ entries: 200 })
    expect(withRetention(task({ mode: 'target', retention: { entries: 200 } }), 'entries', '').tracking?.retention).toBeNull()
    expect(withRetention(task(null), 'days', '3').tracking).toBeNull()
  })

  it('says what the retention keeps', () => {
    expect(retentionText(1000, 7)).toBe('The target\'s table keeps the newest 1000 entries of each mapping and everything newer than 7 days; the newest entry of each source is never removed.')
    expect(retentionText(1, 1)).toContain('newest 1 entry of each mapping and everything newer than 1 day;')
  })

  it('shows the retention, with the defaults it falls back to, only for a mode that keeps a table on the target', () => {
    const target = renderToStaticMarkup(<TrackingCard draft={task({ mode: 'target', retention: { entries: 200 } })} onChange={() => {}} />)
    expect(target).toContain('data-testid="tracking-retention"')
    expect(target).toContain('keeps the newest 200 entries of each mapping and everything newer than 7 days')
    expect(target).not.toContain('tracking-primary-select')
    const both = renderToStaticMarkup(<TrackingCard draft={task({ mode: 'both', primary: 'state' })} onChange={() => {}} />)
    expect(both).toContain('tracking-primary-select')
    const state = renderToStaticMarkup(<TrackingCard draft={task({ mode: 'state' })} onChange={() => {}} />)
    expect(state).not.toContain('tracking-retention')
    expect(renderToStaticMarkup(<TrackingCard draft={task(undefined)} onChange={() => {}} />)).toContain('tracking-default-note')
  })

  it('disables target and both, with the reason, when the target\'s driver keeps no state table', () => {
    const mysql = { driverType: 'MySql', keepsStateTable: false }
    expect(noStateTableReason(mysql, 'mart')).toBe('The target connection mart (driver MySql) keeps no state table, so this replication keeps its state on the Runner: choose State only or Default.')
    expect(noStateTableReason({ driverType: 'Postgres', keepsStateTable: true }, 'dw')).toBeNull()
    expect(noStateTableReason(undefined, 'dw')).toBeNull()

    const html = renderToStaticMarkup(<TrackingCard draft={task(null, 'mart')} onChange={() => {}} target={mysql} />)
    expect(html).toMatch(/<option value="target" disabled=""/)
    expect(html).toMatch(/<option value="both" disabled=""/)
    expect(html).not.toMatch(/<option value="state" disabled/)
    expect(html).toContain('data-testid="tracking-no-state-table"')
    expect(html).toContain('(driver MySql) keeps no state table')

    // a file that already asks for the target keeps it shown, and the reason reads as a warning
    const asked = renderToStaticMarkup(<TrackingCard draft={task({ mode: 'target' }, 'mart')} onChange={() => {}} target={mysql} />)
    expect(asked).toMatch(/class="hint warn" data-testid="tracking-no-state-table"/)
    expect(asked).not.toContain('tracking-target-note')

    const postgres = renderToStaticMarkup(<TrackingCard draft={task(null, 'dw')} onChange={() => {}} target={{ driverType: 'Postgres', keepsStateTable: true }} />)
    expect(postgres).not.toMatch(/<option value="(target|both)" disabled/)
    expect(postgres).not.toContain('tracking-no-state-table')
  })

  it('is read only for a person who may not change the draft', () => {
    expect(renderToStaticMarkup(<TrackingCard draft={task(null)} onChange={() => {}} editable={false} />)).toMatch(/<fieldset[^>]*disabled/)
  })
})
