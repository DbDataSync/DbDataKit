import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { allTablesOf, leftOut, patternProblem, patternsOf, readImport, statusWord, writableFiles } from '../buildImport'
import { RECORDED_IMPORT, RECORDED_IMPORT_DIAGNOSTICS } from '../buildImport.fixture'
import { BuildImportPanel, ImportResultView } from './BuildImportPanel'

describe('the import document of the real tool', () => {
  const read = readImport(RECORDED_IMPORT)
  it('is read into the models, the notes and the files, nothing dropped', () => {
    expect(read.dropped).toBe(0)
    expect(read.connection).toBe('sqlserver')
    expect(read.models.map((m) => [m.name, m.objectKind, m.status])).toEqual([['crm.account_totals', 'view', 'new'], ['crm.accounts', 'table', 'new']])
    expect(read.models[1].grain).toEqual(['id'])
    expect(read.notes[0]).toContain('no index and no foreign key was read')
    expect(read.files.map((f) => f.path)).toEqual(['models/crm/account_totals.yml', 'models/crm/accounts.yml'])
    expect(writableFiles(read)).toHaveLength(2)
  })
  it('keeps what the type mapping said: the logical type, how faithful it is, and the column left out with the reason', () => {
    const accounts = read.models[1]
    expect(accounts.columns.map((c) => [c.name, c.nativeType, c.logicalType])).toEqual([['id', 'int', 'INTEGER'], ['name', 'nvarchar(80)', 'VARCHAR(80)'], ['balance', 'decimal(18, 2)', 'DECIMAL(18, 2)'], ['opened', 'datetime2(3)', 'TIMESTAMP'], ['area', 'geography', null]])
    expect(leftOut(accounts).map((c) => [c.name, c.fit])).toEqual([['area', 'none']])
    expect(leftOut(accounts)[0].reason).toContain('geography has no logical type')
    const total = read.models[0].columns.find((c) => c.name === 'total')!
    expect(total).toMatchObject({ logicalType: 'DECIMAL(19, 4)', fit: 'widened' })
  })
  it('counts what it cannot place and never guesses', () => {
    const odd = readImport({ mapped_models: [{ name: 'a' }, { name: 'a', status: 'bogus' }, { name: 'a.b', status: 'new', columns: [{ name: 1 }] }, 4], skipped: [{ reason: 'x' }], files: [{ path: 'p' }] })
    expect(odd.models).toHaveLength(1)
    expect(odd.dropped).toBe(6)
    expect(readImport(null)).toMatchObject({ models: [], dropped: 0 })
  })
  it('does not offer to write a file that was cut for its size', () => {
    expect(writableFiles({ ...read, files: [{ path: 'm.yml', content: 'x', exists: false, truncated: true }] })).toEqual([])
  })
})

describe('the patterns a person types', () => {
  it('are split, listed from the cache and refused before anything runs when they are an option or not schema.table', () => {
    expect(patternsOf(' src.* , sales.orders  ')).toEqual(['src.*', 'sales.orders'])
    expect(patternsOf('')).toEqual([])
    expect(allTablesOf([{ schema: 'crm', name: 'accounts' }])).toEqual(['crm.accounts'])
    expect(patternProblem('src.*')).toBeNull()
    expect(patternProblem('--write')).toContain('option')
    expect(patternProblem('nodot')).toContain('schema.table')
    expect(patternProblem('a/b.c')).toContain('schema.table')
  })
  it('say what each status means for the file', () => {
    expect(statusWord('new')).toContain('no descriptor yet')
    expect(statusWord('stale')).toContain('no such table')
    expect(statusWord('unchanged')).toBe('unchanged')
  })
})

describe('the import drawn', () => {
  const read = readImport(RECORDED_IMPORT)
  const diagnostics = RECORDED_IMPORT_DIAGNOSTICS.map((d) => ({ ...d, line: null }))
  it('lists each model with its status, the column left out in red, the widened one as a note and a checkbox for each file', () => {
    const html = renderToStaticMarkup(<ImportResultView read={read} diagnostics={diagnostics} chosen={new Set(['models/crm/accounts.yml'])} onChoose={() => {}} />)
    expect(html.match(/data-testid="wsp-import-model"/g)).toHaveLength(2)
    expect(html).toContain('data-status="new"')
    expect(html.match(/data-testid="wsp-import-choose"/g)).toHaveLength(2)
    expect(html.match(/checked=""/g)).toHaveLength(1)
    expect(html).toContain('area (geography) is left out: geography has no logical type')
    expect(html).toContain('total: money as DECIMAL(19, 4) (widened: money is a fixed-point type with 4 decimals)')
    expect(html).toContain('Note: the catalog cache holds columns')
    expect(html).toContain('DDB-226')
  })
  it('says so when no cached table matched, and shows what the tool refused', () => {
    const html = renderToStaticMarkup(<ImportResultView read={{ ...read, models: [], files: [], notes: [] }} diagnostics={[{ severity: 'error', code: 'DBD-WS-BUILD-IMPORT', message: 'The catalog cache has no entry for the connection `x`', file: null, line: null }]} chosen={new Set()} onChoose={() => {}} />)
    expect(html).toContain('wsp-import-none')
    expect(html).toContain('DBD-WS-BUILD-IMPORT: The catalog cache has no entry')
  })
  it('is a form over the cached connections, with no result until it is asked', () => {
    const connections = { sqlserver: { engine: 'sqlserver', tables: [{ schema: 'crm', name: 'accounts', kind: 'table' as const, columns: [] }] } }
    const html = renderToStaticMarkup(<QueryClientProvider client={new QueryClient()}><BuildImportPanel project="p" id="d" connections={connections} canWrite /></QueryClientProvider>)
    expect(html).toContain('data-testid="wsp-import-run"')
    expect(html.match(/<option/g)).toHaveLength(1)
    expect(html).not.toContain('wsp-import-result')
    expect(html).not.toContain('wsp-import-write')
  })
})
