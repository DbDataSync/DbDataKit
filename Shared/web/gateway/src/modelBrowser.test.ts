import { describe, expect, it } from 'vitest'
import { bindingLine, filterCatalog, lineageLink, measureLine } from './modelBrowser'
import { view } from './modelBrowserFixture'

describe('model browser words', () => {
  it('links a member to its lineage node by the name Build gives it', () => {
    expect(lineageLink('shop_sales', 'revenue')).toBe('/lineage?find=shop_sales%3A%20revenue')
  })
  it('reads a measure and an attribute binding as a sentence', () => {
    expect(measureLine(view.measures[0]!)).toBe('sum of amount (source shop)')
    expect(measureLine(view.measures[1]!)).toBe('derived: divide of other measures')
    expect(bindingLine(view.dimensions[0]!.attributes[0]!)).toBe('shop: c.region')
    expect(bindingLine(view.dimensions[0]!.attributes[1]!)).toBe('not bound to a column')
  })
  it('finds by name or synonym, every word', () => {
    expect(filterCatalog(view, '').measures).toHaveLength(2)
    expect(filterCatalog(view, 'sales').measures.map((m) => m.name)).toEqual(['revenue'])
    expect(filterCatalog(view, 'area').dimensions[0]!.attributes.map((a) => a.name)).toEqual(['customer.region'])
    expect(filterCatalog(view, 'nothing here').dimensions).toEqual([])
  })
})
