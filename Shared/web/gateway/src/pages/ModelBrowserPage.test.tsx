import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { ModelBrowserView } from './ModelBrowserPage'
import { view } from '../modelBrowserFixture'

const html = (props: Partial<Parameters<typeof ModelBrowserView>[0]> = {}) =>
  renderToStaticMarkup(<MemoryRouter><ModelBrowserView view={view} loading={false} error={null} search="" onSearch={() => {}} {...props} /></MemoryRouter>)

describe('the model browser', () => {
  it('shows the measures, the attributes with the column each is read from, and the version', () => {
    const text = html()
    expect(text).toContain('version 3 (published)')
    expect(text).toContain('data-testid="measure-revenue"')
    expect(text).toContain('sum of amount (source shop)')
    expect(text).toContain('shop: c.region')
    expect(text).toContain('not bound to a column')
  })
  it('links a measure to its lineage node', () => {
    expect(html()).toContain('href="/lineage?find=shop_sales%3A%20revenue"')
  })
  it('filters by synonym and says when nothing matches', () => {
    const sales = html({ search: 'sales' })
    expect(sales).toContain('measure-revenue')
    expect(sales).not.toContain('measure-margin_pct')
    const none = html({ search: 'zzz' })
    expect(none).toContain('model-no-measures')
    expect(none).toContain('model-no-dimensions')
  })
  it('lists the versions only when it was given them, and names a hand-set model', () => {
    expect(html()).not.toContain('model-versions')
    const text = html({ versions: [{ version: 3, published_by: 'ada', published_at: null }, { version: 2, published_by: 'grace', published_at: null }] })
    expect(text).toContain('3 (served)')
    expect(html({ view: { ...view, version: null, published: false } })).toContain('set up in server.yaml')
  })
  it('shows the Gateway\'s refusal and no content', () => {
    const text = html({ view: undefined, error: new ApiError(404, 'There is no model \'x\'.', null) })
    expect(text).toContain('There is no model')
    expect(text).not.toContain('model-measures')
  })
})
