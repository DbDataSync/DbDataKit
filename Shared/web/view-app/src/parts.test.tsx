import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { StoreError } from '@dbdatakit/store-ui'
import { doc, grid, rows, territory } from './fixtures'
import { DashboardBoard, DetailPage, FormFields, GridTable } from './parts'
import { lockedFields, notifyParent, openedByLink, startingValues, type DashboardSpec, type DetailSpec, type FormSpec } from './spec'
import { ViewApp, menuOf } from './screens'

const go = () => {}
const doc2 = { ...doc, files: ['views/grid.yaml', 'views/other.yaml'], views: { ...doc.views, 'views/other.yaml': { ...grid, title: 'Other' } } }

describe('the grid', () => {
  it('draws the columns under the spec labels, a link on each row, escapes values, and pages by the spec size', () => {
    const html = renderToStaticMarkup(<GridTable spec={grid} entity={territory} rows={rows.slice(0, 2)} total={4} offset={0} sort={[{ attribute: 'name', direction: 'asc' }]} filters={{}} params={{}} onPage={go} go={go} />)
    expect(html).toContain('>Territory')
    expect(html).toContain('aria-sort="ascending"')
    expect(html).toContain('href="#/views/detail.yaml?id=EU"')
    expect(html).toContain('1–2 of 4')
    expect(html).toContain('Sales region')
    expect(html).not.toMatch(/<button[^>]*disabled[^>]*>Next/)
    expect(renderToStaticMarkup(<GridTable spec={grid} entity={territory} rows={[rows[2]]} total={3} offset={2} sort={[]} filters={{}} params={{}} onPage={go} go={go} />)).toContain('Asia &lt;Pacific&gt;')
  })

  it('says when there are no rows', () => {
    expect(renderToStaticMarkup(<GridTable spec={grid} entity={territory} rows={[]} total={0} offset={0} sort={[]} filters={{}} params={{}} go={go} />)).toContain('No rows.')
  })
})

describe('the form', () => {
  const spec: FormSpec = {
    kind: 'form', title: 'New', model: 'territories', entity: 'territory', mode: 'create', key: null, submit: 'Add territory', params: [], links: [],
    fields: [{ attribute: 'code', label: null, readOnly: false, required: false, help: 'Short and unique' }, { attribute: 'quota', label: 'Quota (units)', readOnly: false, required: true, help: null }],
  }

  it('draws the listed fields only, with the spec label, help and required over the model type', () => {
    const html = renderToStaticMarkup(<FormFields spec={spec} entity={territory} row={null} text={{}} />)
    expect(html).toContain('Quota (units)')
    expect(html).toContain('type="number"')
    expect(html).toContain('Short and unique')
    expect(html).toContain('Add territory')
    expect(html).not.toContain('Europe')
    expect(html.match(/<input /g)).toHaveLength(2)
  })

  it('shows the key of an existing row but does not let it be edited, and draws the server problems against the attribute', () => {
    const html = renderToStaticMarkup(<FormFields spec={{ ...spec, mode: 'edit', key: 'id' }} entity={territory} row={rows[0]} text={{ code: 'EU', quota: 'x' }} problems={{ quota: 'A whole number.' }} />)
    expect(html).toMatch(/<input[^>]*id="sui-code"[^>]*disabled/)
    expect(html).toContain('A whole number.')
  })

  it('a conflict says to reload the row, and any other failure shows the server sentence', () => {
    const conflict = renderToStaticMarkup(<FormFields spec={spec} entity={territory} row={rows[0]} text={{}} error={new StoreError('The row changed.', 409, [], 3)} />)
    expect(conflict).toContain('The row changed. Reload the row')
    expect(renderToStaticMarkup(<FormFields spec={spec} entity={territory} row={null} text={{}} error={new Error('The Store is away.')} />)).toContain('The Store is away.')
  })
})

describe('a form a link opened in a dialog or a panel (plan 32 DBR36)', () => {
  const spec: FormSpec = {
    kind: 'form', title: 'New order', model: 'territories', entity: 'territory', mode: 'create', key: null, submit: null, params: [], links: [],
    fields: [{ attribute: 'code', label: null, readOnly: false, required: false, help: null }, { attribute: 'quota', label: null, readOnly: false, required: true, help: null }],
  }

  it('starts a form that creates a row from the query (the `new` of a link), listed fields only, and leaves an edit form to its row', () => {
    expect(startingValues(spec, { code: 'EU', quota: '5', other: 'x', _lock: 'code' })).toEqual({ code: 'EU', quota: '5' })
    expect(startingValues(spec, { code: '' })).toEqual({})
    expect(startingValues({ ...spec, mode: 'edit', key: 'code' }, { code: 'EU' })).toEqual({})
    expect(startingValues(spec, {})).toEqual({})
  })

  it('locks the listed fields the query names and no other: they are read-only on screen, and still the form to save', () => {
    expect([...lockedFields(spec, { _lock: 'code, nothing,quota' })]).toEqual(['code', 'quota'])
    expect([...lockedFields(spec, {})]).toEqual([])
    const html = renderToStaticMarkup(<FormFields spec={spec} entity={territory} row={null} text={{ code: 'EU', quota: '5' }} locked={new Set(['code'])} />)
    expect(html).toMatch(/<input[^>]*id="sui-code"[^>]*disabled/)
    expect(html).not.toMatch(/<input[^>]*id="sui-quota"[^>]*disabled/)
  })

  it('has a Cancel only when it was opened as a dialog or a panel', () => {
    expect(openedByLink({ _as: 'dialog' })).toBe(true)
    expect(openedByLink({ _as: 'panel' })).toBe(true)
    expect(openedByLink({ _as: 'navigate' })).toBe(false)
    expect(openedByLink({})).toBe(false)
    expect(renderToStaticMarkup(<FormFields spec={spec} entity={territory} row={null} text={{}} onCancel={go} />)).toContain('data-testid="va-form-cancel"')
    expect(renderToStaticMarkup(<FormFields spec={spec} entity={territory} row={null} text={{}} />)).not.toContain('va-form-cancel')
  })

  it('tells the page above it, with exactly the two messages the surface reads, and nothing when there is no page above', () => {
    const sent: unknown[] = []
    const above = { postMessage: (m: unknown, target: string) => sent.push([m, target]) }
    notifyParent({ type: 'views:saved' }, { parent: above })
    notifyParent({ type: 'views:close' }, { parent: above })
    expect(sent).toEqual([[{ type: 'views:saved' }, '*'], [{ type: 'views:close' }, '*']])
    const top: { parent?: unknown; postMessage(m: unknown, t: string): void } = { postMessage: (m, t) => sent.push([m, t]) }
    top.parent = top
    notifyParent({ type: 'views:saved' }, top as never)
    expect(sent).toHaveLength(2)   // a top-level document speaks to nobody
    notifyParent({ type: 'views:saved' }, undefined)
  })
})

describe('the detail page and the dashboard', () => {
  it('a detail page shows its sections', () => {
    const spec: DetailSpec = { kind: 'detail', title: 'T', model: 'territories', entity: 'territory', key: 'id', params: [{ name: 'id', type: 'string', required: true, default: null }], links: [], sections: [{ title: 'Main', attributes: [{ name: 'name', label: null }, { name: 'quota', label: 'Quota' }] }] }
    const html = renderToStaticMarkup(<DetailPage spec={spec} entity={territory} row={rows[1]} params={{ id: 'NA' }} go={go} />)
    expect(html).toContain('<h3>Main</h3>')
    expect(html).toContain('North America')
    expect(html).toContain('>100<')
  })

  it('a detail page draws every link it has (there are no rows in it), a grid only its row links in the rows', () => {
    const spec: DetailSpec = {
      kind: 'detail', title: 'T', model: 'territories', entity: 'territory', key: 'id', params: [{ name: 'id', type: 'string', required: true, default: null }], sections: [],
      links: [{ label: 'Edit', to: 'views/edit.yaml', row: false, with: { code: { kind: 'param', text: 'id' } } }, { label: 'Same row', to: 'views/edit.yaml', row: false, with: { code: { kind: 'attribute', text: 'code' } } }],
    }
    const html = renderToStaticMarkup(<DetailPage spec={spec} entity={territory} row={rows[1]} params={{ id: 'NA' }} go={go} />)
    expect(html).toContain('href="#/views/edit.yaml?code=NA">Edit<')
    expect(html).toContain('>Same row<')
    const gridHtml = renderToStaticMarkup(<GridTable spec={grid} entity={territory} rows={rows.slice(0, 1)} total={1} offset={0} sort={[]} filters={{}} params={{}} go={go} />)
    expect(gridHtml.match(/>Open</g)).toHaveLength(1)    // in the row
    expect(gridHtml).not.toContain('>New<')                // the page's link is drawn by the app, under the grid
  })

  it('a dashboard draws a figure, a table, and a chart through the shared renderer', () => {
    const spec: DashboardSpec = {
      kind: 'dashboard', title: 'D', model: 'territories', params: [], links: [],
      widgets: [
        { kind: 'metric', title: 'Territories', entity: 'territory', aggregate: 'count', measure: null, by: null, columns: [], limit: 10 },
        { kind: 'table', title: 'Top', entity: 'territory', aggregate: 'count', measure: null, by: null, columns: [{ name: 'code', label: null }], limit: 2 },
        { kind: 'bar', title: 'By region', entity: 'territory', aggregate: 'sum', measure: { name: 'quota', label: null }, by: { name: 'region', label: null }, columns: [], limit: 10 },
      ],
    }
    const html = renderToStaticMarkup(<DashboardBoard spec={spec} rows={{ territory: rows }} truncated={['territory']} />)
    expect(html).toContain('data-testid="va-metric-0"')
    expect(html).toContain('<b>4</b>')
    expect(html).toContain('data-testid="vz-table-w1"')
    expect(html).toContain('data-testid="vz-chart-w2"')
    expect(html).toContain('Only the first rows of territory')
  })

  it('a report of an external server is framed only when the host approved its origin, and always has its link (AP17)', () => {
    const embed = (framed: boolean, url: string) => ({ kind: 'embed' as const, title: 'Sales', entity: '', aggregate: 'count' as const, measure: null, by: null, columns: [], limit: 10, url, link_text: 'Open the sales report', framed })
    const board = (...widgets: ReturnType<typeof embed>[]): DashboardSpec => ({ kind: 'dashboard', title: 'D', model: 'territories', params: [], links: [], widgets })
    const approved = renderToStaticMarkup(<DashboardBoard spec={board(embed(true, 'https://reports.contoso.com/Reports/report/Sales'))} rows={{}} />)
    expect(approved).toContain('data-mode="frame"')
    expect(approved).toContain('<iframe')
    expect(approved).toContain('src="https://reports.contoso.com/Reports/report/Sales"')
    expect(approved).toContain('>Open the sales report<')
    const unapproved = renderToStaticMarkup(<DashboardBoard spec={board(embed(false, 'https://other.contoso.com/r'))} rows={{}} />)
    expect(unapproved).toContain('data-mode="link"')
    expect(unapproved).not.toContain('<iframe')
    expect(unapproved).toContain('href="https://other.contoso.com/r"')
    const unsafe = renderToStaticMarkup(<DashboardBoard spec={board(embed(true, 'http://reports.contoso.com/r'))} rows={{}} />)
    expect(unsafe).not.toContain('<iframe')                // not https: an error tile, never a frame
  })
})

describe('the app', () => {
  const client = {} as never
  const host = { route: () => {}, onNavigate: () => () => {} }

  it('opens on the first view and offers a menu only when there is more than one', () => {
    const html = renderToStaticMarkup(<ViewApp doc={doc} client={client} host={host} />)
    expect(html).toContain('Territories')
    expect(html).not.toContain('aria-label="Views"')
    const two = { ...doc, files: ['views/grid.yaml', 'views/b.yaml'], views: { ...doc.views, 'views/b.yaml': { ...doc.views['views/grid.yaml'], title: 'Second' } } }
    expect(renderToStaticMarkup(<ViewApp doc={two} client={client} host={host} />)).toContain('aria-label="Views"')
  })

  it('shows the files that did not read, in place of any view', () => {
    const html = renderToStaticMarkup(<ViewApp doc={{ ...doc, views: {}, problems: ['views/grid.yaml:3:1: The view needs a model.'] }} client={client} host={host} />)
    expect(html).toContain('views/grid.yaml:3:1')
  })

  it('refuses a format it does not read, naming both', () => {
    const html = renderToStaticMarkup(<ViewApp doc={{ ...doc, format: 'view-spec/2' }} client={client} host={host} />)
    expect(html).toContain('view-spec/1')
    expect(html).toContain('view-spec/2')
  })
})

describe('code pages beside the spec files', () => {
  const host = { route: () => {}, onNavigate: () => () => {} }
  const client = {} as never
  const Overview = ({ params }: { params: Record<string, string> }) => <section data-testid="code-overview">{`code page, region ${params.region ?? 'all'}`}</section>
  const pages = { 'pages/overview': { title: 'Overview (code)', component: Overview } }

  it('an app can be only spec files: the menu is the specs and nothing else', () => {
    expect(menuOf(doc2, {})).toEqual([{ key: 'views/grid.yaml', title: 'Territories' }, { key: 'views/other.yaml', title: 'Other' }])
  })

  it('a code page is a menu entry after the specs, and the home is the first spec while there is one', () => {
    expect(menuOf(doc2, pages)).toEqual([{ key: 'views/grid.yaml', title: 'Territories' }, { key: 'views/other.yaml', title: 'Other' }, { key: 'pages/overview', title: 'Overview (code)' }])
    const html = renderToStaticMarkup(<ViewApp doc={doc2} client={client} host={host} pages={pages} />)
    expect(html).toContain('href="#/pages/overview"')
    expect(html).toContain('href="#/views/other.yaml"')
    expect(html).not.toContain('code-overview')   // the home is the first spec, which is loading
  })

  it('a code page with the key of a spec takes its place in the menu, and an app of one page of code and no spec shows it as the home', () => {
    expect(menuOf(doc2, { 'views/other.yaml': pages['pages/overview'] }).map((m) => m.key)).toEqual(['views/grid.yaml', 'views/other.yaml'])
    expect(menuOf(doc2, { 'views/other.yaml': pages['pages/overview'] })[1].title).toBe('Overview (code)')
    const alone = { ...doc, home: null, files: [], views: {} }
    const html = renderToStaticMarkup(<ViewApp doc={alone} client={client} host={host} pages={pages} />)
    expect(html).toContain('code page, region all')
    expect(html).toContain('<h2 class="va-title">Overview (code)</h2>')
    expect(html).not.toContain('class="va-menu"')   // one entry is not a menu
  })

  it('without specs or pages the app says it has no view', () => {
    expect(renderToStaticMarkup(<ViewApp doc={{ ...doc, home: null, files: [], views: {} }} client={client} host={host} />)).toContain('This app has no view')
  })
})
