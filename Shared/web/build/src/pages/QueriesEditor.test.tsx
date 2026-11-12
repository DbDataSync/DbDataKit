import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it } from 'vitest'
import { checkMarkers, QueriesEditor, tryBlock } from './QueriesEditor'
import type { QueryCheckAnswer } from '../types'

const FILE = `name: retail_sales
queries:
  by_region:
    sql: SELEKT 1
    parameters:
      regions:
        for: sales.region
        many: true
`
const bad: QueryCheckAnswer = { ok: false, errors: [{ code: 'sql_syntax', message: 'near SELEKT' }], warnings: [], parameters: [], request: null }
const good: QueryCheckAnswer = { ok: true, errors: [], warnings: [], parameters: [{ name: 'regions', for: 'sales.region', type: 'string', many: true }], request: {} }

function draw(text: string, readOnly = false) {
  const client = new QueryClient()
  return renderToStaticMarkup(<QueryClientProvider client={client}><QueriesEditor project="retail" workspace="w1" path="semantic/retail.yml" text={text} onText={() => {}} readOnly={readOnly} /></QueryClientProvider>)
}

describe('The saved queries form', () => {
  it('draws a card for each query with its signature, the Try tool and the way to add one', () => {
    const html = draw(FILE)
    expect(html).toContain('Saved queries of retail_sales')
    expect(html).toContain('data-testid="build-queries-entry-by_region"')
    expect(html).toContain('data-testid="build-queries-param-regions"')
    expect(html).toContain('line 4')
    expect(html).toContain('data-testid="build-queries-add"')
    expect(html).toContain('data-testid="build-queries-try"')
  })

  it('says there is none yet, and offers no way to change a file a person may not author', () => {
    expect(draw('name: x\n')).toContain('build-queries-none')
    const readOnly = draw(FILE, true)
    expect(readOnly).not.toContain('build-queries-add')
    expect(readOnly).not.toContain('build-queries-remove')
  })

  it('says what it cannot read instead of drawing it', () => {
    expect(draw('a: [')).toContain('build-queries-problem')
  })

  it('puts the errors of the translation check on the line of the query', () => {
    expect(checkMarkers(FILE, 'by_region', 'sql', bad)).toEqual([{ line: 4, column: 1, message: 'sql_syntax: near SELEKT', severity: 'error' }])
    expect(checkMarkers(FILE, 'by_region', 'sql', good)).toEqual([])
    expect(checkMarkers(FILE, 'by_region', 'sql', undefined)).toEqual([])
  })

  it('says why Try is off', () => {
    expect(tryBlock('', 'm', false, false, good)).toMatch(/Choose the Gateway/)
    expect(tryBlock('edge', 'm', false, false, bad)).toMatch(/Fix what the check found/)
    expect(tryBlock('edge', 'm', true, false, good)).toMatch(/not valid JSON/)
    expect(tryBlock('edge', 'm', false, true, good)).toBe('Running…')
    expect(tryBlock('edge', 'm', false, false, good)).toBeNull()
    expect(tryBlock('edge', 'm', false, false, undefined)).toBeNull()
  })
})
