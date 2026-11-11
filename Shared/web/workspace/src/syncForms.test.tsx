import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { SchemaForm, parseYaml } from '@dbdatakit/shell'
import { SYNC_CONNECTION_SCHEMA, SYNC_MAPPING_SCHEMA, SYNC_TASK_SCHEMA, newReplicationFiles, secretFindings, syncSchemaSet } from './syncSchemas'

// plan 33 SC22i: the parts of a replication that were YAML only: the connection's run login, the scripts and hooks of the pipeline, the segmenting strategies and a mapping's default segmenting
const schemas = syncSchemaSet
const problems = (schema: string, text: string) => schemas.problems(schema, parseYaml(text).value)
const draw = (schema: string, text: string) => renderToStaticMarkup(<SchemaForm schemas={schemas} schema={schema} text={text} onText={() => {}} />)

const [task, mapping] = newReplicationFiles('orders', 'src', 'dst')

describe('the connection\'s run login', () => {
  const text = 'schema: connection/1\nname: src\nengine: sqlserver\nextensions:\n  sync:\n    run_user: runner\n    run_password_secret: secret:dbdatakit:sync:connection:src-run\n'
  it('is a field of the connection form and the schema accepts it', () => {
    expect(problems(SYNC_CONNECTION_SCHEMA, text)).toEqual([])
    expect(draw(SYNC_CONNECTION_SCHEMA, text)).toContain('runner')
  })
  it('is held to the secrets rule: a password value or a secret that is not a reference is a finding with its line', () => {
    expect(secretFindings(text)).toEqual([])
    const bad = secretFindings('extensions:\n  sync:\n    run_user: r\n    run_password_secret: hunter2\n')
    expect(bad).toHaveLength(1)
    expect(bad[0].line).toBe(4)
    expect(secretFindings('extensions:\n  sync:\n    run_password: hunter2\n')).toHaveLength(1)
  })
})

describe('the segmenting strategies of a replication', () => {
  const withStrategies = task.text.replace('segmentingStrategies: []', 'segmentingStrategies:\n- name: monthly\n  kind: DuckDb\n  sql: SELECT 1\n- name: by-script\n  kind: Script\n  scriptName: seg\n  parameters: {a: b}')
  it('are a list in the task form', () => {
    expect(problems(SYNC_TASK_SCHEMA, withStrategies)).toEqual([])
    const html = draw(SYNC_TASK_SCHEMA, withStrategies)
    expect(html).toContain('monthly')
    expect(html).toContain('by-script')
  })
  it('refuse a kind Sync does not know', () => {
    expect(problems(SYNC_TASK_SCHEMA, task.text.replace('segmentingStrategies: []', 'segmentingStrategies:\n- name: x\n  kind: Magic')).length).toBeGreaterThan(0)
  })
})

describe('the default segmenting of a mapping', () => {
  const withSegments = mapping.text.replace('defaultSegmenting: []', 'defaultSegmenting:\n- mode: range\n  column: created\n  rangeMin: "2024-01-01"\n  rangeMax: "2025-01-01"\n- mode: custom\n  strategyName: monthly')
  it('is a list of segments with the fields of each mode', () => {
    expect(problems(SYNC_MAPPING_SCHEMA, withSegments)).toEqual([])
    const html = draw(SYNC_MAPPING_SCHEMA, withSegments)
    expect(html).toContain('rangeMin')
    expect(html).toContain('monthly')
  })
  it('refuses a mode Sync does not read', () => {
    expect(problems(SYNC_MAPPING_SCHEMA, mapping.text.replace('defaultSegmenting: []', 'defaultSegmenting:\n- mode: weekly')).length).toBeGreaterThan(0)
  })
})

describe('the pipeline: scripts and hooks of a mapping and of a replication', () => {
  const pipeline = (text: string) => text.replace('scripts: {}', 'scripts:\n  rowTransform:\n    scriptName: clean\n    parameters: {mode: strict}').replace('hooks: {}', 'hooks:\n  afterLoad:\n  - name: stats\n    sql: ANALYZE t\n    connection: Target')
  it('are forms by slot and by point', () => {
    for (const [schema, file] of [[SYNC_TASK_SCHEMA, task.text], [SYNC_MAPPING_SCHEMA, mapping.text]] as const) {
      const text = pipeline(file)
      expect(problems(schema, text)).toEqual([])
      const html = draw(schema, text)
      expect(html).toContain('clean')
      expect(html).toContain('ANALYZE t')
    }
  })
  it('refuse a hook that runs on neither side', () => {
    expect(problems(SYNC_MAPPING_SCHEMA, mapping.text.replace('hooks: {}', 'hooks:\n  afterLoad:\n  - sql: x\n    connection: Elsewhere')).length).toBeGreaterThan(0)
  })
  it('keep the files of a new replication sound', () => {
    expect(problems(SYNC_TASK_SCHEMA, task.text)).toEqual([])
    expect(problems(SYNC_MAPPING_SCHEMA, mapping.text)).toEqual([])
  })
})

// plan 33 SC22k: the replication's `checks:` section is the shared check/1 list, drawn from its own schema
describe('the quality checks of a replication', () => {
  const withChecks = (checks: string) => task.text.replace('segmentingStrategies: []', `segmentingStrategies: []\nchecks:\n${checks}`)
  it('are a list of check/1 entries in the task form', () => {
    const text = withChecks('- id: orders_not_null\n  kind: not_null\n  dataset: orders\n  columns: [id]\n- id: orders_fresh\n  kind: freshness\n  dataset: orders\n  max_age: 2h')
    expect(problems(SYNC_TASK_SCHEMA, text)).toEqual([])
    const html = draw(SYNC_TASK_SCHEMA, text)
    expect(html).toContain('orders_not_null')
    expect(html).toContain('orders_fresh')
  })
  it('are refused by the schema when a kind is unknown or a required part is missing', () => {
    expect(problems(SYNC_TASK_SCHEMA, withChecks('- id: x\n  kind: sparkle\n  dataset: orders')).length).toBeGreaterThan(0)
    expect(problems(SYNC_TASK_SCHEMA, withChecks('- kind: not_null\n  dataset: orders\n  columns: [id]')).length).toBeGreaterThan(0)
  })
  it('leave a task without checks sound', () => {
    expect(problems(SYNC_TASK_SCHEMA, task.text)).toEqual([])
  })
})
