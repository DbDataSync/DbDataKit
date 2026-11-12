import { describe, expect, it } from 'vitest'
import {
  entryValue, isSemanticFile, lineOfEntry, modelNameOf, nameProblem, parameterValue, readQueries, removeEntry, renameEntry, writeEntry, type QueryEntry,
} from './semanticQueries'

const FILE = `schema: semantic/1
name: retail_sales
description: Retail
queries:
  # the monthly view
  monthly:
    description: Net by month
    sql: |
      SELECT sales.month, MEASURE(sales.net_amount)
      FROM retail_sales
      GROUP BY ALL
  by_region:
    request:
      kind: query
      query:
        measures: [sales.net_amount]
    parameters:
      regions:
        for: sales.region
        many: true
        empty: matches_none
`

const entry = (over: Partial<QueryEntry> = {}): QueryEntry => ({ name: 'top', description: '', kind: 'sql', body: 'SELECT 1 FROM retail_sales', parameters: [], ...over })

describe('The saved queries of a semantic file', () => {
  it('are read as entries with their kind, body and parameter definitions', () => {
    const read = readQueries(FILE)
    expect(read.problem).toBeNull()
    expect(read.entries.map((e) => [e.name, e.kind])).toEqual([['monthly', 'sql'], ['by_region', 'request']])
    expect(read.entries[0].description).toBe('Net by month')
    expect(read.entries[0].body).toContain('GROUP BY ALL')
    expect(JSON.parse(read.entries[1].body)).toEqual({ kind: 'query', query: { measures: ['sales.net_amount'] } })
    expect(read.entries[1].parameters).toEqual([{ name: 'regions', def: { for: 'sales.region', many: true, empty: 'matches_none' } }])
  })

  it('say why they cannot be shown when the file is not what the form reads', () => {
    expect(readQueries('a: [').problem).toMatch(/not valid YAML/)
    expect(readQueries('queries: nope\n').problem).toMatch(/mapping/)
    expect(readQueries('queries:\n  a: nope\n').problem).toMatch(/query a must be a mapping/)
    expect(readQueries('name: x\n')).toEqual({ entries: [], problem: null })
  })

  it('are written as exactly one of sql and request, with only what was set, and nothing else of the file changes', () => {
    const added = writeEntry(FILE, entry({ description: 'Top', parameters: [{ name: 'n', def: { type: 'integer', default: 10, required: false } }] }))
    expect(added).toContain('# the monthly view')
    expect(added).toContain('description: Retail')
    const top = readQueries(added).entries.find((e) => e.name === 'top')!
    expect(top.parameters).toEqual([{ name: 'n', def: { type: 'integer', default: 10, required: false } }])
    expect(entryValue(entry({ kind: 'request', body: '{"kind":"query"}' }))).toEqual({ request: { kind: 'query' } })
    expect(entryValue(entry({ kind: 'request', body: '{nope' }))).toBeNull()
    expect(() => writeEntry(FILE, entry({ kind: 'request', body: '{nope' }))).toThrow(/not valid JSON/)
    expect(entryValue(entry({ parameters: [{ name: 'a', def: { for: '', many: false, default: '' } }] }))).toEqual({ sql: 'SELECT 1 FROM retail_sales', parameters: { a: {} } })
  })

  it('are changed in place, removed and renamed, and the others stay', () => {
    const changed = writeEntry(FILE, { ...readQueries(FILE).entries[0], body: 'SELECT 2 FROM retail_sales' })
    expect(readQueries(changed).entries[0].body).toBe('SELECT 2 FROM retail_sales')
    expect(readQueries(changed).entries).toHaveLength(2)
    const removed = removeEntry(FILE, 'monthly')
    expect(readQueries(removed).entries.map((e) => e.name)).toEqual(['by_region'])
    const renamed = renameEntry(FILE, 'monthly', 'per_month')
    expect(readQueries(renamed).entries.map((e) => e.name).sort()).toEqual(['by_region', 'per_month'])
    expect(readQueries(renamed).entries.find((e) => e.name === 'per_month')!.description).toBe('Net by month')
    expect(() => renameEntry(FILE, 'nothing', 'x')).toThrow(/no query/)
  })

  it('give the line of a query for a message that says where', () => {
    expect(lineOfEntry(FILE, 'monthly')).toBe(8)
    expect(lineOfEntry(FILE, 'by_region', 'request')).toBe(13)
    expect(lineOfEntry(FILE, 'by_region', 'sql')).toBe(12)         // no sql: the query's own line
    expect(lineOfEntry(FILE, 'nothing')).toBeNull()
    expect(lineOfEntry('name: x\n', 'a')).toBeNull()
  })

  it('know a model file, its name and what a query may be called', () => {
    expect(isSemanticFile('semantic/retail.yml')).toBe(true)
    expect(isSemanticFile('models/a.sql')).toBe(false)
    expect(isSemanticFile('semantic/x.sql')).toBe(false)
    expect(modelNameOf(FILE, 'semantic/other.yml')).toBe('retail_sales')
    expect(modelNameOf('a: 1\n', 'semantic/other.yml')).toBe('other')
    expect(nameProblem('Top', [])).toMatch(/lower-case/)
    expect(nameProblem('top', ['top'])).toMatch(/already/)
    expect(nameProblem('top_2', ['monthly'])).toBeNull()
  })

  it('turn what is typed for a parameter into the value the call carries', () => {
    expect(parameterValue('', 'integer', false)).toBeNull()
    expect(parameterValue(' 12 ', 'integer', false)).toEqual({ value: 12 })
    expect(parameterValue('1.5', 'integer', false)).toEqual({ error: '1.5 is not a whole number.' })
    expect(parameterValue('2.5', 'number', false)).toEqual({ value: 2.5 })
    expect(parameterValue('x', 'number', false)).toEqual({ error: 'x is not a number.' })
    expect(parameterValue('true', 'boolean', false)).toEqual({ value: true })
    expect(parameterValue('yes', 'boolean', false)).toEqual({ error: 'yes is not true or false.' })
    expect(parameterValue('EU, US', 'string', true)).toEqual({ value: ['EU', 'US'] })
    expect(parameterValue('1, 2, x', 'integer', true)).toEqual({ error: 'x is not a whole number.' })
    expect(parameterValue('FY27', null, false)).toEqual({ value: 'FY27' })
  })
})
