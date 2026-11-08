import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { chartToSvg } from '../chartSvg'
import { sampleBundle } from '../testing/sampleBundle'
import { validateText } from '../client/text'
import type { RenderedChart, RenderedMatrix } from '../types'
import { viewFixtures } from '../testing/fixtures'
import type { ControlDoc, MetricDoc, TableDoc, TextDoc, ViewsDoc, WidgetDoc } from '../views/model'
import type { RenderedControl, RenderedMetric, RenderedTable, RenderedWidget } from '../views/rendered'
import { fitChartSpec } from './chartFit'
import { densityClass, rowsThatFit, type DensityClass } from './density'
import { ImageBlock, IMAGE_MAX_BYTES, checkImageBytes, isSafeImageSrc } from './ImageBlock'
import { MetricTile } from './MetricTile'
import { ParameterControls, ControlWidget, controlKind } from './ParameterControls'
import { TableWidget } from './TableWidget'
import { TextBlock } from './TextBlock'
import { WidgetView } from './WidgetView'
import { MatrixWidget } from './MatrixWidget'
import { EmptyTile, ErrorTile, LoadingTile } from './states'
import { parseBlocks, parameterText, substituteParameters, unsafeTextReasons } from './markdown'

const bundle = sampleBundle()
const dashboard = validateText(bundle.documents['dashboard/retail-sales-today']!).document as ViewsDoc
const report = validateText(bundle.documents['report/retail-sales-monthly']!).document as ViewsDoc
const dash = bundle.results!['dashboard/retail-sales-today']!
const rep = bundle.results!['report/retail-sales-monthly']!
const html = (element: React.ReactElement) => renderToStaticMarkup(element)
const text = (fragment: string) => fragment.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").trim()
/** Several renderings in one snapshot file, each under its label, so a change shows which case moved. */
const cases = (list: Array<[string, string]>) => list.map(([label, markup]) => `<!-- ${label} -->\n${markup}`).join('\n\n') + '\n'

describe('density classes', () => {
  it.each([
    ['metric', { width: 400, height: 200 }, 'l'], ['metric', { width: 341, height: 192 }, 'l'], ['metric', { width: 250, height: 140 }, 'm'], ['metric', { width: 200, height: 110 }, 's'], ['metric', { width: 170, height: 90 }, 'xs'],
    ['chart', { width: 797, height: 480 }, 'l'], ['chart', { width: 450, height: 280 }, 'm'], ['chart', { width: 350, height: 230 }, 's'], ['chart', { width: 280, height: 180 }, 'xs'],
    ['table', { width: 640, height: 400 }, 'l'], ['matrix', { width: 360, height: 220 }, 'xs'],
  ] as Array<[string, { width: number; height: number }, DensityClass]>)('%s in %j is %s', (type, box, expected) => {
    expect(densityClass(type, box)).toBe(expected)
  })

  it('is the tighter side that counts, and a layout can pin it', () => {
    expect(densityClass('chart', { width: 2000, height: 190 })).toBe('xs')
    expect(densityClass('chart', { width: 281, height: 181 }, 'comfortable')).toBe('l')
    expect(densityClass('chart', { width: 2000, height: 1000 }, 'compact')).toBe('s')
  })

  it('a table shows at least one row, and one more for each 28 pixels', () => {
    expect(rowsThatFit(10)).toBe(1)
    expect(rowsThatFit(30 + 28 * 5)).toBe(5)
    expect(rowsThatFit(30 + 28 * 5, 36)).toBe(3)
  })
})

describe('MetricTile', () => {
  const widget = dashboard.widgets.net as MetricDoc
  const withChange: RenderedMetric = { type: 'metric', value: { v: 3300000, text: '$3.30M' }, compare: { v: 3170000, text: '$3.17M', change: { text: '+4.1%', direction: 'up' } } }

  it('draws the value, the label and the comparison at l and m, drops the label at s, the comparison too at xs', () => {
    const by = (d: DensityClass) => text(html(<MetricTile widget={widget} result={withChange} density={d} />))
    expect(by('l')).toBe('Net sales $3.30M \u25B2 up +4.1% vs $3.17M')
    expect(by('m')).toBe(by('l'))
    expect(by('s')).toBe('$3.30M \u25B2 up +4.1% vs $3.17M')
    expect(by('xs')).toBe('$3.30M')
  })

  it('tells the change by an arrow and a word, not by colour alone, and names itself to a screen reader', () => {
    const markup = html(<MetricTile widget={widget} result={{ ...withChange, compare: { text: '$3.5M', change: { text: '-8.0%', direction: 'down' } } }} />)
    expect(markup).toContain('▼')
    expect(markup).toContain('down ')
    expect(markup).toContain('role="group" aria-label="Net sales: $3.30M"')
  })

  it('snapshot', async () => {
    const plain: RenderedMetric = dash.net as RenderedMetric
    await expect(cases([
      ['l, with change', html(<MetricTile widget={widget} result={withChange} density="l" />)],
      ['l, a plain number', html(<MetricTile widget={widget} result={plain} density="l" />)],
      ['s', html(<MetricTile widget={widget} result={withChange} density="s" />)],
      ['xs', html(<MetricTile widget={widget} result={withChange} density="xs" />)],
      ['flat', html(<MetricTile widget={widget} result={{ type: 'metric', value: { text: '38.2%' }, compare: { text: '38.2%', change: { text: '0.0 pt', direction: 'flat' } } }} />)],
    ])).toMatchFileSnapshot('./__snapshots__/metric.html')
  })
})

describe('TableWidget', () => {
  const widget = dashboard.widgets.top_products as TableDoc
  const result = dash.top_products as RenderedTable
  const longWidget = report.widgets.products as TableDoc
  const long = rep.products as RenderedTable

  it('shows the rows that fit its box, says "n of m rows" and offers the whole', () => {
    const markup = html(<TableWidget id="top_products" widget={widget} result={result} box={{ width: 500, height: 30 + 28 * 8 + 36 }} onMaximize={() => {}} />)
    expect(markup.match(/<tr>/g)!.length - 1).toBe(8)
    expect(text(markup)).toContain('8 of 24 rows')
    expect(markup).toContain('aria-label="Show all 24 rows of Best-selling products"')
  })

  it('shows every row, and no notice, when it fits or is maximized', () => {
    const tall = html(<TableWidget id="t" widget={widget} result={result} box={{ width: 500, height: 30 + 28 * 24 + 40 }} />)
    expect(tall.match(/<tr>/g)!.length - 1).toBe(24)
    expect(tall).not.toContain('vz-fit')
    const maximized = html(<TableWidget id="t" widget={widget} result={result} box={{ width: 500, height: 100 }} maximized />)
    expect(maximized.match(/<tr>/g)!.length - 1).toBe(24)
    expect(maximized).toContain('vz-maximized')
  })

  it('keeps the total row, which is a row of the result, when it cuts the rest', () => {
    const markup = html(<TableWidget id="p" widget={longWidget} result={long} box={{ width: 700, height: 30 + 28 * 6 + 36 }} />)
    expect(text(markup)).toContain('5 of 60 rows')
    expect(markup).toMatch(/<tr class="vz-total"><td[^>]*>Total<\/td>/)
    expect(markup.match(/<tr[ >]/g)!.length - 1).toBe(6)
  })

  it('says when the server cut the rows', () => {
    const markup = html(<TableWidget id="t" widget={widget} result={{ ...result, total_rows: 400 }} />)
    expect(text(markup)).toContain('24 of 400 rows')
  })

  it('reads as a table: a caption, column headers with scope, the server\'s text, numbers aligned right', () => {
    const markup = html(<TableWidget id="top_products" widget={widget} result={result} />)
    expect(markup).toContain('<caption class="vz-sr">Best-selling products</caption>')
    expect(markup).toContain('<th scope="col">Product</th>')
    expect(markup).toContain('<th scope="col" class="vz-right">Units</th>')
    expect(markup).toContain('<td class="vz-right">2,400</td>')
  })

  it('snapshot', async () => {
    await expect(cases([
      ['all rows', html(<TableWidget id="top_products" widget={widget} result={{ ...result, rows: result.rows.slice(0, 4) }} />)],
      ['cut to the box', html(<TableWidget id="top_products" widget={widget} result={result} box={{ width: 500, height: 30 + 28 * 5 + 36 }} onMaximize={() => {}} />)],
      ['with a total row, cut', html(<TableWidget id="products" widget={longWidget} result={long} box={{ width: 700, height: 30 + 28 * 4 + 36 }} onMaximize={() => {}} />)],
    ])).toMatchFileSnapshot('./__snapshots__/table-widget.html')
  })
})

describe('MatrixWidget', () => {
  const fixture = viewFixtures().find((f) => f.name === '01_matrix_basic')!.rendered.views![0] as RenderedMatrix
  const wide = viewFixtures().find((f) => f.name === '10_column_limit_top_n_other')!.rendered.views![0] as RenderedMatrix

  it('draws the whole grid when it fits and cuts columns before rows when it does not, keeping totals', () => {
    const whole = html(<MatrixWidget view={fixture} box={{ width: 900, height: 400 }} />)
    expect(whole).not.toContain('vz-fit')
    const narrow = html(<MatrixWidget view={fixture} box={{ width: 132 + 96, height: 400 }} onMaximize={() => {}} />)
    expect(text(narrow)).toMatch(/1 of \d+ columns/)
    expect(narrow).toContain('Show the whole matrix')
    const short = html(<MatrixWidget view={wide} box={{ width: 900, height: 28 * 3 + 40 }} />)
    expect(text(short)).toMatch(/\d+ of \d+ rows/)
  })

  it('maximized is whole', () => {
    expect(html(<MatrixWidget view={fixture} box={{ width: 10, height: 10 }} maximized />)).not.toContain('vz-fit')
  })

  it('snapshot', async () => {
    await expect(cases([
      ['whole', html(<MatrixWidget view={fixture} box={{ width: 900, height: 400 }} />)],
      ['cut to the box', html(<MatrixWidget view={fixture} box={{ width: 132 + 96, height: 28 * 3 + 40 }} onMaximize={() => {}} title="Revenue" />)],
    ])).toMatchFileSnapshot('./__snapshots__/matrix-widget.html')
  })
})

describe('TextBlock: the safe subset of markdown', () => {
  const widget = (value: string): TextDoc => ({ type: 'text', text: value })

  it('draws headings, paragraphs, lists, bold, italic, code and a link to a path on the host', () => {
    const markup = html(<TextBlock widget={widget('# Title\n\nSome **bold**, *italic* and `code` with a [link](/docs/x).\nSecond line.\n\n- one\n- two\n\n1. first\n2. second')} />)
    expect(markup).toContain('<h2>Title</h2>')
    expect(markup).toContain('<p>Some <strong>bold</strong>, <em>italic</em> and <code>code</code> with a <a href="/docs/x">link</a>. Second line.</p>')
    expect(markup).toContain('<ul><li>one</li><li>two</li></ul>')
    expect(markup).toContain('<ol><li>first</li><li>second</li></ol>')
  })

  it.each([
    ['raw HTML', '<b>hi</b>'],
    ['a script tag', 'text <script>alert(1)</script>'],
    ['a comment', '<!-- x -->'],
    ['an image', '![logo](/img.png)'],
    ['a link to another host', 'see [here](https://example.com)'],
    ['a scheme-relative link', '[x](//evil.example/a)'],
    ['a javascript link', '[x](javascript:alert(1))'],
    ['a link with a space', '[x](/a b)'],
    ['a backslash link', '[x](/\\evil)'],
  ])('%s is refused with DBD-VIEW-002 and nothing of the text is drawn', (_name, value) => {
    const markup = html(<TextBlock widget={widget(`before\n${value}\nafter`)} />)
    expect(markup).toContain('role="alert"')
    expect(markup).toContain('DBD-VIEW-002')
    expect(markup).not.toContain('before')
    expect(markup).not.toContain('<a ')
    expect(markup).not.toContain('<img')
    expect(markup).not.toContain('<script')
  })

  it('is the refusal of the loader for the same text, so what the loader accepts is drawn', () => {
    for (const ok of ['plain', 'a < b and b > c', '1 < 2', '[a](/x)', 'x ![not an image'])
      expect(unsafeTextReasons(ok), ok).toEqual([])
  })

  it('substitutes {{params.name}} as text: a value is never markup, a link or part of a link target', () => {
    const w = widget('Region **{{params.region}}** and [more]({{params.where}}) {{params.none}}!')
    expect(unsafeTextReasons(w.text).length).toBeGreaterThan(0) // `{{params.where}}` is not a path on the host: refused as written
    const ok = widget('Region **{{params.region}}**, [more](/docs/more) {{params.none}}!')
    const markup = html(<TextBlock widget={ok} parameters={{ region: '<b>x</b> [evil](http://evil.example)' }} />)
    expect(markup).toContain('<strong>&lt;b&gt;x&lt;/b&gt; [evil](http://evil.example)</strong>')
    expect(markup).not.toContain('evil.example"')
    expect(markup).toContain('<a href="/docs/more">more</a> !')
  })

  it('reads a parameter as text: a list joined, nothing as empty', () => {
    expect(parameterText(['West', 'East'])).toBe('West, East')
    expect(parameterText(null)).toBe('')
    expect(parameterText(3)).toBe('3')
    expect(substituteParameters('{{ params.a }}/{{params.b}}', { a: true, b: [] })).toBe('true/')
    expect(parseBlocks('a\nb\n\n# h\n- x')).toEqual([{ kind: 'paragraph', text: 'a b' }, { kind: 'heading', level: 2, text: 'h' }, { kind: 'list', ordered: false, items: ['x'] }])
  })

  it('snapshot', async () => {
    const note = dashboard.widgets.note as TextDoc
    await expect(cases([
      ['the sample note, a region chosen', html(<TextBlock widget={note} parameters={{ region: ['West', 'East'] }} />)],
      ['the sample report intro, none chosen', html(<TextBlock widget={report.widgets.intro as TextDoc} parameters={{ region: [] }} />)],
      ['a refused text', html(<TextBlock widget={widget('<img src=x onerror=alert(1)>')} />)],
    ])).toMatchFileSnapshot('./__snapshots__/text.html')
  })
})

describe('ImageBlock: a raster by hash, never a URL', () => {
  const widget = { type: 'image', hash: `sha256:${'ab'.repeat(32)}`, media_type: 'image/png', alt: 'Company logo', fit: 'contain' } as const
  const png = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=='

  it('draws the data URI of its raster with its alt text', () => {
    const markup = html(<ImageBlock widget={widget} result={{ type: 'image', src: png }} />)
    expect(markup).toContain(`src="${png}"`)
    expect(markup).toContain('alt="Company logo"')
  })

  it.each([
    ['a remote address', 'https://example.com/logo.png'],
    ['a scheme-relative address', '//example.com/logo.png'],
    ['a javascript link', 'javascript:alert(1)'],
    ['a blob', 'blob:https://x/1'],
    ['an SVG data URI', 'data:image/svg+xml;base64,PHN2Zz48L3N2Zz4='],
    ['another raster type than the widget says', png.replace('image/png', 'image/jpeg')],
    ['a data URI that is not base64', 'data:image/png,abc'],
    ['a data URI with markup in it', 'data:image/png;base64,AAAA" onerror="alert(1)'],
  ])('%s is an error tile and no image', (_name, src) => {
    const markup = html(<ImageBlock widget={widget} result={{ type: 'image', src }} />)
    expect(markup).toContain('DBD-VIEW-003')
    expect(markup).not.toContain('<img')
  })

  it('knows a safe src by its type and its size', () => {
    expect(isSafeImageSrc(png, 'image/png')).toBe(true)
    expect(isSafeImageSrc(png, 'image/webp')).toBe(false)
    expect(isSafeImageSrc(`data:image/png;base64,${'A'.repeat(Math.ceil((IMAGE_MAX_BYTES * 4) / 3) + 100)}`, 'image/png')).toBe(false)
    expect(isSafeImageSrc(png, 'image/svg+xml')).toBe(false)
  })

  it('caps the bytes at publish: a raster by its first bytes, at most 1 MiB, never an SVG', () => {
    const bytes = (...v: number[]) => new Uint8Array([...v, ...Array.from({ length: 20 }, () => 0)])
    expect(checkImageBytes(bytes(0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a), 'image/png')).toEqual({ ok: true })
    expect(checkImageBytes(bytes(0xff, 0xd8, 0xff, 0xe0), 'image/jpeg')).toEqual({ ok: true })
    const webp = new Uint8Array([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 0])
    expect(checkImageBytes(webp, 'image/webp')).toEqual({ ok: true })
    expect(checkImageBytes(new TextEncoder().encode('<svg xmlns="http://www.w3.org/2000/svg"/>'), 'image/png')).toMatchObject({ ok: false, code: 'DBD-VIEW-003' })
    expect(checkImageBytes(new TextEncoder().encode('<svg/>'), 'image/svg+xml')).toMatchObject({ ok: false, code: 'DBD-VIEW-003' })
    const big = new Uint8Array(IMAGE_MAX_BYTES + 1)
    big.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])
    expect(checkImageBytes(big, 'image/png')).toMatchObject({ ok: false, message: expect.stringContaining(String(IMAGE_MAX_BYTES + 1)) })
    expect(checkImageBytes(big.subarray(0, IMAGE_MAX_BYTES), 'image/png')).toEqual({ ok: true })
  })

  it('snapshot', async () => {
    await expect(cases([
      ['drawn', html(<ImageBlock widget={widget} result={{ type: 'image', src: png }} />)],
      ['cover', html(<ImageBlock widget={{ ...widget, fit: 'cover' }} result={{ type: 'image', src: png }} />)],
      ['refused', html(<ImageBlock widget={widget} result={{ type: 'image', src: 'https://example.com/logo.png' }} />)],
    ])).toMatchFileSnapshot('./__snapshots__/image.html')
  })
})

describe('parameter controls', () => {
  const control = (value: Partial<ControlDoc>): ControlDoc => ({ type: 'control', parameter: 'p', ...value })
  const region = dashboard.parameters![0]!
  const options: RenderedControl = { type: 'control', values: [{ value: 'West' }, { value: 'East', label: 'The East' }] }

  it('picks the kind a control has: its own, else what its parameter suggests', () => {
    expect(controlKind(control({ control: 'date' }))).toBe('date')
    expect(controlKind(control({}), region)).toBe('multiselect')
    expect(controlKind(control({}), { name: 'n', type: 'integer' })).toBe('number')
    expect(controlKind(control({}), { name: 'b', type: 'boolean' })).toBe('toggle')
    expect(controlKind(control({}), { name: 's', type: 'string', for: 'customers.region' })).toBe('select')
    expect(controlKind(control({}))).toBe('text')
  })

  it('every control has a label tied to it (or a labelled group)', () => {
    const widgets = (['select', 'multiselect', 'date', 'daterange', 'number', 'text', 'toggle'] as const).map((kind) => control({ control: kind, label: `Label of ${kind}` }))
    for (const widget of widgets) {
      const markup = html(<ControlWidget id="c" widget={widget} result={options} />)
      expect(markup, widget.control).toMatch(new RegExp(`<label class="vz-control-label" for="vz-control-c">Label of ${widget.control}</label>|role="group" aria-label="Label of ${widget.control}"`))
    }
  })

  it('a select lists the values of its result, "All" first, the chosen one selected, a label where the list has one', () => {
    const markup = html(<ControlWidget id="r" widget={control({ control: 'select', parameter: 'region', label: 'Region' })} result={options} value="East" />)
    expect(markup).toContain('<option value="">All</option>')
    expect(markup).toContain('<option value="West">West</option>')
    expect(markup).toContain('<option value="East" selected="">The East</option>')
  })

  it('a multiselect checks the chosen values; a daterange has two bounded dates; a number, a text and a toggle hold their value', () => {
    expect(html(<ControlWidget id="m" widget={control({ control: 'multiselect' })} result={options} value={['West']} />)).toMatch(/<input type="checkbox" checked=""[^>]*\/> West/)
    const range = html(<ControlWidget id="d" widget={control({ control: 'daterange', label: 'Period' })} value={['2026-01-01', '2026-02-01']} />)
    expect(range).toContain('type="date" max="2026-02-01" value="2026-01-01"')
    expect(range).toContain('type="date" min="2026-01-01" value="2026-02-01"')
    expect(html(<ControlWidget id="n" widget={control({ control: 'number' })} value={5} />)).toContain('type="number" value="5"')
    expect(html(<ControlWidget id="t" widget={control({ control: 'text' })} value="abc" />)).toContain('type="text" value="abc"')
    expect(html(<ControlWidget id="b" widget={control({ control: 'toggle' })} value />)).toMatch(/role="switch" checked=""/)
  })

  it('reports a change to the host with the parameter name and the new value, and a form submits nothing', () => {
    const changes: Array<[string, unknown]> = []
    const element = ControlWidget({ id: 'r', widget: control({ control: 'select', parameter: 'region' }), result: options, value: 'West', onChange: (n, v) => changes.push([n, v]) })
    const select = findFirst(element, 'select') as { props: { onChange: (e: unknown) => void } }
    select.props.onChange({ target: { value: 'East' } })
    select.props.onChange({ target: { value: '' } })
    expect(changes).toEqual([['region', 'East'], ['region', null]])
    const multi = findFirst(ControlWidget({ id: 'm', widget: control({ control: 'multiselect', parameter: 'region' }), result: options, value: ['West'], onChange: (n, v) => changes.push([n, v]) }), 'input') as { props: { onChange: (e: unknown) => void } }
    multi.props.onChange({ target: { checked: false } })
    expect(changes[2]).toEqual(['region', []])
    expect(html(<ParameterControls controls={[]} values={{}} />)).toBe('<form class="vz-controls" aria-label="Parameters"></form>')
  })

  it('snapshot', async () => {
    const make = (kind: ControlDoc['control'], value: unknown, label: string, extra: Partial<ControlDoc> = {}) =>
      html(<ControlWidget id={kind!} widget={control({ control: kind, label, ...extra })} result={options} value={value as never} />)
    await expect(cases([
      ['the sample region control', html(<ControlWidget id="regions" widget={dashboard.widgets.regions as ControlDoc} definition={region} result={dash.regions as never} value={['West', 'South']} />)],
      ['select', make('select', 'East', 'Region')], ['multiselect', make('multiselect', ['West'], 'Regions')], ['date', make('date', '2026-03-01', 'Day')],
      ['daterange', make('daterange', ['2026-01-01', '2026-02-01'], 'Period')], ['number', make('number', 5, 'Top')], ['text', make('text', 'abc', 'Search')], ['toggle', make('toggle', true, 'Only open')],
    ])).toMatchFileSnapshot('./__snapshots__/controls.html')
  })
})

/** The first element of a type in what a component function returned (for calling a handler without a DOM). */
function findFirst(node: unknown, type: string): unknown {
  if (node === null || typeof node !== 'object') return undefined
  const element = node as { type?: unknown; props?: { children?: unknown } }
  if (element.type === type) return node
  const children = element.props?.children
  for (const child of Array.isArray(children) ? children.flat(Infinity) : [children]) {
    const found = findFirst(child, type)
    if (found) return found
  }
  return undefined
}

describe('the three states', () => {
  it('say so in words and to a screen reader', () => {
    expect(html(<LoadingTile what="Net sales" />)).toBe('<div class="vz-state vz-loading" role="status" aria-busy="true" aria-live="polite" data-testid="vz-loading">Loading Net sales…</div>')
    expect(html(<EmptyTile />)).toContain('role="note"')
    const error = html(<ErrorTile code="denied" message="You may not read this model." path="widgets.net" suggestions={['retail_sales']} />)
    expect(error).toContain('role="alert"')
    expect(text(error)).toBe('denied widgets.net : You may not read this model. Did you mean retail_sales?')
  })

  it('a widget waiting for its rows is loading; with none and not loading, empty; text and a control need no result', () => {
    const w = dashboard.widgets
    expect(html(<WidgetView id="net" widget={w.net!} loading />)).toContain('vz-loading')
    expect(html(<WidgetView id="net" widget={w.net!} loading />)).toContain('aria-busy="true"')
    expect(html(<WidgetView id="net" widget={w.net!} />)).toContain('Nothing to show yet.')
    expect(html(<WidgetView id="note" widget={w.note!} loading />)).toContain('Showing')
    expect(html(<WidgetView id="regions" widget={w.regions!} loading />)).toContain('vz-control')
  })

  it('a result with nothing in it is "No data.", for each type that can be empty', () => {
    const w = dashboard.widgets
    const empty: Array<[string, RenderedWidget]> = [
      ['net', { type: 'metric', value: null }], ['top_products', { type: 'table', columns: [], rows: [] }], ['by_region', { id: 'by_region', type: 'chart', vega_lite: { data: { values: [] } } }],
    ]
    for (const [id, result] of empty) expect(html(<WidgetView id={id} widget={w[id]!} result={result} />), id).toContain('No data.')
    const matrix: WidgetDoc = { type: 'matrix', encodings: { values: [] }, query: {} }
    expect(html(<WidgetView id="m" widget={matrix} result={{ id: 'm', type: 'matrix', columns: [], rows: [] }} />)).toContain('No data.')
  })

  it('an error entry draws as the server\'s words in its tile; the other tiles are not touched', () => {
    const w = dashboard.widgets
    const failed = html(<WidgetView id="by_region" widget={w.by_region!} result={{ type: 'error', code: 'unknown_field', message: '`customers.regoin` is not in the model.', suggestions: ['customers.region'] }} />)
    expect(failed).toContain('role="alert"')
    expect(text(failed)).toContain('unknown_field : `customers.regoin` is not in the model. Did you mean customers.region?')
    expect(html(<WidgetView id="net" widget={w.net!} result={dash.net} />)).toContain('$3.30M')
  })

  it('a result of another type than the widget is refused in words', () => {
    expect(html(<WidgetView id="net" widget={dashboard.widgets.net!} result={dash.top_products} />)).toContain('result_type_mismatch')
  })
})

describe('WidgetView', () => {
  it('draws every widget of both sample documents from its recorded result, without an error, in light and dark', () => {
    for (const [doc, results] of [[dashboard, dash], [report, rep]] as const)
      for (const [id, widget] of Object.entries(doc.widgets))
        for (const theme of ['light', 'dark'] as const) {
          const markup = html(<WidgetView id={id} widget={widget} result={results[id] ?? (widget.type === 'text' ? { type: 'text' } : undefined)} theme={theme} parameters={{ region: [] }} box={{ width: 600, height: 400 }} />)
          expect(markup, `${id} ${theme}`).not.toContain('role="alert"')
          expect(markup).toContain(`data-vz-theme="${theme}"`)
          expect(markup).toContain(`data-testid="vz-widget-${id}"`)
        }
  })

  it('titles a tile (a metric carries its label instead), names the section, and offers to maximize what can be cut', () => {
    const w = dashboard.widgets
    const table = html(<WidgetView id="top_products" widget={w.top_products!} result={dash.top_products} box={{ width: 500, height: 300 }} onMaximize={() => {}} />)
    expect(table).toContain('aria-labelledby="vz-title-top_products"')
    expect(table).toContain('<h2 id="vz-title-top_products">Best-selling products</h2>')
    expect(table).toContain('aria-label="Maximize Best-selling products"')
    expect(table).toContain('aria-pressed="false"')
    const open = html(<WidgetView id="top_products" widget={w.top_products!} result={dash.top_products} box={{ width: 500, height: 300 }} onMaximize={() => {}} maximized />)
    expect(open).toContain('aria-label="Close the full view of Best-selling products"')
    expect(open).toContain('vz-is-maximized')
    expect(html(<WidgetView id="net" widget={w.net!} result={dash.net} box={{ width: 300, height: 150 }} onMaximize={() => {}} />)).not.toContain('<h2')
    expect(html(<WidgetView id="by_region" widget={w.by_region!} result={dash.by_region} options={{ show_title: false }} onMaximize={() => {}} />)).not.toContain('<h2')
  })

  it('chooses its density from the box, and the layout\'s option pins it', () => {
    const w = dashboard.widgets
    const at = (box: { width: number; height: number }, options?: { density?: 'compact' | 'comfortable' }) =>
      html(<WidgetView id="net" widget={w.net!} result={dash.net} box={box} options={options} />).match(/data-density="(\w+)"/)![1]
    expect(at({ width: 400, height: 200 })).toBe('l')
    expect(at({ width: 170, height: 90 })).toBe('xs')
    expect(at({ width: 400, height: 200 }, { density: 'compact' })).toBe('s')
  })

  it('snapshot of each type from the sample results', async () => {
    const view = (id: string, doc: ViewsDoc, results: Record<string, RenderedWidget>, box: { width: number; height: number }, parameters = {}) =>
      html(<WidgetView id={id} widget={doc.widgets[id]!} result={results[id] ?? { type: 'text' }} box={box} theme="light" parameters={parameters} onMaximize={() => {}} definitions={doc.parameters} />)
    await expect(cases([
      ['metric', view('net', dashboard, dash, { width: 341, height: 192 })],
      ['metric, narrow', view('margin', dashboard, dash, { width: 170, height: 90 })],
      ['text', view('note', dashboard, dash, { width: 683, height: 192 }, { region: ['West'] })],
      ['control', view('regions', dashboard, dash, { width: 1366, height: 96 }, { region: ['West'] })],
      ['table, cut', view('top_products', dashboard, dash, { width: 569, height: 480 })],
      ['chart (host)', view('by_region', dashboard, dash, { width: 797, height: 480 })],
      ['error', html(<WidgetView id="by_region" widget={dashboard.widgets.by_region!} result={{ type: 'error', code: 'no_recorded_result', message: 'No result.' }} box={{ width: 400, height: 300 }} />)],
      ['loading', html(<WidgetView id="top_products" widget={dashboard.widgets.top_products!} loading box={{ width: 400, height: 300 }} />)],
    ])).toMatchFileSnapshot('./__snapshots__/widget-view.html')
  })
})

describe('charts drawn at a box (SVG)', () => {
  const chart = dash.by_region as RenderedChart
  const box = { width: 600, height: 360 }
  const svgOf = (density: DensityClass, theme: 'light' | 'dark' = 'light', size = box, legend?: 'show' | 'hide') => chartToSvg(fitChartSpec(chart.vega_lite, size, density, legend), theme)
  const attr = (svg: string, name: string) => Number(new RegExp(`^<svg[^>]* ${name}="([\\d.]+)"`).exec(svg)![1])
  const count = (svg: string, needle: string) => svg.split(needle).length - 1

  it('draws an SVG of exactly the size of the box, whatever size the spec asked for', async () => {
    for (const size of [{ width: 600, height: 360 }, { width: 300, height: 200 }, { width: 797, height: 480 }]) {
      const svg = await svgOf('l', 'light', size)
      expect(attr(svg, 'width'), JSON.stringify(size)).toBe(size.width)
      expect(attr(svg, 'height'), JSON.stringify(size)).toBe(size.height)
    }
    const spec = { ...chart.vega_lite, width: 3000, height: 3000 }
    const svg = await chartToSvg(fitChartSpec(spec, box, 'l'))
    expect([attr(svg, 'width'), attr(svg, 'height')]).toEqual([600, 360])
  })

  it('drops the legend at m, the axis titles at s, and thins the ticks at xs', async () => {
    const [l, m, s, xs] = await Promise.all((['l', 'm', 's', 'xs'] as const).map((d) => svgOf(d)))
    const legend = 'role-legend'
    const title = 'role-axis-title'
    expect(l).toContain(legend)
    expect(l).toContain('>Apparel<')
    expect(l).toContain(title)
    expect(m).not.toContain(legend)
    expect(m).not.toContain('>Footwear<')
    expect(m).toContain(title)
    expect(m).toContain('>customers_region<')
    expect(s).not.toContain(legend)
    expect(s).not.toContain(title)
    expect(count(xs, 'role-axis-label') + count(xs, '<text')).toBeLessThan(count(s, 'role-axis-label') + count(s, '<text'))
  })

  it('a layout can pin the legend either way', async () => {
    expect(await svgOf('xs', 'light', { width: 600, height: 360 }, 'show')).toContain('role-legend')
    expect(await svgOf('l', 'light', box, 'hide')).not.toContain('role-legend')
  })

  it('loads nothing: no address in the drawing', async () => {
    for (const d of ['l', 'xs'] as const) expect(await svgOf(d)).not.toMatch(/(?:href|src)="https?:/)
  })

  it('a chart with a facet gets its share of the box, not `fit` (Vega-Lite refuses it)', async () => {
    const faceted = structuredClone(chart.vega_lite) as Record<string, any>
    faceted.encoding.column = { field: 'customers_region', type: 'nominal' }
    faceted.encoding.x = { field: 'products_category', type: 'nominal' }
    const spec = fitChartSpec(faceted, { width: 880, height: 360 }, 'l') as Record<string, any>
    expect(spec.autosize).toBeUndefined()
    expect(spec.width).toBe(Math.floor((880 - 80) / 4))
    expect(await chartToSvg(spec)).toMatch(/^<svg /)
  })

  it('does not change the spec it is given', () => {
    const before = JSON.stringify(chart.vega_lite)
    fitChartSpec(chart.vega_lite, box, 'xs')
    expect(JSON.stringify(chart.vega_lite)).toBe(before)
  })

  it('snapshots, light and dark, by density', async () => {
    await expect(await svgOf('l', 'light', { width: 797, height: 480 })).toMatchFileSnapshot('./__snapshots__/chart-fit-l-light.svg')
    await expect(await svgOf('l', 'dark', { width: 797, height: 480 })).toMatchFileSnapshot('./__snapshots__/chart-fit-l-dark.svg')
    await expect(await svgOf('m', 'light', { width: 450, height: 280 })).toMatchFileSnapshot('./__snapshots__/chart-fit-m-light.svg')
    await expect(await svgOf('s', 'light', { width: 340, height: 220 })).toMatchFileSnapshot('./__snapshots__/chart-fit-s-light.svg')
    await expect(await svgOf('xs', 'light', { width: 280, height: 180 })).toMatchFileSnapshot('./__snapshots__/chart-fit-xs-light.svg')
    await expect(await chartToSvg(fitChartSpec((rep.by_month as RenderedChart).vega_lite, { width: 720, height: 260 }, 'l'), 'light')).toMatchFileSnapshot('./__snapshots__/chart-fit-line-light.svg')
  })

  it('the host the browser fills is the box, as the component draws it', () => {
    const markup = html(<WidgetView id="by_region" widget={dashboard.widgets.by_region!} result={chart} box={{ width: 797, height: 480 }} theme="light" />)
    expect(markup).toContain('data-testid="vz-chart-by_region" style="width:779px;height:434px"')
    expect(markup).toContain('role="img" aria-label="Chart by_region"')
  })
})
