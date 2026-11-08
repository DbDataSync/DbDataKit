import { describe, expect, it } from 'vitest'
import { engineName, modulesCurrent, offersState, stateBase, versionsLine } from './stateApi'

describe('stateApi helpers', () => {
  it('reaches a paired server through the relay and the console directly', () => {
    expect(stateBase('abc 1')).toBe('/relay/abc%201')
    expect(stateBase('console')).toBe('')
  })
  it('offers the panel for the products that mount the state API', () => {
    expect(offersState('dbdatagateway')).toBe(true)
    expect(offersState('dbdatasync')).toBe(false)
    expect(offersState(null)).toBe(false)
  })
  it('names engines and module versions', () => {
    expect(engineName('Postgres')).toBe('PostgreSQL')
    expect(engineName('mssql')).toBe('SQL Server')
    expect(versionsLine([])).toBe('none')
    expect(versionsLine([{ module: 'identity', scope: 'install', version: 3 }, { module: 'audit', scope: 'install', version: 1 }])).toBe('identity 3, audit 1')
  })
  it('says when a module differs from the binary', () => {
    expect(modulesCurrent([{ module: 'a', scope: null, binary_version: 2, registered_version: 2, pending: 0 }])).toBe(true)
    expect(modulesCurrent([{ module: 'a', scope: null, binary_version: 2, registered_version: 1, pending: 1 }])).toBe(false)
  })
})
