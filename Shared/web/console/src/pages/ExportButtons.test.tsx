import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { ViewsClient, ViewsDoc } from '@dbdatakit/visuals'
import { ExportButtons, widgetDownloads } from './ExportButtons'

const doc = { schemaVersion: 1, kind: 'dashboard', title: 'T', layouts: [], widgets: {
  chart1: { type: 'chart', title: 'By region' }, grid: { type: 'table', title: 'Rows' }, kpi: { type: 'metric', label: 'K' }, note: { type: 'text', text: 'x' },
} } as unknown as ViewsDoc

describe('what a widget can be downloaded as', () => {
  it('gives a chart rows and a picture, a table or matrix rows, and the rest nothing', () => {
    expect(widgetDownloads('chart')).toEqual(['csv', 'xlsx', 'svg'])
    expect(widgetDownloads('table')).toEqual(['csv', 'xlsx'])
    expect(widgetDownloads('matrix')).toEqual(['csv', 'xlsx'])
    for (const type of ['metric', 'text', 'image', 'control', 'embed', 'custom']) expect(widgetDownloads(type)).toEqual([])
  })
})

describe('the download buttons', () => {
  const client = { exportFile: async () => ({ fileName: 'x', contentType: 'x', bytes: new Uint8Array(), pages: null, browser: null }) } as unknown as ViewsClient

  it('are not drawn for a source with no server', () => {
    expect(renderToStaticMarkup(<ExportButtons client={{} as ViewsClient} kind="dashboard" name="t" document={doc} layout={null} parameters={{}} />)).toBe('')
  })

  it('offer the PDF, the Excel of the layout and a choice of the widgets that have rows', () => {
    const html = renderToStaticMarkup(<ExportButtons client={client} kind="dashboard" name="t" document={doc} layout={null} parameters={{}} />)
    expect(html).toContain('views-download-pdf')
    expect(html).toContain('views-download-xlsx')
    expect(html).toContain('By region')
    expect(html).toContain('Rows')
    expect(html).not.toContain('>K<')
  })
})
