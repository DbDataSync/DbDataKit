import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { followableColumns, readLineage, sourceLine, sourcesOf, stepLine } from '../buildLineage'
import { BuildLineagePanel, LineageView } from './BuildLineagePanel'
import { RECORDED_LINEAGE, RECORDED_FOLLOW } from '../buildLineage.fixture'

describe('the lineage document of the real tool', () => {
  const read = readLineage(RECORDED_LINEAGE)
  it('is read into the edges of every output column, nothing dropped', () => {
    expect(read.dropped).toBe(0)
    expect(read.columnEdges.length).toBeGreaterThan(0)
    expect(read.lineage).toBeNull()
    expect(followableColumns(read.columnEdges).length).toBeGreaterThan(0)
  })
  it('says what one column is made of, with the tool\'s own word for how', () => {
    const [first] = followableColumns(read.columnEdges)
    const dot = first.lastIndexOf('.')
    const sources = sourcesOf(read.columnEdges, first.slice(0, dot), first.slice(dot + 1))
    expect(sources.length).toBeGreaterThan(0)
    expect(sourceLine(sources[0])).toMatch(/\(.+\)$/)
  })
  it('reads a followed column into what it comes from and what is built from it, with the steps', () => {
    const followed = readLineage(RECORDED_FOLLOW)
    expect(followed.lineage).not.toBeNull()
    expect(followed.lineage!.upstream.length + followed.lineage!.downstream.length).toBeGreaterThan(0)
    for (const s of [...followed.lineage!.upstream, ...followed.lineage!.downstream]) expect(stepLine(s)).toContain(`${s.table}.${s.column}`)
  })
  it('counts what it cannot place and never guesses', () => {
    const odd = readLineage({ column_edges: [{ to_model: 'a' }, { to_model: 'a', to_column: 'b', transform: 'direct' }, 7], column_lineage: { column: 'a.b', upstream: [{ table: 'x' }], downstream: [] } })
    expect(odd.columnEdges).toHaveLength(1)
    expect(odd.dropped).toBe(3)
    expect(odd.lineage?.upstream).toEqual([])
    expect(readLineage(null)).toEqual({ columnEdges: [], lineage: null, dropped: 0 })
  })
  it('names a constant and a step count', () => {
    expect(sourceLine({ fromTable: null, fromColumn: null, toModel: 'm', toColumn: 'c', transform: 'constant' })).toBe('a constant (constant)')
    expect(stepLine({ table: 'staging.orders', column: 'id', distance: 2 })).toBe('staging.orders.id (2 steps)')
    expect(stepLine({ table: 'staging.orders', column: 'id', distance: 1 })).toBe('staging.orders.id')
  })
})

describe('the lineage drawn', () => {
  const read = readLineage(RECORDED_LINEAGE)
  const column = followableColumns(read.columnEdges)[0]
  it('offers every followable column and, for the chosen one, what it is made of', () => {
    const html = renderToStaticMarkup(<LineageView read={read} diagnostics={[]} chosen={column} onChoose={() => {}} />)
    expect(html.match(/<option/g)!.length).toBe(followableColumns(read.columnEdges).length + 1)
    expect(html).toContain('wsp-lineage-made-of')
    expect(html).toContain('wsp-lineage-source')
    expect(html).not.toContain('wsp-lineage-followed')
  })
  it('shows what the followed column comes from and what it feeds', () => {
    const followed = readLineage(RECORDED_FOLLOW)
    const html = renderToStaticMarkup(<LineageView read={{ ...read, lineage: followed.lineage }} diagnostics={[]} chosen={followed.lineage!.column} onChoose={() => {}} />)
    expect(html).toContain('wsp-lineage-followed')
    expect(html).toMatch(/wsp-lineage-(up|up-none)/)
    expect(html).toMatch(/wsp-lineage-(down|down-none)/)
  })
  it('says so when no built model has columns, and shows the findings of a project with errors', () => {
    const html = renderToStaticMarkup(<LineageView read={{ columnEdges: [], lineage: null, dropped: 0 }} diagnostics={[{ severity: 'error', code: 'DDB-301', message: 'A table is not defined', file: 'models/marts/orders.sql', line: 1 }]} chosen="" onChoose={() => {}} />)
    expect(html).toContain('wsp-lineage-none')
    expect(html).toContain('models/marts/orders.sql:1 DDB-301: A table is not defined')
  })
  it('is a button until it is asked, and reads the saved models only', () => {
    const html = renderToStaticMarkup(<QueryClientProvider client={new QueryClient()}><BuildLineagePanel project="p" id="d" /></QueryClientProvider>)
    expect(html).toContain('data-testid="wsp-lineage-run"')
    expect(html).not.toContain('wsp-lineage-result')
  })
})
