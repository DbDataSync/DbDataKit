import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { Diagnostic, ImportRow } from '../types'
import { ImportView, importFailure, importable, markOf, statusText, type ImportViewProps } from './ImportPanel'

const row = (name: string, status: string, columns = 3): ImportRow => ({ name, object_kind: 'table', file: `models/${name.replace('.', '/')}.yml`, status, columns: Array.from({ length: columns }, (_, i) => ({ name: `c${i}`, native_type: 'int', logical_type: 'INTEGER', nullable: false })), changes: [], notes: [] })
const stale: Diagnostic = { code: 'DDB-228', severity: 'warning', title: 'A table cannot be imported as a mapped model', location: { file: 'models/raw/orders.yml', line: 0, column: 0 }, found: '`raw.orders` has a mapped model, but origin shows no table or view of that name.\nMore.', supported: '', fix: '' }

function props(over: Partial<ImportViewProps> = {}): ImportViewProps {
  return { connections: ['origin', 'sqlserver'], connection: 'origin', listing: false, rows: null, skipped: [], diagnostics: [], selected: [], importing: false, failure: null, written: null, onConnection: () => {}, onList: () => {}, onToggle: () => {}, onImport: () => {}, onOpen: () => {}, onCancel: () => {}, ...over }
}

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

describe('Import from a connection', () => {
  it('lists the connections of the project, and reads nothing until one is chosen and asked', () => {
    const html = renderToStaticMarkup(ImportView(props()))
    expect(html).toContain('>origin</option>')
    expect(html).toContain('never shown here')
    expect(find(ImportView(props({ connection: '' })), 'build-import-list')?.props.disabled).toBe(true)
    expect(find(ImportView(props()), 'build-import-list')?.props.disabled).toBe(false)
    expect(find(ImportView(props({ listing: true })), 'build-import-list')?.props.disabled).toBe(true)
    expect(html).not.toContain('build-import-tables')
  })

  it('shows each table with its columns and what importing it would do, and lets only new or changed ones be ticked', () => {
    const rows = [row('shop.orders', 'new', 5), row('shop.items', 'changed'), row('shop.same', 'unchanged'), row('raw.gone', 'stale', 0), row('raw.bad', 'invalid')]
    const view = ImportView(props({ rows }))
    const html = renderToStaticMarkup(view)
    expect(html).toContain('not mapped yet')
    expect(html).toContain('already mapped, the same')
    expect(html).toContain('a model, but the connection shows no such table')
    expect(find(view, 'build-import-check-shop.orders')?.props.disabled).toBe(false)
    expect(find(view, 'build-import-check-shop.items')?.props.disabled).toBe(false)
    for (const name of ['shop.same', 'raw.gone', 'raw.bad']) expect(find(view, `build-import-check-${name}`)?.props.disabled).toBe(true)
    expect(rows.filter(importable).map((r) => r.name)).toEqual(['shop.orders', 'shop.items'])
    expect(statusText('something_new')).toBe('something_new')
  })

  it('writes only what is ticked, and says how many', () => {
    const rows = [row('shop.orders', 'new'), row('shop.items', 'new')]
    expect(find(ImportView(props({ rows })), 'build-import-write')?.props.disabled).toBe(true)
    const calls: string[] = []
    const view = ImportView(props({ rows, selected: ['shop.orders'], onImport: () => calls.push('import'), onToggle: (n) => calls.push(`toggle ${n}`) }))
    expect(renderToStaticMarkup(view)).toContain('Import 1 table<')
    expect(renderToStaticMarkup(ImportView(props({ rows, selected: ['shop.orders', 'shop.items'] })))).toContain('Import 2 tables')
    ;(find(view, 'build-import-write')!.props.onClick as () => void)()
    ;(find(view, 'build-import-check-shop.items')!.props.onChange as () => void)()
    expect(calls).toEqual(['import', 'toggle shop.items'])
  })

  it('says when the connection shows nothing to map, and lists what was left out', () => {
    expect(renderToStaticMarkup(ImportView(props({ rows: [] })))).toContain('build-import-none')
    const html = renderToStaticMarkup(ImportView(props({ rows: [row('a.b', 'new')], skipped: [{ object: 'marts.orders', reason: 'it is a built model, not a table to map' }] })))
    expect(html).toContain('marts.orders (it is a built model, not a table to map)')
  })

  it('shows what the tool says as markers, like the compiler’s, and opens the file one names', () => {
    const calls: string[] = []
    const view = ImportView(props({ rows: [row('a.b', 'new')], diagnostics: [stale, { ...stale, code: 'DDB-999', severity: 'note' }], onOpen: (f) => calls.push(f) }))
    const html = renderToStaticMarkup(view)
    expect(html.match(/build-import-diagnostic"/g)).toHaveLength(1)    // a note is not a marker
    expect(html).toContain('DDB-228')
    expect(html).toContain('data-severity="warning"')
    expect(html).toContain('`raw.orders` has a mapped model, but origin shows no table or view of that name.')
    expect(html).not.toContain('More.')
    ;(find(view, 'build-import-diagnostic')!.props.onClick as () => void)()
    expect(calls).toEqual(['models/raw/orders.yml'])
    expect(markOf(stale)).toBe('~')
    expect(markOf({ ...stale, severity: 'error' })).toBe('!')
  })

  it('reports what was written', () => {
    expect(renderToStaticMarkup(ImportView(props({ rows: [row('a.b', 'unchanged')], written: ['models/a/b.yml'] })))).toContain('Wrote models/a/b.yml.')
  })
})

describe('when the connection cannot be read', () => {
  const missing: Diagnostic = { code: 'DDB-501', severity: 'error', title: 'Login not configured', location: { file: 'env:DBDATA_BUILD_SQLSERVER_READ', line: 0, column: 0 }, found: 'The read login for connection `sqlserver` is not configured: environment variable `DBDATA_BUILD_SQLSERVER_READ` is not set.', supported: '', fix: '' }

  it('says there is no login on the server, naming the variable and no value', () => {
    const text = importFailure({ exit: 1, text: null, document: { ok: false, data: {} as never, diagnostics: [missing] } })
    expect(text).toContain('no read login on the server')
    expect(text).toContain('DBDATA_BUILD_SQLSERVER_READ')
  })

  it('gives the first sentence of anything else, never a trace, and nothing for a list', () => {
    expect(importFailure({ exit: 70, text: 'The connection could not be read.\n   at Npgsql.Foo()', document: null })).toBe('The connection could not be read.')
    expect(importFailure({ exit: 2, text: null, document: { ok: false, data: {} as never, errors: ['Unknown connection `nope`. One of: origin.'] } })).toContain('Unknown connection')
    expect(importFailure({ exit: 0, text: null, document: { ok: true, data: { connection: 'origin', mode: 'preview', mapped_models: [], skipped: [] } } })).toBeNull()
    expect(renderToStaticMarkup(ImportView(props({ failure: 'Broken.' })))).toContain('build-import-failed')
  })
})
