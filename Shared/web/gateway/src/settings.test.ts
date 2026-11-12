import { describe, expect, it } from 'vitest'
import type { Diagnostic, SettingsCheck } from './types'
import { MODULES, SEMANTIC_NUMBERS, cacheSeconds, editFor, fieldOf, groupDiagnostics, placeOf, restartText, saveState, semanticNumber, shown, toggleModule } from './settings'

const diag = (key: string | null, severity = 'error', line = 3): Diagnostic => ({ code: 'DBP-1', severity, key, file: '/x/config/server.yaml', line, column: 1, message: 'wrong', fix: null })
const check = (over: Partial<SettingsCheck> = {}): SettingsCheck => ({ valid: true, diagnostics: [], fields: {}, changed: ['logSql'], restart_keys: [], problems: [], ...over })
const state = (over: Partial<Parameters<typeof saveState>[0]> = {}) => saveState({ allowed: true, dirty: true, checking: false, check: check(), checkFailed: false, busy: false, ...over })

describe('errors by field', () => {
  it('finds the form field a diagnostic belongs to, whatever the case or the index', () => {
    expect(fieldOf(diag('Views.CacheSeconds'))).toBe('views.cacheSeconds')
    expect(fieldOf(diag('Modules[1]'))).toBe('modules')
    expect(fieldOf(diag('Views.Export.Mode'))).toBe('views.export.mode')
    expect(fieldOf(diag('Tls.Mode'))).toBeNull()
    expect(fieldOf(diag(null))).toBeNull()
  })

  it('groups the diagnostics by field, the rest in order, and counts the errors but not the warnings', () => {
    const g = groupDiagnostics([diag('Views.CacheSeconds'), diag('Tls.Mode', 'warning'), diag('Modules[0]'), diag(null)])
    expect(g.byField['views.cacheSeconds']).toHaveLength(1)
    expect(g.byField.modules).toHaveLength(1)
    expect(g.other).toHaveLength(2)
    expect(g.errors).toBe(3)
  })

  it('says where in the text', () => {
    expect(placeOf(diag('x', 'error', 12))).toBe('server.yaml:12')
    expect(placeOf(diag('x', 'error', 0))).toBe('server.yaml')
    expect(placeOf({ file: 'C:\\data\\config\\server.yaml', line: 4 })).toBe('server.yaml:4')
  })
})

describe('the Save button', () => {
  it('is on for a changed, valid draft', () => expect(state()).toEqual({ enabled: true, why: null }))

  it('says why it is off, in this order: role, saving, nothing changed, check failed, checking, problems', () => {
    expect(state({ allowed: false }).why).toContain('admin role')
    expect(state({ busy: true }).why).toBe('Saving…')
    expect(state({ dirty: false }).why).toBe('Nothing is changed yet.')
    expect(state({ checkFailed: true }).why).toContain('could not check')
    expect(state({ checking: true }).why).toBe('Checking the text…')
    expect(state({ check: undefined }).why).toBe('Checking the text…')
    expect(state({ check: check({ valid: false, diagnostics: [diag('Views.CacheSeconds')] }) })).toEqual({ enabled: false, why: 'Fix the 1 problem first.' })
    expect(state({ check: check({ valid: false, diagnostics: [diag('a'), diag('b')], problems: ['c'] }) }).why).toBe('Fix the 3 problems first.')
    expect(state({ check: check({ valid: false, problems: ['kept by pairing'] }) }).enabled).toBe(false)
  })

  it('is not stopped by a warning', () => expect(state({ check: check({ diagnostics: [diag('Tls.Mode', 'warning')] }) }).enabled).toBe(true))
})

describe('a restart', () => {
  it('is said for what a running Gateway reads at start, before and after saving', () => {
    expect(restartText([], false)).toBeNull()
    expect(restartText([], true)).toContain('nothing here needs a restart')
    expect(restartText(['modules'], true)).toBe('Saved, but a running Gateway reads modules only when it starts: restart the Gateway for it to take effect.')
    expect(restartText(['modules', 'views'], false)).toBe('modules, views are read when the Gateway starts: saving this needs a restart to take effect.')
  })
})

describe('the form', () => {
  it('switches a module on in the order of the known modules and off by removing it, keeping one it does not know', () => {
    expect(toggleModule(['flightsql'], 'views', true)).toEqual(['flightsql', 'views'])
    expect(toggleModule(['views', 'flightsql'], 'apps', true)).toEqual(['flightsql', 'apps', 'views'])
    expect(toggleModule(['flightsql', 'views'], 'views', false)).toEqual(['flightsql'])
    expect(toggleModule(['flightsql', 'custom'], 'views', true)).toEqual(['flightsql', 'views', 'custom'])
  })

  it('asks the Gateway for the edit of a field: a list, a number, a switch, or a key that is left out when emptied', () => {
    expect(editFor('modules', ['flightsql', 'views'])).toEqual({ list: { modules: ['flightsql', 'views'] } })
    expect(editFor('views.cacheSeconds', 60)).toEqual({ set: { 'views.cacheSeconds': 60 } })
    expect(editFor('logSql', false)).toEqual({ set: { logSql: false } })
    expect(editFor('views.export.mode', 'system')).toEqual({ set: { 'views.export.mode': 'system' } })
    expect(editFor('views.export.mode', '')).toEqual({ remove: ['views.export.mode'] })
    expect(editFor('views.export.path', '')).toEqual({ remove: ['views.export.path'] })
  })

  it('reads a whole number of seconds in range and nothing else', () => {
    expect(cacheSeconds('0')).toBe(0)
    expect(cacheSeconds(' 3600 ')).toBe(3600)
    for (const bad of ['', '-1', '3601', '1.5', 'x', '12345']) expect(cacheSeconds(bad), bad).toBeNull()
  })

  it('shows the values the draft sets, with the defaults when the check has not answered', () => {
    const { semantic: defaults, ...rest } = shown(undefined)
    expect(rest).toEqual({ modules: [], cacheSeconds: 30, exportMode: '', exportPath: '', logSql: true, telemetryEnabled: false, telemetryEndpoint: '', telemetryProtocol: 'grpc' })
    expect(defaults['semantic.level']).toBe(5)
    expect(shown({ modules: ['flightsql'], 'views.cacheSeconds': 5, 'views.export.mode': 'system', 'views.export.path': '/opt/chrome', logSql: false })).toMatchObject({ modules: ['flightsql'], cacheSeconds: 5, exportMode: 'system', exportPath: '/opt/chrome', logSql: false })
  })
})

describe('the semantic section', () => {
  it('offers the feeds module and the numbers of its rest section, found by their diagnostics', () => {
    expect(MODULES).toContain('feeds')
    expect(fieldOf(diag('Rest.Port'))).toBe('rest.port')
    expect(fieldOf(diag('Rest.DefaultLimit'))).toBe('rest.defaultLimit')
    const port = SEMANTIC_NUMBERS.find((f) => f.key === 'rest.port')!
    expect(semanticNumber(port, '8821')).toBe(8821)
    expect(semanticNumber(port, '70000')).toBeNull()
    expect(editFor('rest.maxRows', 500)).toEqual({ set: { 'rest.maxRows': 500 } })
    expect(toggleModule(['flightsql'], 'feeds', true)).toEqual(['flightsql', 'feeds'])
  })

  const level = SEMANTIC_NUMBERS[0]
  const memory = SEMANTIC_NUMBERS[1]
  it('finds the field a diagnostic of the section belongs to', () => {
    expect(fieldOf(diag('Semantic.Combine.MemoryMb'))).toBe('semantic.combine.memoryMb')
    expect(fieldOf(diag('Semantic.Level'))).toBe('semantic.level')
    expect(fieldOf(diag('Semantic.Models[0].Catalog'))).toBeNull()
  })
  it('reads a whole number in the range of the field and nothing else', () => {
    expect(semanticNumber(level, '3')).toBe(3)
    expect(semanticNumber(level, '6')).toBeNull()
    expect(semanticNumber(memory, '15')).toBeNull()
    expect(semanticNumber(memory, ' 512 ')).toBe(512)
    expect(semanticNumber(memory, '5e2')).toBeNull()
  })
  it('edits the number by its path and shows the draft values with the Gateway defaults beneath', () => {
    expect(editFor('semantic.combine.timeoutSeconds', 30)).toEqual({ set: { 'semantic.combine.timeoutSeconds': 30 } })
    expect(shown(undefined).semantic).toEqual({ 'semantic.level': 5, 'semantic.combine.memoryMb': 1024, 'semantic.combine.timeoutSeconds': 60, 'semantic.combine.maxInputRows': 5000000, 'rest.port': 8821, 'rest.defaultLimit': 1000, 'rest.maxRows': 10000 })
    expect(shown({ 'semantic.level': 2 }).semantic['semantic.level']).toBe(2)
  })
})
