import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { retailCatalog, type EditCommand, type ViewsDoc, type WidgetDoc } from '@dbdatakit/visuals'
import { GroupEditor } from './GroupEditor'
import { WidgetEditor } from './WidgetEditor'

/** The entry controls and the group editor as markup (plan 32 DBR54): what the screen offers for the entries of a table and a matrix and for the document's groups. The behaviour of each change is `entryParts.test.ts` in the visuals. */
const catalog = retailCatalog
const doc = (widgets: Record<string, unknown>, extra: Record<string, unknown> = {}) => ({ schemaVersion: 1, kind: 'dashboard', title: 'T', widgets, layouts: [], ...extra }) as unknown as ViewsDoc
const editor = (id: string, d: ViewsDoc) => renderToStaticMarkup(
  <WidgetEditor id={id} widget={d.widgets[id] as WidgetDoc} doc={d} catalog={catalog} catalogNote={null} apply={(_c: EditCommand) => {}} diagnostics={[]} server={[]} preview={async () => ({ type: 'text' }) as never} />,
)

describe('the entry controls', () => {
  it('a measure entry offers its alias, a Repeat button and its filters; an alias that is not a name is said', () => {
    const d = doc({ t: { type: 'table', encodings: { columns: [{ field: 'sales.net_amount', alias: 'Open Revenue', filters: [{ attribute: 'customers.region', operator: 'equals', values: ['East'] }] }] }, query: { model: 'retail_sales' } } })
    const out = editor('t', d)
    expect(out).toContain('data-testid="ed-entry-t-columns-0-alias"')
    expect(out).toContain('data-testid="ed-entry-t-columns-0-repeat"')
    expect(out).toContain('data-testid="ed-entry-t-columns-0-filter-0"')
    expect(out).toContain('data-testid="ed-entry-t-columns-0-alias-problem"')
    expect(out).toContain('snake_case')
  })

  it('an attribute entry of a matrix offers its display, many policy, link, and the icon map when it shows icons', () => {
    const d = doc({ m: { type: 'matrix', encodings: { rows: ['customers.region'], columns: ['date.year_month'], values: [{ field: 'sales.net_amount' }, { attribute: 'products.category', display: 'icon', icons: { Apparel: 'star' }, link: 'open-it' }] }, query: { model: 'retail_sales' } } }, { links: { 'open-it': { form: 'orders', edit: { id: '$orders.id' } } } })
    const out = editor('m', d)
    for (const id of ['display', 'many', 'link', 'icons', 'icon-Apparel', 'icon-add']) expect(out, id).toContain(`data-testid="ed-entry-m-values-1-${id}"`)
    expect(out).toMatch(/<option value="open-it"[^>]*>open-it<\/option>/)
    // a measure beside it has no display, and the many policy is not on a table column
    expect(out).not.toContain('data-testid="ed-entry-m-values-0-display"')
    const table = editor('t', doc({ t: { type: 'table', encodings: { columns: [{ field: 'customers.region', display: 'badge' }] }, query: { model: 'retail_sales' } } }))
    expect(table).toContain('data-testid="ed-entry-t-columns-0-display"')
    expect(table).not.toContain('data-testid="ed-entry-t-columns-0-many"')
  })

  it('a group is offered in the pick list of the wells that take an attribute, and the details of every row in the rows well', () => {
    const d = doc({ m: { type: 'matrix', encodings: { rows: ['customers.region', { group: 'detail', show: 'always' }], columns: [{ group: 'period' }], values: [{ field: 'sales.net_amount' }] }, query: { model: 'retail_sales' } },
      c: { type: 'chart', mark: 'line', encodings: { x: { group: 'period' }, y: 'sales.net_amount' }, query: { model: 'retail_sales' } } },
      { groups: { period: { fields: ['date.year_month', 'products.category'] }, detail: { fields: ['products.product', 'products.category'] } } })
    const out = editor('m', d)
    expect(out).toContain('group detail (details of every row)')
    expect(out).toContain('group period')
    expect(out).toContain('<option value="@period#always">details: period</option>')
    expect(out).toContain('<option value="@period#drill">expandable: period</option>')
    // a group is flagged by no member problem and has no options of its own
    expect(out).not.toContain('ed-entry-m-rows-1-options')
    // a chart's x takes the group but not as details (that is for the rows of a matrix)
    expect(editor('c', d)).toContain('<option value="@period">group period</option>')
    expect(editor('c', d)).not.toContain('details: ')
  })
})

describe('the group editor', () => {
  const groups = { period: { fields: ['orders.year', { field: 'orders.month', format: { date: 'month' } }], label: 'Period', template: '{2} {1}' } }
  const render = (d: ViewsDoc) => renderToStaticMarkup(<GroupEditor doc={d} catalog={catalog} apply={() => {}} />)

  it('lists each group with its levels, label and template, and a level formatted as a date', () => {
    const out = render(doc({}, { groups }))
    for (const id of ['ed-group-period', 'ed-group-field-period-0', 'ed-group-field-period-1', 'ed-group-label-period', 'ed-group-template-period', 'ed-group-format-period-1', 'ed-group-name', 'ed-group-add']) expect(out, id).toContain(`data-testid="${id}"`)
    expect(out).toContain('value="{2} {1}"')
    expect(render(doc({}))).toContain('data-testid="ed-groups-none"')
  })

  it('does not remove a group a widget names, and says which, and refuses a group of one level in the loaders\' words', () => {
    const used = render(doc({ c: { type: 'chart', mark: 'bar', encodings: { x: { group: 'period' }, y: 'a.b' }, query: { model: 'm' } } }, { groups }))
    expect(used).toMatch(/data-testid="ed-group-remove-period"[^>]*disabled/)
    expect(used).toContain('used by c')
    const bad = render(doc({}, { groups: { period: { fields: ['orders.year'] } } }))
    expect(bad).toContain('data-testid="ed-group-problems-period"')
  })
})
