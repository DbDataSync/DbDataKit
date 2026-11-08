import { describe, expect, it } from 'vitest'
import { bundleFrom, resolveLog, viewsClient } from './viewsSource'

describe('the fixture source of the Views page', () => {
  it('holds the retail sample bundle: both documents, valid, with the recorded result of every widget the desk layout places', async () => {
    const client = viewsClient()
    expect((await client.list()).map((d) => `${d.kind}/${d.name}`)).toEqual(['report/retail-sales-monthly', 'dashboard/retail-sales-today'])
    const opened = await client.open('dashboard', 'retail-sales-today')
    expect(opened.diagnostics.filter((d) => d.severity === 'error')).toEqual([])
    resolveLog.clear()
    const { widgets } = await client.resolve({ kind: 'dashboard', name: 'retail-sales-today' }, 'desk', {})
    expect(Object.values(widgets).filter((w) => w.type === 'error')).toEqual([])
    expect(resolveLog.snapshot()).toEqual([{ kind: 'dashboard', name: 'retail-sales-today', layout: 'desk', widgets: ['regions', 'net', 'margin', 'note', 'by_region', 'top_products'] }])
  })

  it('names a document and a result by its folder', () => {
    const bundle = bundleFrom({ '/x/dashboards/a.yaml': 'A', '/x/reports/b.yaml': 'B', '/x/other/c.yaml': 'C' }, { '/y/widget-results/dashboard/a/w1.json': { type: 'text' }, '/y/elsewhere.json': 1 })
    expect(bundle.documents).toEqual({ 'dashboard/a': 'A', 'report/b': 'B' })
    expect(bundle.results).toEqual({ 'dashboard/a': { w1: { type: 'text' } } })
  })
})
