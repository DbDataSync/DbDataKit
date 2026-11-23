import { describe, expect, it } from 'vitest'
import { parseYaml } from '@dbdatakit/shell'
import { editTask, editText, mappingsOf, newMappingText, newTaskText, readConnection, readMapping, readScript, readTask, scriptPathsOf } from './model'
import { columnDifference } from './mappings/CachedMetadataCard'

const TASK = `# the nightly orders copy
schemaVersion: 2
name: orders
scheduling:
  mode: Continuous   # every minute for now
  frequencySeconds: 60
changeProcessing:
  reader: {kind: MsSqlChangeTracking, options: {}}
  cache: {kind: MsSqlStagingTable, options: {}}
  writer: {kind: PostgresMerge, options: {}}
endpoints:
  source:
    connectionName: erp
owner: data-team   # a key no screen knows
`

describe('the draft model of a task.yaml', () => {
  it('keeps the comments and a key no screen knows after a change of the schedule and of an endpoint', () => {
    const before = readTask('config/replications/orders/task.yaml', 'orders', TASK).value!
    const after = {
      ...before,
      scheduling: { ...before.scheduling, mode: 'Periodic' as const, frequencySeconds: null, cronExpression: '*/15 * * * *' },
      endpoints: { ...before.endpoints, source: { ...before.endpoints.source!, database: 'sales' }, target: { connectionName: 'dw', database: null } },
    }
    const text = editTask(TASK, before, after)
    expect(text).toContain('# the nightly orders copy')
    expect(text).toMatch(/owner: data-team +# a key no screen knows/)
    expect(text).toMatch(/mode: Periodic +# every minute for now/)
    const doc = parseYaml(text).value as Record<string, any>
    expect(doc.scheduling).toEqual({ mode: 'Periodic', cronExpression: '*/15 * * * *' })
    expect(doc.endpoints).toEqual({ source: { connectionName: 'erp', database: 'sales' }, target: { connectionName: 'dw' } })
    expect(doc.changeProcessing.reader).toEqual({ kind: 'MsSqlChangeTracking', options: {} })
    // nothing else changed: the parsed file is the old one with the two changes
    const old = parseYaml(TASK).value as Record<string, any>
    expect({ ...doc, scheduling: old.scheduling, endpoints: old.endpoints }).toEqual(old)
  })

  it('fills in what Sync assumes when the file says nothing, and writes none of it back unless it changed', () => {
    const view = readTask('t.yaml', 'orders', TASK).value!
    expect(view.enabled).toBe(true)
    expect(view.changeProcessing.degreeOfParallelism).toBe(4)
    expect(view.endpoints.target).toBeNull()
    expect(editTask(TASK, view, { ...view })).toBe(TASK)
    expect(editTask(TASK, view, { ...view, enabled: false })).toContain('enabled: false')
  })

  it('writes a task of format 1 in the current format once a screen changes it', () => {
    const old = TASK.replace('schemaVersion: 2', 'schemaVersion: 1')
    const view = readTask('t.yaml', 'orders', old).value!
    expect(editTask(old, view, view)).toBe(old)
    expect(editTask(old, view, { ...view, enabled: false })).toContain('schemaVersion: 2')
  })

  it('reports a task of a newer format, and one that is not YAML, for Files', () => {
    expect(readTask('config/replications/orders/task.yaml', 'orders', TASK.replace('schemaVersion: 2', 'schemaVersion: 3')).problem)
      .toMatch(/task\.yaml is in format 3, which is newer than the Designer reads \(2\); open it in Files/)
    expect(readTask('t.yaml', 'orders', 'name: [').problem).toMatch(/not valid YAML/)
  })

  it('makes a new replication disabled, with no endpoints, in the current format', () => {
    const view = readTask('t.yaml', 'nightly', newTaskText('nightly', { mode: 'Periodic', cronExpression: '0 * * * *' })).value!
    expect(view).toMatchObject({ schemaVersion: 2, name: 'nightly', enabled: false, scheduling: { mode: 'Periodic', cronExpression: '0 * * * *' }, endpoints: { source: null, target: null } })
  })
})

describe('the draft model of a table mapping', () => {
  const MAPPING = 'schemaVersion: 1\nname: dbo.orders\nsources:\n- schema: dbo\n  table: orders\ntargets:\n- schema: dbo\n  table: orders\ncolumnMappings: []\n'

  it('reports a mapping of a newer format for Files, naming the file', () => {
    const r = readMapping('config/replications/orders/table-mappings/dbo.orders.yaml', MAPPING.replace('schemaVersion: 1', 'schemaVersion: 2'))
    expect(r.value).toBeNull()
    expect(r.problem).toBe('config/replications/orders/table-mappings/dbo.orders.yaml is in format 2, which is newer than the Designer reads (1); open it in Files, or update the software.')
  })

  it('reads a side that inherits the replication\'s endpoint as null, and writes a list whole when its length changes', () => {
    const view = readMapping('m.yaml', MAPPING).value!
    expect(view.sources[0]).toMatchObject({ connectionName: null, database: null, schema: 'dbo', table: 'orders', filter: null, query: null })
    const text = editText(MAPPING, view, { ...view, columnMappings: [{ sourceColumn: 'id', targetColumn: 'id' }], primaryKey: ['id'] })
    expect(parseYaml(text).value).toMatchObject({ columnMappings: [{ sourceColumn: 'id', targetColumn: 'id' }], primaryKey: ['id'] })
    const back = editText(text, readMapping('m.yaml', text).value!, { ...readMapping('m.yaml', text).value!, primaryKey: [] })
    expect(parseYaml(back).value).toMatchObject({ primaryKey: [] })
  })

  it('lists a replication\'s mappings from the draft\'s paths', () => {
    expect(mappingsOf(['config/replications/orders/task.yaml', 'config/replications/orders/table-mappings/b.yaml', 'config/replications/orders/table-mappings/a.yml', 'config/replications/other/table-mappings/c.yaml'], 'orders')).toEqual(['a', 'b'])
  })

  it('reads a connection for the pickers, and never more than its name, engine, database and scripts', () => {
    const c = readConnection('config/connections/erp.yaml', 'schema: connection/1\nname: erp\nengine: sqlserver\naddress:\n  host: h\n  database: sales\nauth:\n  password_secret: secret:dbdatakit:sync:connection:erp\n').value
    expect(c).toEqual({ name: 'erp', path: 'config/connections/erp.yaml', engine: 'sqlserver', database: 'sales', scripts: {} })
  })
})

describe('a new mapping', () => {
  it('writes the target as a mapping of its own, never an alias of the source, so one side can be edited alone', () => {
    const side = { schema: 'dbo', table: 'customers' }
    const path = 'config/replications/orders/table-mappings/dbo.customers.yaml'
    const text = newMappingText('dbo.customers', side, side)
    expect(text).not.toMatch(/[&*]a\d/)
    const before = readMapping(path, text).value!
    const after = { ...before, targets: [{ ...before.targets[0], connectionName: 'dw', schema: 'staging' }] }
    const edited = readMapping(path, editText(text, before, after)).value!
    expect(edited.targets[0]).toMatchObject({ connectionName: 'dw', schema: 'staging', table: 'customers' })
    expect(edited.sources[0]).toMatchObject({ connectionName: null, schema: 'dbo', table: 'customers' })
  })
})

describe('the cached metadata card', () => {
  it('names the columns that differ between the mapping and the catalog cache', () => {
    const col = (name: string, nativeType = 'int') => ({ name, nativeType, isNullable: true, isPrimaryKey: false, isIdentity: false })
    expect(columnDifference([col('id'), col('total')], [col('id'), col('total', 'bigint'), col('placed_at')])).toBe('the catalog cache has 1 column added (placed_at), 1 changed type (total)')
    expect(columnDifference([col('id')], [col('id')])).toBe('the same as the catalog cache')
  })
})

describe('the set\'s scripts, read only', () => {
  it('are the manifests of config/scripts, each with its slot, parameters and whether it is enabled', () => {
    expect(scriptPathsOf(['config/scripts/b.yaml', 'config/scripts/b.csx', 'config/scripts/a.yml', 'config/scripts/x/y.yaml', 'config/connections/erp.yaml']))
      .toEqual(['config/scripts/a.yml', 'config/scripts/b.yaml'])
    const read = readScript('config/scripts/mask.yaml', 'schemaVersion: 1\nkind: rowTransform\nenabled: false\nparameters:\n- name: column\n  type: ColumnPicker\n- type: Text\n')
    expect(read.value).toEqual({ name: 'mask', path: 'config/scripts/mask.yaml', kind: 'rowTransform', language: 'CSharp', description: null, parameters: [{ name: 'column', type: 'ColumnPicker' }], enabled: false })
    expect(readScript('config/scripts/bad.yaml', 'kind: [').problem).toContain('config/scripts/bad.yaml is not valid YAML')
  })
})
