// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { fixtureClient } from '../client/fixtureClient'
import { validateText } from '../client/text'
import { DashboardView } from '../dashboard/DashboardView'
import { sampleBundle } from '../testing/sampleBundle'
import type { FlowLayoutDoc, ViewsDoc } from '../views/model'
import type { RenderedWidget } from '../views/rendered'
import { PagedFrame, fitScale } from './PagedFrame'
import { ReportView } from './ReportView'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const bundle = sampleBundle()
const report = validateText(bundle.documents['report/retail-sales-monthly']!).document as ViewsDoc
const dashboard = validateText(bundle.documents['dashboard/retail-sales-today']!).document as ViewsDoc
const recorded = bundle.results!['report/retail-sales-monthly'] as Record<string, RenderedWidget>
// the resolver's sample result has no total row (the document asks for none): the report's totals-as-last-row rule is tested on the same rows with one appended
const products = recorded.products as Extract<RenderedWidget, { type: 'table' }>
const results: Record<string, RenderedWidget> = {
  ...recorded,
  products: { ...products, rows: [...products.rows, { total: true, cells: [{ v: 'Total', text: 'Total' }, { v: '', text: '' }, { v: 99999, text: '99,999' }, { v: 999999, text: '999,999' }] }] },
}
const paged = report.layouts.find((l) => l.name === 'paged') as FlowLayoutDoc
const box = { width: 1000, height: 700 }
const ASOF = '2026-11-08T09:30:00Z'

describe('PagedFrame', () => {
  it('compiles the sample report to the HTML of section 4.1 (snapshot)', async () => {
    const html = renderToStaticMarkup(<PagedFrame document={report} layout={paged} box={box} theme="light" results={results} parameters={{ region: [] }} asOf={ASOF} viewer="Ada" />)
    await expect(`${html.replace(/></g, '>\n<')}\n`).toMatchFileSnapshot('./__snapshots__/report-paged.html')
  })

  it('has the sections, a row of spans, a kept chart, the table with its head and the total once as the last row, the running text with its tokens', () => {
    const html = renderToStaticMarkup(<PagedFrame document={report} layout={paged} box={box} theme="light" results={results} parameters={{ region: [] }} asOf={ASOF} viewer="Ada" />)
    const dom = new DOMParser().parseFromString(`<body>${html}</body>`, 'text/html')
    const q = (s: string) => dom.querySelector(s)
    expect([...dom.querySelectorAll('.vz-flow-title')].map((e) => e.textContent)).toEqual(['Summary', 'Trend', 'Products'])
    expect(dom.querySelectorAll('[data-testid^="vz-flow-cell-"]').length).toBe(3)
    expect((q('[data-testid="vz-flow-cell-net"]') as HTMLElement).style.gridColumn).toBe('span 4')
    expect(q('[data-testid="vz-flow-block-by_month"]')!.hasAttribute('data-keep')).toBe(true)
    expect(q('[data-testid="vz-flow-block-by_month"]')!.getAttribute('style')).toContain('height:302.36px') // 80 mm
    const table = q('[data-testid="vz-table-products"]')!
    expect(table.querySelectorAll('thead').length).toBe(1)
    expect(table.querySelectorAll('tfoot').length).toBe(0)
    expect(table.querySelectorAll('tbody tr').length).toBe(25)
    expect(table.querySelectorAll('tr.vz-total').length).toBe(1)
    expect(table.querySelector('tbody tr:last-child')!.classList.contains('vz-total')).toBe(true)
    expect(q('[data-testid="vz-flow-header"]')!.textContent).toBe('Retail sales by month2026-11-08 09:30 UTC')
    expect(q('[data-testid="vz-flow-footer"]')!.textContent).toBe(`Page ${q('[data-testid="vz-flow"]')!.getAttribute('data-pages')} of ${q('[data-testid="vz-flow"]')!.getAttribute('data-pages')}`)
    expect(q('style')!.textContent).toContain('@page { size: A4 portrait')
    // the guides are the estimate: the table runs over one more page
    expect([...dom.querySelectorAll('[data-testid="vz-flow-guide"]')].map((g) => g.getAttribute('data-page'))).toEqual(['2'])
    expect(q('[data-testid="vz-flow-note"]')!.textContent).toContain('the PDF is exact')
  })

  it('a section break, a page break block and a cover are in the markup; the preview button is disabled and says why unless an exporter is wired', () => {
    const layout = { ...paged, cover: { parameters: true }, sections: [{ title: 'One', break: 'before', blocks: [{ widget: 'net' }, { pagebreak: true }] }, { break: 'after', blocks: [{ widget: 'units', repeat_header: false }] }] } as FlowLayoutDoc
    const html = renderToStaticMarkup(<PagedFrame document={report} layout={layout} box={box} theme="dark" results={results} parameters={{ region: ['West'] }} asOf={ASOF} />)
    const dom = new DOMParser().parseFromString(`<body>${html}</body>`, 'text/html')
    expect(dom.querySelector('[data-testid="vz-flow-cover"]')!.textContent).toContain('Region')
    expect(dom.querySelector('[data-testid="vz-flow-cover"]')!.textContent).toContain('West')
    expect(dom.querySelector('[data-testid="vz-flow-section-0"]')!.hasAttribute('data-break-before')).toBe(true)
    expect(dom.querySelector('[data-testid="vz-flow-section-1"]')!.hasAttribute('data-break-after')).toBe(true)
    expect(dom.querySelectorAll('[data-testid="vz-flow-break"]').length).toBe(1)
    expect(dom.querySelector('[data-testid="vz-flow"]')!.getAttribute('data-vz-theme')).toBe('dark')
    const preview = dom.querySelector('[data-testid="vz-flow-preview"]') as HTMLButtonElement
    expect(preview.disabled).toBe(true)
    expect(preview.title).toContain('exporter')
    const wired = renderToStaticMarkup(<PagedFrame document={report} layout={layout} box={box} theme="light" results={results} onPrintPreview={() => {}} />)
    expect(wired).not.toContain('data-testid="vz-flow-preview" disabled')
  })

  it('a widget with no result draws its loading state, one with an error its error tile', () => {
    const html = renderToStaticMarkup(<PagedFrame document={report} layout={paged} box={box} theme="light" results={{ net: { type: 'error', code: 'denied', message: 'No access.' } }} loading={new Set(['products'])} />)
    expect(html).toContain('No access.')
    expect(html).toContain('aria-busy')
  })
})

describe('in a view', () => {
  let host: HTMLDivElement
  let root: Root
  beforeEach(() => {
    host = document.createElement('div')
    document.body.appendChild(host)
    root = createRoot(host)
  })
  afterEach(async () => {
    await act(async () => root.unmount())
    host.remove()
  })
  const q = (id: string) => host.querySelector(`[data-testid="${id}"]`) as HTMLElement | null

  it('ReportView draws the report\'s flow layout, resolved through the client as the viewer', async () => {
    const asked: string[][] = []
    const client = fixtureClient(bundle, { onResolve: (c) => asked.push(c.widgets) })
    await act(async () => root.render(<ReportView name="retail-sales-monthly" document={report} client={client} theme="light" box={{ width: 1000, height: 700 }} viewer="Ada" />))
    expect(q('vz-flow')!.getAttribute('data-layout')).toBe('paged')
    expect(asked).toEqual([['intro', 'net', 'margin', 'units', 'by_month', 'products']])
    expect(q('vz-flow-header')!.textContent).toContain('Retail sales by month')
    expect(q('vz-flow-header')!.textContent).toContain('2026-')
    expect(q('vz-table-products')!.querySelectorAll('tbody tr').length).toBe(24)
    expect(q('vz-dashboard')).toBeNull()
  })

  it('a dashboard is a one-page dashboard by default and, with ?layout=print, a paginated report; the layout switch keeps the results', async () => {
    const asked: string[][] = []
    const client = fixtureClient(bundle, { onResolve: (c) => asked.push(c.widgets) })
    const show = (layout?: string) => act(async () => root.render(<DashboardView name="retail-sales-today" document={dashboard} client={client} theme="light" box={{ width: 1366, height: 768 }} layout={layout} />))
    await show()
    expect(q('vz-dashboard')!.getAttribute('data-layout')).toBe('desk')
    expect(q('vz-flow')).toBeNull()
    await show('print')
    expect(q('vz-flow')!.getAttribute('data-layout')).toBe('print')
    expect(q('vz-dashboard')).toBeNull()
    expect(q('vz-flow')!.querySelector('style')!.textContent).toContain('size: A4 landscape')
    expect(q('vz-table-top_products')!.querySelectorAll('tbody tr').length).toBe(24)
    expect(q('vz-flow-cell-net')).not.toBeNull()
    expect(asked).toEqual([['regions', 'net', 'margin', 'note', 'by_region', 'top_products']]) // print places five of them, all resolved already
    await show()
    expect(q('vz-dashboard')!.getAttribute('data-layout')).toBe('desk')
  })

  it('reports a block wider than the page after layout, once (a browser measures; here the measure is given)', async () => {
    const wide: string[][] = []
    const original = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollWidth')
    Object.defineProperty(HTMLElement.prototype, 'scrollWidth', { configurable: true, get() { return (this as HTMLElement).dataset.flowKey === 's2b0' ? 900 : 100 } })
    Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, get() { return 680 } })
    try {
      await act(async () => root.render(<PagedFrame document={report} layout={paged} box={box} theme="light" results={results} onOverWidth={(w) => wide.push(w)} />))
      expect(wide).toEqual([['products']])
      expect(q('vz-flow-overwidth')!.textContent).toContain('Products')
      expect(q('vz-flow-overwidth')!.textContent).toContain('180 mm')
    } finally {
      if (original) Object.defineProperty(HTMLElement.prototype, 'scrollWidth', original)
      else delete (HTMLElement.prototype as unknown as Record<string, unknown>).scrollWidth
      delete (HTMLElement.prototype as unknown as Record<string, unknown>).clientWidth
    }
  })

  it('scales a sheet wider than its window to fit, and leaves one that fits alone', async () => {
    expect(fitScale(1000, 680)).toBe(1)
    expect(fitScale(0, 680)).toBe(1)
    expect(fitScale(392, 680)).toBeCloseTo(0.529, 3)
    expect(fitScale(100, 680)).toBeCloseTo(1 / 3, 5) // never smaller than a third
    await act(async () => root.render(<PagedFrame document={report} layout={paged} box={{ width: 392, height: 700 }} theme="light" results={results} />))
    expect(q('vz-flow-fit')!.getAttribute('data-scale')).not.toBeNull()
    expect(q('vz-flow-sheet')!.style.transform).toMatch(/^scale\(0\.45/)
    await act(async () => root.render(<PagedFrame document={report} layout={paged} box={box} theme="light" results={results} />))
    expect(q('vz-flow-fit')!.getAttribute('data-scale')).toBeNull()
    expect(q('vz-flow-sheet')!.style.transform).toBe('')
  })
})
