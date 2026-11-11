import type { SemanticCatalogView } from './types'

export const view: SemanticCatalogView = {
  name: 'shop_sales', version: 3, published: true, timezone: 'UTC',
  dimensions: [{ name: 'customer', label: 'Customer', default_role: null, attributes: [
    { name: 'customer.region', type: 'string', synonyms: ['area'], bindings: [{ source: 'shop', join: 'c', column: 'region', dax: null }], sort_key: null },
    { name: 'customer.name', type: 'string', synonyms: [], bindings: [], sort_key: null },
  ] }],
  measures: [
    { name: 'revenue', type: 'number', source: 'shop', aggregation: 'sum', column: 'amount', derived: null, format: null, synonyms: ['sales'], roles: {} },
    { name: 'margin_pct', type: 'number', source: null, aggregation: null, column: null, derived: 'divide', format: null, synonyms: [], roles: {} },
  ],
  hierarchies: [], calendars: [], segments: [], queries: [], sources: [{ name: 'shop', kind: 'postgres', fact_table: 'orders', joins: ['customers'] }],
}
