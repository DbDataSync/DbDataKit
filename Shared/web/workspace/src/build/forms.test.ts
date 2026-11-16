import { describe, expect, it } from 'vitest'
import { CONNECTION_ENGINES, connectionPath, connectionProblem, connectionYaml, emptyConnection, type ConnectionForm } from './connectionForm'
import { MODEL_KINDS, modelNameProblem } from './modelForm'

describe('New model', () => {
  it('offers the four kinds the tool can start', () => {
    expect(MODEL_KINDS.map((k) => k.key)).toEqual(['view', 'full', 'incremental_by_unique_key', 'incremental_by_time_range'])
  })

  it('needs a name of the form schema_name.object_name', () => {
    expect(modelNameProblem('orders')).toContain('schema_name.object_name')
    expect(modelNameProblem('marts.fct_returns')).toBeNull()
    expect(modelNameProblem('')).toBeNull()
  })
})

describe('New connection', () => {
  const form = (over: Partial<ConnectionForm> = {}): ConnectionForm => ({ ...emptyConnection, name: 'warehouse', engine: 'postgres', ...over })

  it('is the connection/1 file for the form, with no address and no login in it', () => {
    expect(connectionYaml(form({ encrypt: false, trustCertificate: true }))).toBe('schema: connection/1\nname: warehouse\nengine: postgres\ntls:\n  encrypt: false\n  trust_server_certificate: true\n')
    expect(connectionYaml(form())).not.toMatch(/address|host|password|user|auth/)
    expect(connectionPath(form())).toBe('connections/warehouse.yaml')
  })

  it('makes a DuckDB file a read-only origin with nothing else in the file', () => {
    expect(connectionYaml(form({ engine: 'duckdb' }))).toBe('schema: connection/1\nname: warehouse\nengine: duckdb\naccess: readonly\n')
  })

  it('says what is wrong with a name', () => {
    expect(connectionProblem(form({ name: 'Warehouse' }))).toContain('lower-case')
    expect(connectionProblem(form({ name: '1st' }))).toContain('lower-case')
    expect(connectionProblem(emptyConnection)).toBeNull()
  })

  it('offers the engines the tool builds on and a DuckDB origin', () => {
    expect(CONNECTION_ENGINES.map((e) => e.key)).toEqual(['sqlserver', 'postgres', 'fabric', 'duckdb'])
  })
})
