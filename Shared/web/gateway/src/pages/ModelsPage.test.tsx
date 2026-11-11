import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { EntityModelsView, SemanticModelsView, type EntityModelsViewProps, type SemanticModelsViewProps } from './ModelsPage'
import type { SemanticModelRow } from '../types'

const sales: SemanticModelRow = { name: 'sales', current_version: 2, versions: 2, managed_by: 'build-1', served: true, served_version: 2, failed_version: null, failure: null }
const detail = { name: 'sales', current_version: 2, catalog: 'name: sales', sources: { orders: 'warehouse' }, ssas: [], versions: [{ version: 1, published_by: 'ada', published_at: '2026-11-01T10:00:00Z' }, { version: 2, published_by: 'grace', published_at: '2026-11-02T10:00:00Z' }] }

function sem(over: Partial<SemanticModelsViewProps> = {}): SemanticModelsViewProps {
  return { rows: [sales], loading: false, listError: null, opened: null, detail: undefined, detailError: null, confirming: null, busy: false, notice: null, onOpen: () => {}, onServe: () => {}, onAskRemove: () => {}, onRemove: () => {}, ...over }
}
function ent(over: Partial<EntityModelsViewProps> = {}): EntityModelsViewProps {
  return { rows: [{ name: 'customers', current_version: 3, data_source: 'erp', managed_by: null, served_version: 3, failed_version: null, failure: null, entities: ['customer', 'address'] }], loading: false, listError: null, opened: null, detail: undefined, detailError: null, confirming: null, busy: false, onOpen: () => {}, onAskRemove: () => {}, onRemove: () => {}, ...over }
}
const html = (node: ReactNode) => renderToStaticMarkup(<MemoryRouter>{node}</MemoryRouter>)

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  // a component that reads a hook (the module's Link) cannot be called as a plain function: its children are its props' children
  if (typeof element.type === 'function' && element.type.name !== 'Link') { const inner = find((element.type as (p: unknown) => ReactNode)(element.props), testId); if (inner) return inner }
  return find(element.props.children as ReactNode, testId)
}

describe('semantic models', () => {
  it('lists each model with its version, where it is served and who owns it', () => {
    const text = html(SemanticModelsView(sem()))
    expect(text).toContain('data-testid="semantic-row-sales"')
    expect(text).toContain('serving version 2')
    expect(text).toContain('published by a paired server (build-1)')
    expect(text).toContain('2 kept')
    expect(text).toContain('href="/models/sales"')
  })
  it('says when nothing is published, while loading, and when the list is refused', () => {
    expect(html(SemanticModelsView(sem({ rows: [] })))).toContain('data-testid="models-semantic-empty"')
    expect(html(SemanticModelsView(sem({ rows: undefined, loading: true })))).toContain('Loading')
    const refused = html(SemanticModelsView(sem({ rows: undefined, listError: new ApiError(403, 'This needs the admin role.', null) })))
    expect(refused).toContain('This needs the admin role.')
    expect(refused).not.toContain('models-semantic-table')
  })
  it('shows a version that did not load in red words', () => {
    const text = html(SemanticModelsView(sem({ rows: [{ ...sales, current_version: 3, failed_version: 3, failure: 'the catalog does not load' }] })))
    expect(text).toContain('version 3 did not load; still serving 2: the catalog does not load')
    expect(text).toContain('data-tone="bad"')
  })
  it('offers to serve any other version and presses it with the version', () => {
    const onServe = vi.fn()
    const view = SemanticModelsView(sem({ opened: 'sales', detail, onServe }))
    expect(html(view)).toContain('data-testid="semantic-serve-1"')
    expect(html(view)).not.toContain('data-testid="semantic-serve-2"')
    expect(html(view)).toContain('orders reads warehouse')
    ;(find(view, 'semantic-serve-1')!.props.onClick as () => void)()
    expect(onServe).toHaveBeenCalledWith('sales', 1)
  })
  it('turns the buttons off while a change is under way, and says there is no earlier version', () => {
    expect(find(SemanticModelsView(sem({ busy: true })), 'semantic-remove-sales')!.props.disabled).toBe(true)
    expect(html(SemanticModelsView(sem({ busy: true, opened: 'sales', detail })))).toContain('disabled')
    const single = html(SemanticModelsView(sem({ rows: [{ ...sales, versions: 1 }], opened: 'sales', detail: { ...detail, versions: [detail.versions[1]!] } })))
    expect(single).toContain('There is no earlier version to go back to.')
    expect(single).not.toContain('Serve this version')
  })
  it('retires only after a second click that says what it does', () => {
    const onAsk = vi.fn()
    const first = SemanticModelsView(sem({ onAskRemove: onAsk }))
    expect(html(first)).not.toContain('semantic-remove-confirm-sales')
    ;(find(first, 'semantic-remove-sales')!.props.onClick as () => void)()
    expect(onAsk).toHaveBeenCalledWith('sales')
    const second = html(SemanticModelsView(sem({ confirming: 'sales' })))
    expect(second).toContain('data-testid="semantic-remove-confirm-sales"')
    expect(second).toContain('every instance of the group')
  })
  it('shows what a change came to, and a refusal as an alert', () => {
    expect(html(SemanticModelsView(sem({ notice: { text: 'sales serves version 1 now (it was 2).', bad: false } })))).toContain('sales serves version 1 now')
    expect(html(SemanticModelsView(sem({ notice: { text: 'Version 1 does not load, so the current one still serves.', bad: true } })))).toContain('role="alert"')
  })
})

describe('entity models', () => {
  it('lists the model, its data source and entities, and only retires', () => {
    const text = html(EntityModelsView(ent({ opened: 'customers', detail: { name: 'customers', current_version: 3, data_source: 'erp', managed_by: null, versions: [{ version: 3, published_by: 'store', published_at: null }, { version: 2, published_by: 'store', published_at: null }] } })))
    expect(text).toContain('customer, address')
    expect(text).toContain('>erp<')
    expect(text).toContain('data-testid="entity-version-2"')
    expect(text).not.toContain('entity-serve-')
    expect(text).toContain('publishing it again from the Store')
  })
  it('says when none is published, and asks twice before retiring', () => {
    expect(html(EntityModelsView(ent({ rows: [] })))).toContain('data-testid="models-entity-empty"')
    expect(html(EntityModelsView(ent({ confirming: 'customers' })))).toContain('The Store still holds the model and its data')
    expect(find(EntityModelsView(ent({ busy: true })), 'entity-remove-customers')!.props.disabled).toBe(true)
  })
})
