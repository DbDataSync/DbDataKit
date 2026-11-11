import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { CompileResult, LoweredResult, SampleResult } from '../api'
import { cellText, compileActions, compileCounts, compiledWord, connectionOfRendered } from '../buildCompile'
import { CompileResultView, LoweredView, SampleView } from './BuildCompilePanel'

const compiled: CompileResult = {
  ok: true, message: '3 rendered file(s): 2 would be written, 1 removed.', diagnostics: [],
  files: [
    { path: 'rendered/sqlserver/orders.sql', status: 'added', content: '-- lowered\nSELECT 1 AS id', truncated: false },
    { path: 'rendered/sqlserver/loads.yml', status: 'changed', content: 'loads: []\n', truncated: false },
    { path: 'rendered/refresh.plan.yaml', status: 'unchanged', content: null, truncated: false },
    { path: 'rendered/stale.sql', status: 'removed', content: null, truncated: false },
    { path: 'rendered/big.sql', status: 'added', content: null, truncated: true },
  ],
}

describe('what a compile would do', () => {
  it('counts the files it writes, removes and leaves, and says each in words', () => {
    expect(compileCounts(compiled.files)).toEqual({ written: 3, removed: 1, unchanged: 1 })
    expect(compiledWord('added')).toBe('would be added')
    expect(compiledWord('removed')).toBe('would be removed')
  })
  it('writes only the files that came with their text, removes the removed, and names a file too large to write', () => {
    const actions = compileActions(compiled)
    expect(actions.write.map((f) => f.path)).toEqual(['rendered/sqlserver/orders.sql', 'rendered/sqlserver/loads.yml'])
    expect(actions.remove).toEqual(['rendered/stale.sql'])
    expect(actions.skipped).toEqual(['rendered/big.sql'])
  })
  it('knows the connection of a rendered file', () => {
    expect(connectionOfRendered('rendered/sqlserver/orders.sql')).toBe('sqlserver')
    expect(connectionOfRendered('rendered/refresh.plan.yaml')).toBeNull()
  })
  it('cuts a long cell and writes a null', () => {
    expect(cellText(null)).toBe('null')
    expect(cellText('x'.repeat(50), 10)).toBe('xxxxxxxxx…')
  })
})

describe('the compile drawn', () => {
  it('lists each file with its status and a button to write them', () => {
    const html = renderToStaticMarkup(<CompileResultView result={compiled} canWrite busy={false} onWrite={() => {}} />)
    expect(html).toContain('data-testid="wsp-compile-result" data-ok="true"')
    expect((html.match(/data-testid="wsp-compile-file"/g) ?? []).length).toBe(5)
    expect(html).toContain('data-status="removed"')
    expect(html).toContain('3 to write, 1 to remove, 1 as committed.')
    expect(html).toContain('data-testid="wsp-compile-skipped"')
    expect(html).not.toMatch(/data-testid="wsp-compile-write"[^>]*disabled/)
  })
  it('does not offer to write without the author role, or when there is nothing to write', () => {
    expect(renderToStaticMarkup(<CompileResultView result={compiled} canWrite={false} busy={false} onWrite={() => {}} />)).toMatch(/disabled=""[^>]*data-testid="wsp-compile-write"|data-testid="wsp-compile-write"[^>]*disabled/)
    const same: CompileResult = { ok: true, message: 'all as committed', diagnostics: [], files: [{ path: 'rendered/a.sql', status: 'unchanged', content: null, truncated: false }] }
    expect(renderToStaticMarkup(<CompileResultView result={same} canWrite busy={false} onWrite={() => {}} />)).toMatch(/disabled=""[^>]*data-testid="wsp-compile-write"|data-testid="wsp-compile-write"[^>]*disabled/)
  })
  it('shows the tool\'s findings with file and line and no files when the project has errors', () => {
    const bad: CompileResult = { ok: false, message: 'Nothing was compiled: the project has errors.', diagnostics: [{ severity: 'error', code: 'DDB-301', message: 'A table is not defined', file: 'models/marts/orders.sql', line: 1 }], files: [] }
    const html = renderToStaticMarkup(<CompileResultView result={bad} canWrite busy={false} onWrite={() => {}} />)
    expect(html).toContain('data-ok="false"')
    expect(html).toContain('models/marts/orders.sql:1 DDB-301')
    expect(html).not.toContain('wsp-compile-write')
  })
})

describe('the lowered SQL and the sample drawn', () => {
  it('shows the text per connection', () => {
    const result: LoweredResult = { ok: true, diagnostics: [], files: [{ path: 'rendered/sqlserver/orders.sql', content: 'SELECT 1 AS id' }] }
    const html = renderToStaticMarkup(<LoweredView result={result} />)
    expect(html).toContain('rendered/sqlserver/orders.sql')
    expect(html).toContain('SELECT 1 AS id')
  })
  it('shows a table with its rows and the count, and a table that failed with its error', () => {
    const result: SampleResult = {
      ok: false, diagnostics: [],
      data: { tables: [
        { name: 'marts.orders', kind: 'view', origin: null, columns: [{ name: 'id', type: 'BIGINT' }], row_count: 7, rows: [['1'], [null]], error: null, warnings: [] },
        { name: 'marts.bad', kind: 'view', origin: null, columns: [], row_count: 0, rows: [], error: 'Table nowhere does not exist', warnings: [] },
      ] },
    }
    const html = renderToStaticMarkup(<SampleView result={result} />)
    expect(html).toContain('data-name="marts.orders" data-rows="7"')
    expect((html.match(/data-testid="wsp-sample-row"/g) ?? []).length).toBe(2)
    expect(html).toContain('>null<')
    expect(html).toContain('Table nowhere does not exist')
  })
})
