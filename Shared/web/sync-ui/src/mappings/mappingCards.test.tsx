import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { segmentingModeOf, segmentsForMode } from './DefaultSegmentingCard'
import { checkAsWritten, blankCheck, summarise } from './ChecksCard'
import { NaturalKeyView, inheritedStages, kindsFor, toggledOverride, type PipelineOverrides } from './MappingPipelineCard'
import { ProvisioningCard, pendingSteps } from './ProvisioningCard'
import { queryLine } from './QuerySourcePanel'
import type { DriverCapabilities } from '../api/types'
import type { ReplicationTaskConfig } from '../types'

const task: ReplicationTaskConfig = {
  name: 'orders', enabled: true, endpoints: { source: null, target: null },
  scheduling: { mode: 'Continuous', frequencySeconds: 60, cronExpression: null },
  changeProcessing: { reader: { kind: 'MsSqlChangeTracking', options: {} }, cache: { kind: 'MsSqlStagingTable', options: {} }, writer: { kind: 'ChangeApply', options: { batch: '10' } }, degreeOfParallelism: 4, bulkLoadDegreeOfParallelism: 4 },
}
const none: PipelineOverrides = { readerOverride: null, cacheOverride: null, writerOverride: null, bulkLoadReaderOverride: null, bulkLoadCacheOverride: null, bulkLoadWriterOverride: null }

describe('a mapping\'s pipeline', () => {
  it('inherits Change Processing\'s stages, and Bulk Load\'s reader as BatchReload with staging and writer from Change Processing when the replication says nothing', () => {
    expect(inheritedStages(task, 'changeProcessing').writer).toEqual({ kind: 'ChangeApply', options: { batch: '10' } })
    expect(inheritedStages(task, 'bulkLoad')).toEqual({ reader: { kind: 'BatchReload', options: {} }, cache: task.changeProcessing.cache, writer: task.changeProcessing.writer })
    expect(inheritedStages({ ...task, bulkLoad: { reader: { kind: 'BatchReload' }, writer: { kind: 'DeleteInsert', options: {} } } }, 'bulkLoad').writer).toEqual({ kind: 'DeleteInsert', options: {} })
  })

  it('starts an override from what is in effect, and turning it off goes back to the replication\'s', () => {
    const on = toggledOverride(none, 'writerOverride', task.changeProcessing.writer)
    expect(on.writerOverride).toEqual({ kind: 'ChangeApply', options: { batch: '10' } })
    expect(on.writerOverride).not.toBe(task.changeProcessing.writer)
    expect(toggledOverride(on, 'writerOverride', on.writerOverride!).writerOverride).toBeNull()
  })

  it('offers the source driver\'s readers and the target driver\'s writers, never a reconcile-only kind', () => {
    const caps = (readers: string[], writers: string[]): DriverCapabilities => ({
      driverType: 'x', supportsConnectionTest: true, supportedProvisioningActions: [], keepsStateTable: false, stagingProviders: [{ kind: 'StagingTable', parameters: [] }],
      readers: readers.map((kind) => ({ kind, parameters: [], supportsSegmentation: kind === 'BatchReload', detectsDeletes: false, supportedIntents: [] })),
      writers: writers.map((kind) => ({ kind, parameters: [], supportsReconciliation: kind === 'DeleteInsert' })),
    })
    const source = caps(['BatchReload', 'KeyReconcile'], [])
    const target = caps([], ['DeleteInsert', 'ChangeApply', 'KeyReconcileDelete'])
    expect(kindsFor('reader', source, target)).toEqual([{ kind: 'BatchReload', note: 'segmentable · does not detect deletes' }])
    expect(kindsFor('writer', source, target)).toEqual([{ kind: 'DeleteInsert', note: 'reconciling' }, { kind: 'ChangeApply', note: 'change writer, default' }])
    expect(kindsFor('cache', source, target)).toEqual([{ kind: 'StagingTable' }])
  })

  it('shows the natural key the writer derives, or why there is none, and the key stated by hand in its place', () => {
    const derived = renderToStaticMarkup(<NaturalKeyView derived={{ columns: ['customer_id'], problem: null }} problem={null} mappingName="dbo.customers" value="" stated={false} onChange={() => {}} />)
    expect(derived).toContain('AUTO-DERIVED')
    expect(derived).toContain('customer_id')
    const missing = renderToStaticMarkup(<NaturalKeyView derived={{ columns: [], problem: '\'dbo.lines\' has no primary key.' }} problem={null} mappingName="dbo.lines" value="" stated={false} onChange={() => {}} />)
    expect(missing).toContain('mapping-natural-key-problem')
    expect(missing).toContain('has no primary key')
    expect(renderToStaticMarkup(<NaturalKeyView derived={undefined} problem={null} mappingName="x" value="a, b" stated onChange={() => {}} />)).toContain('value="a, b"')
  })
})

describe('a mapping\'s default segmenting', () => {
  it('reads nothing as the whole table, one full, auto or custom entry as that mode, and anything else as a fixed list', () => {
    expect(segmentingModeOf([])).toBe('none')
    expect(segmentingModeOf([{ mode: 'custom', strategyName: 'by-month', column: 'placed_at' }])).toBe('custom')
    expect(segmentingModeOf([{ mode: 'range', column: 'a', rangeMin: '1', rangeMax: '2' }, { mode: 'list', column: 'b', values: ['x'] }])).toBe('static')
  })

  it('replaces the list with the one entry of a mode, a custom one naming the replication\'s first strategy', () => {
    expect(segmentsForMode('custom', [{ name: 'by-month', kind: 'DuckDb' }])).toEqual([{ mode: 'custom', strategyName: 'by-month', column: '' }])
    expect(segmentsForMode('none', [])).toEqual([])
    expect(segmentsForMode('auto', [])).toEqual([{ mode: 'auto', column: '', bucketCount: 4 }])
  })
})

describe('a mapping\'s verification checks', () => {
  it('keep only the keys of their kind', () => {
    const sql = { ...blankCheck(), name: ' rows ', kind: 'RowCount' as const, measures: ['total'], sourceSql: 'select 1', scriptName: 's', parameters: { a: '1' } }
    expect(checkAsWritten(sql)).toMatchObject({ name: 'rows', measures: [], sourceSql: null, scriptName: null, parameters: {} })
    expect(checkAsWritten({ ...blankCheck(), name: 'x', kind: 'Sql', sourceSql: 'select 1' }).sourceSql).toBe('select 1')
  })

  it('are told in a line', () => {
    expect(summarise({ ...blankCheck(), name: 'r', kind: 'RowCount', groupBy: ['region'] })).toBe('rows by region')
    expect(summarise({ ...blankCheck(), name: 's', kind: 'Sum', measures: ['total'] })).toBe('total in total')
  })
})

describe('a mapping\'s provisioning and query source', () => {
  const side = (state: 'missing' | 'satisfied', n: number) => ({ action: 'CreateTargetTable', state, warnings: [], steps: Array.from({ length: n }, (_, i) => ({ title: `s${i}`, command_text: `X${i};`, rationale: null, scope: 'table' })) })

  it('counts the statements its plan would run, for the tab\'s badge', () => {
    expect(pendingSteps({ mapping: 'm', source: null, target: null, source_plan: side('satisfied', 0), target_plan: side('missing', 2), excluded: null })).toBe(2)
    expect(pendingSteps(undefined)).toBe(0)
  })

  it('shows the settings as the replication\'s until overridden, and each side\'s plan with its statements', () => {
    const html = renderToStaticMarkup(<ProvisioningCard provisioning={{ createTargetTableIfMissing: null, alterTargetTableColumnsIfMissingOrChanged: false }} inherited={{ createTargetTableIfMissing: true, alterTargetTableColumnsIfMissingOrChanged: null }}
      onChangeProvisioning={() => {}} plan={{ mapping: 'm', source: null, target: null, source_plan: side('satisfied', 0), target_plan: side('missing', 1), excluded: null }} />)
    expect(html).toContain('INHERITED')
    expect(html).toContain('data-testid="provisioning-plan-target" data-side="target" data-state="missing"')
    expect(html).toContain('X0;')
    expect(html).toContain('nothing to do')
  })

  it('says how long a query is, or that none is written', () => {
    expect(queryLine('select a\nfrom b')).toBe('2 line query')
    expect(queryLine('  ')).toBe('No query written yet')
  })
})
