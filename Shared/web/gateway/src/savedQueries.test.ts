import { describe, expect, it } from 'vitest'
import type { SavedQueryRow } from './runTypes'
import { granteeLine, merged, openLink, ownerLabel, parseGrantees, promotedEntry, queryText, saveBody } from './savedQueries'

const row = (over: Partial<SavedQueryRow> = {}): SavedQueryRow => ({ model: 'shop', name: 'q', owner: 'ada@example.org', owner_name: 'Ada', own: true, description: null, sql: 'SELECT 1', request: null, parameters: {}, ...over })

describe('saved queries words', () => {
  it('shows the owner by email with the name beside it, once, and only the email when there is no other name', () => {
    expect(ownerLabel(row())).toBe('ada@example.org (Ada)')
    expect(ownerLabel(row({ owner_name: null }))).toBe('ada@example.org')
    expect(ownerLabel(row({ owner: 'ada', owner_name: 'Ada' }))).toBe('ada')
  })
  it('reads the sharing box: emails as users, groups, everyone, once each, and names what it cannot read', () => {
    expect(parseGrantees('Cy@Example.org\nuser:Bob@x.org, group:sales\n*\ncy@example.org')).toEqual({ grantees: ['user:cy@example.org', 'user:bob@x.org', 'group:sales', '*'], problems: [] })
    expect(parseGrantees('everyone').problems).toEqual(["'everyone' is not user:<email>, group:<name> or *."])
    expect(granteeLine('user:cy@example.org')).toBe('cy@example.org')
    expect(granteeLine('*')).toBe('everyone who can query the model')
  })
  it('saves JSON as a request and anything else as semantic SQL, with the version it read', () => {
    expect(saveBody('SELECT 1;', ' what ', 2)).toEqual({ body: { description: 'what', expected_version: 2, sql: 'SELECT 1' } })
    expect(saveBody('{"kind":"query"}', '')).toEqual({ body: { description: null, request: { kind: 'query' } } })
    expect(saveBody('{bad', '')).toHaveProperty('problem')
    expect(queryText(row({ sql: null, request: { kind: 'query' } }))).toContain('"kind": "query"')
  })
  it('opens a shared query by its owner and an own one by name', () => {
    expect(openLink(row())).toBe('/models/shop/explore?saved=q')
    expect(openLink(row({ own: false }))).toBe('/models/shop/explore?saved=q&owner=ada%40example.org')
  })
  it('lists a person\'s own first and each query once', () => {
    const rows = merged([[row({ name: 'b', own: false, owner: 'cy@x.org' })], [row({ name: 'a' }), row({ name: 'b', own: false, owner: 'cy@x.org' })]])
    expect(rows.map((r) => `${r.name}:${r.own}`)).toEqual(['a:true', 'b:false'])
  })
  it("writes a saved query as the entry of the model's queries: SQL as a block, a request and the parameters as flow mappings", () => {
    // the same text the Gateway's PromotedEntryTests loads as a catalog and runs
    expect(promotedEntry(row({ name: 'revenue_by_region', description: 'Net revenue by region', sql: 'SELECT customers.region, MEASURE(orders.net_revenue)\nFROM sales_catalog\nWHERE date.fiscal_quarter = :quarter GROUP BY ALL LIMIT 100;', parameters: { quarter: { for: 'date.fiscal_quarter', type: 'string', default: 'FY27-Q1' } } }))).toBe(
      'queries:\n  revenue_by_region:\n    description: "Net revenue by region"\n    sql: |-\n      SELECT customers.region, MEASURE(orders.net_revenue)\n      FROM sales_catalog\n      WHERE date.fiscal_quarter = :quarter GROUP BY ALL LIMIT 100\n    parameters: {"quarter":{"for":"date.fiscal_quarter","type":"string","default":"FY27-Q1"}}\n')
    expect(promotedEntry(row({ name: 'r', sql: null, request: { kind: 'query' } }))).toBe('queries:\n  r:\n    request: {"kind":"query"}\n')
    // the text in the box (an edit not saved yet) is what is promoted
    expect(promotedEntry(row({ name: 'r' }), 'SELECT 2')).toContain('      SELECT 2')
  })
})
