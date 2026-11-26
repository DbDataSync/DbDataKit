import { describe, expect, it } from 'vitest'
import { connectionReader } from './connectionReader'
import type { ConnectionView } from '../types'

const view = (engine: string, scripts: ConnectionView['scripts'] = {}): ConnectionView => ({ name: 'crm', path: 'config/connections/crm.yaml', engine, database: null, scripts })

describe('who reads a connection of the set', () => {
  it('is the Runner\'s built-in package for an engine it opens', () => {
    for (const engine of ['sqlserver', 'postgres', 'mysql', 'mariadb', 'oracle']) expect(connectionReader(view(engine))).toEqual({ reader: 'builtin', why: null })
    expect(connectionReader(null).reader).toBe('builtin')
  })

  it('is the set\'s own package for a driver that is Sync\'s own, or a connection whose metadataProvider script answers its catalog', () => {
    expect(connectionReader(view('sqlite.generic'))).toEqual({ reader: 'package', why: "crm is a sqlite.generic connection, whose driver is Sync's own, so this set's own package reads it, in the build the channel holds." })
    expect(connectionReader(view('duckdb')).reader).toBe('package')
    expect(connectionReader(view('flightsql')).reader).toBe('package')
    const scripted = connectionReader(view('sqlserver', { metadataProvider: { script: 'catalog', parameters: {} } as never }))
    expect(scripted.reader).toBe('package')
    expect(scripted.why).toContain('metadataProvider script answers its catalog')
    // a binding cleared to null is no script
    expect(connectionReader(view('sqlserver', { metadataProvider: null })).reader).toBe('builtin')
  })
})
