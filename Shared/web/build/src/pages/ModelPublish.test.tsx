import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { ModelPublishView, publishModelBlock, readsOf, type ModelPublishViewProps } from './ModelPublish'

const gateway = { name: 'gateway-one', url: 'https://gw:8485', instance_id: 'gw-1', paired_at: '2026-10-30T10:00:00Z' }
const sales = { name: 'sales', file: 'semantic/sales.yml', description: 'Sales by day', sources: [{ source: 'orders', connection: 'warehouse', engine: 'postgres' }, { source: 'fx', connection: 'finance', engine: 'postgres' }, { source: 'x', connection: 'warehouse', engine: 'postgres' }] }

function props(over: Partial<ModelPublishViewProps> = {}): ModelPublishViewProps {
  return { proxies: [gateway], models: [sales], loading: false, listError: null, proxy: '', model: '', busy: false, done: null, failure: null, onProxy: () => {}, onModel: () => {}, onPublish: () => {}, ...over }
}
const html = (over?: Partial<ModelPublishViewProps>) => renderToStaticMarkup(ModelPublishView(props(over)))
function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}
const disabled = (over: Partial<ModelPublishViewProps>) => find(ModelPublishView(props(over)), 'build-publish-model-button')!.props.disabled

describe('publishing a semantic model', () => {
  it('says what goes and who may, and offers the gateways and the models', () => {
    const text = html()
    expect(text).toContain('read</strong> logins')
    expect(text).toContain('approver role')
    expect(text).toContain('>gateway-one</option>')
    expect(text).toContain('>sales</option>')
  })
  it('names the connections a model reads, once each', () => {
    expect(readsOf(sales)).toBe('finance, warehouse')
    expect(readsOf(undefined)).toBe('')
    expect(html({ model: 'sales' })).toContain('finance, warehouse')
  })
  it('keeps the button off until a gateway and a model are chosen, and says why', () => {
    expect(disabled({})).toBe(true)
    expect(html()).toContain('Choose the Gateway to publish to.')
    expect(html({ proxy: 'gateway-one' })).toContain('Choose a semantic model of this project.')
    expect(disabled({ proxy: 'gateway-one', model: 'sales' })).toBe(false)
    expect(disabled({ proxy: 'gateway-one', model: 'sales', busy: true })).toBe(true)
    expect(publishModelBlock('a', 'b', false)).toBeNull()
  })
  it('says when there is no gateway, no model, or the lists are loading or failed', () => {
    expect(html({ proxies: [] })).toContain('build-publish-model-no-gateway')
    expect(html({ models: [] })).toContain('build-publish-model-none')
    expect(html({ proxies: undefined, models: undefined, loading: true })).toContain('Loading')
    expect(html({ proxies: undefined, listError: new ApiError(500, 'The server could not list its gateways.', null) })).toContain('The server could not list its gateways.')
  })
  it('shows what the Gateway answered, and its refusal in its words', () => {
    const done = html({ done: { proxy: 'gateway-one', model: 'sales', version: 2, created: false, data_sources: ['warehouse', 'finance'] } })
    expect(done).toContain('is updated on')
    expect(done).toContain('warehouse, finance')
    expect(html({ done: { proxy: 'gateway-one', model: 'sales', version: 1, created: true, data_sources: [] } })).toContain('now served')
    expect(html({ failure: new ApiError(400, 'The proxy refused publishing the model: Data source \'warehouse\' did not answer a test query.', null) })).toContain('did not answer a test query')
    expect(html({ failure: new ApiError(403, 'This needs the approver role.', null) })).toContain('Publishing needs the approver role')
  })
})
