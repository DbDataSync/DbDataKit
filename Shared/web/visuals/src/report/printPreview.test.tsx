// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { fixtureClient } from '../client/fixtureClient'
import { validateText } from '../client/text'
import type { ExportOptions, ExportedFile, ViewsClient } from '../client/types'
import { ViewsClientError } from '../client/types'
import { DashboardView } from '../dashboard/DashboardView'
import { sampleBundle } from '../testing/sampleBundle'
import type { ViewsDoc } from '../views/model'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const bundle = sampleBundle()
const report = validateText(bundle.documents['report/retail-sales-monthly']!).document as ViewsDoc

let host: HTMLDivElement
let root: Root
beforeEach(() => {
  class Observer { cb: (e: Array<{ contentRect: { width: number; height: number } }>) => void; constructor(cb: (e: Array<{ contentRect: { width: number; height: number } }>) => void) { this.cb = cb } observe() { this.cb([{ contentRect: { width: 1000, height: 700 } }]) } unobserve() {} disconnect() {} }
  ;(globalThis as { ResizeObserver?: unknown }).ResizeObserver = Observer
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, get: () => 1000 })
  Object.defineProperty(HTMLElement.prototype, 'clientHeight', { configurable: true, get: () => 700 })
  host = window.document.createElement('div')
  window.document.body.appendChild(host)
  root = createRoot(host)
})
afterEach(async () => {
  await act(async () => root.unmount())
  host.remove()
})
const q = (id: string) => host.querySelector(`[data-testid="${id}"]`) as HTMLElement | null
const click = async (el: Element) => act(async () => { el.dispatchEvent(new window.MouseEvent('click', { bubbles: true })) })
const pdf = (pages: number | null): ExportedFile => ({ fileName: 'retail-sales-monthly.pdf', contentType: 'application/pdf', bytes: new TextEncoder().encode('%PDF-1.7 fake'), pages, browser: 'Chrome/1' })

describe('the print preview of a report (DBR27)', () => {
  it('a client with an exporter turns the button on; a click asks for the PDF of the layout in play with the parameters and shows its page count', async () => {
    const asked: Array<{ name: string; options?: ExportOptions }> = []
    const client: ViewsClient = { ...fixtureClient(bundle), async exportPdf(ref, options) { asked.push({ name: ref.name, options }); return pdf(3) } }
    await act(async () => root.render(<DashboardView kind="report" name="retail-sales-monthly" document={report} client={client} theme="light" />))
    const button = q('vz-flow-preview') as HTMLButtonElement
    expect(button.disabled).toBe(false)
    await click(button)
    expect(asked).toHaveLength(1)
    expect(asked[0]!.name).toBe('retail-sales-monthly')
    expect(asked[0]!.options!.layout).toBe('paged')
    expect(q('vz-print-preview')!.getAttribute('data-pages')).toBe('3')
    expect(q('vz-print-preview-pages')!.textContent).toBe('3 pages')
    await click(q('vz-print-preview-close')!)
    expect(q('vz-print-preview')).toBeNull()
  })

  it('a client with no exporter leaves the button disabled and says why', async () => {
    await act(async () => root.render(<DashboardView kind="report" name="retail-sales-monthly" document={report} client={fixtureClient(bundle)} theme="light" />))
    const button = q('vz-flow-preview') as HTMLButtonElement
    expect(button.disabled).toBe(true)
    expect(button.title).toContain('no exporter')
  })

  it('a refusal of the exporter is shown in the module\'s words and no dialog opens', async () => {
    const client: ViewsClient = { ...fixtureClient(bundle), async exportPdf() { throw new ViewsClientError('unavailable', 'Export of the report is unavailable: export unavailable: it is off on this host (the views.export setting is not set).', { status: 501 }) } }
    await act(async () => root.render(<DashboardView kind="report" name="retail-sales-monthly" document={report} client={client} theme="light" />))
    await click(q('vz-flow-preview')!)
    expect(q('vz-print-preview-refused')!.textContent).toContain('views.export setting')
    expect(q('vz-print-preview')).toBeNull()
  })
})
