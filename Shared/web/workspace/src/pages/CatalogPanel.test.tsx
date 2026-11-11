import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { readCatalogCache } from '../catalogCache'
import type { PublishTarget } from '../api'
import { catalogAdoptionSentence, refreshButton, refreshedLine, refreshLabel } from '../model'
import { CatalogView, RefreshForm } from './CatalogPanel'

const sound = [
  'schema: catalog-cache/1', 'generated_at: 2026-11-11T10:00:00Z', 'source: {server: prod-gateway, product: gateway}', 'connections:',
  '  src:', '    engine: postgres', '    tables:', '      - {schema: public, name: orders, kind: table, columns: [{name: id, type: integer, key: true}, {name: total, type: numeric}]}', '',
].join('\n')

describe('the catalog panel', () => {
  it('says there is no cache and what one is', () => {
    const html = renderToStaticMarkup(<CatalogView read={null} present={false} onOpen={() => {}} />)
    expect(html).toContain('wsp-catalog-none')
    expect(html).toContain('never a row and never a secret')
  })

  it('lists the connections and tables of a sound cache and where it came from', () => {
    const html = renderToStaticMarkup(<CatalogView read={readCatalogCache(sound)} present onOpen={() => {}} />)
    expect(html).toContain('from prod-gateway (gateway)')
    expect(html).toContain('data-connection="src"')
    expect(html).toContain('data-table="public.orders"')
    expect(html).toContain('table, 2 columns')
    expect(html).not.toContain('wsp-catalog-finding')
  })

  it('shows the strict reader\'s findings with their key path and lists no table of a damaged file', () => {
    const html = renderToStaticMarkup(<CatalogView read={readCatalogCache(sound.replace('key: true}', 'key: true, rows: []}'))} present onOpen={() => {}} />)
    expect(html).toContain('data-code="DBD-CAT-002"')
    expect(html).toContain('connections.src.tables[0].columns[0].rows')
    expect(html).not.toContain('wsp-catalog-table')
  })
})

describe('the shape preview (SC20k)', () => {
  const sound2 = sound.replace('{name: total, type: numeric}', '{name: total, type: "numeric(12,2)", nullable: false}, {name: note, type: text}, {name: placed, type: timestamp}, {name: active, type: boolean}, {name: ref, type: uuid}, {name: blob, type: geometry}')

  it('offers a preview per table and draws none until one is chosen', () => {
    const html = renderToStaticMarkup(<CatalogView read={readCatalogCache(sound2)} present onOpen={() => {}} preview={null} onPreview={() => {}} />)
    expect(html).toContain('Preview the shape')
    expect(html).not.toContain('wsp-shape-table')
  })

  it('draws the chosen table: the types as headers, generated values, one null for a nullable column, and the note that they are not rows', () => {
    const html = renderToStaticMarkup(<CatalogView read={readCatalogCache(sound2)} present onOpen={() => {}} preview="src/public.orders" onPreview={() => {}} />)
    expect(html).toContain('Hide the shape')
    expect(html).toContain('not rows of the database')
    expect(html).toContain('id (key)')
    expect(html).toContain('>1<')
    expect(html).toContain('>21.00<')
    expect(html).toContain('note_1')
    expect(html).toContain('2026-01-01 08:00:00')
    expect(html).toContain('00000000-0000-4000-8000-000000000001')
    expect(html).toContain('&lt;geometry&gt;')
    expect(html).toContain('>null<')
  })
})

describe('refreshing the catalog from a data source (plan 33 SC20j)', () => {
  const targets: PublishTarget[] = [
    { name: 'edge', gateway: 'https://gateway:8816', product: 'gateway', accepts: true, can_publish: true },
    { name: 'jobs', gateway: 'https://runner:8495', product: 'runner', accepts: true, can_publish: true },
  ]
  const draw = (props: Partial<Parameters<typeof RefreshForm>[0]> = {}) =>
    renderToStaticMarkup(<RefreshForm canWrite targets={targets} busy={false} onRefresh={() => {}} {...props} />)

  it('offers the Gateways only, and names the data source on the button', () => {
    const html = draw()
    expect(html).toContain('<option value="edge">')
    expect(html).not.toContain('<option value="jobs">')
    expect(html).toContain('Refresh catalog from a data source')
    expect(draw({ initialConnection: 'warehouse' })).toContain('Refresh catalog from warehouse')
    expect(draw({ initialConnection: 'warehouse', initialDataSource: 'dwh' })).toContain('Refresh catalog from dwh')
    expect(refreshLabel('', ' crm ')).toBe('Refresh catalog from crm')
  })

  it('holds the button until a Gateway key and a connection are named, and says why', () => {
    const ok = { canWrite: true, target: 'edge', connection: 'warehouse', busy: false, targets }
    expect(refreshButton(ok)).toEqual({ enabled: true, why: null })
    expect(refreshButton({ ...ok, connection: '' }).why).toBe('Name the connection of the project the catalog is for.')
    expect(refreshButton({ ...ok, target: 'jobs' }).why).toContain('read from a Gateway')
    expect(refreshButton({ ...ok, canWrite: false }).why).toContain('author role')
    expect(refreshButton({ ...ok, targets: [targets[1]!] }).why).toContain('No publisher key is paired with a Gateway')
    expect(refreshButton({ ...ok, busy: true }).enabled).toBe(false)
  })

  it('shows the confirmation for a data source made by hand only when the Gateway asked, with both buttons, and not while it works', () => {
    expect(draw()).not.toContain('wsp-catalog-adopt')
    const html = draw({ adoption: { data_source: 'legacy', made_by: 'hand' } })
    expect(html).toContain('data-testid="wsp-catalog-adopt"')
    expect(html).toContain('data-data-source="legacy"')
    expect(html).toContain('Confirm and read legacy')
    expect(html).toContain('wsp-catalog-adopt-cancel')
    expect(draw({ adoption: { data_source: 'legacy', made_by: 'hand' }, busy: true })).not.toContain('wsp-catalog-adopt"')
    const words = catalogAdoptionSentence({ data_source: 'legacy', made_by: 'hand' })
    expect(words).toContain('The data source legacy on the Gateway was made by hand or by an administrator')
    expect(words).toContain('never rows')
    expect(words).toContain('records that you adopted it')
  })

  it('says what was cached and that no row was read', () => {
    expect(refreshedLine({ connection: 'warehouse', data_source: 'dwh', target: 'edge', tables: 2, columns: 3, file: 'catalog-cache.yaml' }))
      .toBe('Cached 2 tables and 3 columns of warehouse from the data source dwh on edge into catalog-cache.yaml. No row was read.')
    expect(refreshedLine({ connection: 'a', data_source: 'a', target: 'g', tables: 1, columns: 1, file: 'f' })).toContain('1 table and 1 column')
  })
})
