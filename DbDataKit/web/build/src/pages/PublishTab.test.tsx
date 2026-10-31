import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { adoptFailureText, adoptHintOf, publishFailure, PublishView, type PublishViewProps } from './PublishTab'

const edge = { name: 'edge', url: 'https://proxy:8485', instance_id: 'proxy-1', paired_at: '2026-10-30T10:00:00Z' }

function props(over: Partial<PublishViewProps> = {}): PublishViewProps {
  return { proxies: [edge], loading: false, listError: null, proxy: '', connection: '', dataSource: '', busy: false, done: null, failure: null, canAdopt: true, adopting: false, adopted: null, adoptFailure: null, onAdopt: () => {}, onProxy: () => {}, onConnection: () => {}, onDataSource: () => {}, onPublish: () => {}, ...over }
}

const html = (over?: Partial<PublishViewProps>) => renderToStaticMarkup(PublishView(props(over)))

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

const refusal = (status: number, message: string, body: unknown = { error: message }) => new ApiError(status, message, body)
const handMade = refusal(403, 'The proxy refused reading the data source: the object was not made by a publisher, so it is not this client\'s to change. An administrator of the proxy can hand this data source to this server.', {
  error: 'The proxy refused reading the data source: the object was not made by a publisher.',
  adopt: { client: 'build-1', data_source: 'erp', call: 'POST /api/v1/data-sources/erp/adopt', body: { client: 'build-1' } },
})

describe('publishing a connection to a proxy', () => {
  it('says what it does: the read login only, the approver role, SQL Server', () => {
    const text = html()
    expect(text).toContain('read</strong> login')
    expect(text).toContain('approver role')
    expect(text).toContain('SQL Server, ODBC, DuckDB, PostgreSQL and Flight SQL')
    expect(text).toContain('data-testid="build-publish-proxy"')
    expect(text).toContain('>edge</option>')
  })

  it('has nothing to publish with, and says how to get a proxy, when the server is paired with none', () => {
    const text = html({ proxies: [] })
    expect(text).toContain('data-testid="build-publish-none"')
    expect(text).not.toContain('build-publish-button')
  })

  it('shows loading and not the empty message while the proxies are on their way', () => {
    const text = html({ proxies: undefined, loading: true })
    expect(text).toContain('Loading')
    expect(text).not.toContain('build-publish-none')
  })

  it('waits for a proxy and a connection before the button can be pressed, and while a publish is under way', () => {
    const disabled = (over: Partial<PublishViewProps>) => find(PublishView(props(over)), 'build-publish-button')!.props.disabled
    expect(disabled({})).toBe(true)
    expect(disabled({ proxy: 'edge' })).toBe(true)
    expect(disabled({ connection: 'sqlserver' })).toBe(true)
    expect(disabled({ proxy: 'edge', connection: ' ' })).toBe(true)
    expect(disabled({ proxy: 'edge', connection: 'sqlserver' })).toBe(false)
    expect(disabled({ proxy: 'edge', connection: 'sqlserver', busy: true })).toBe(true)
  })

  it('sends what was chosen when the button is pressed', () => {
    const onPublish = vi.fn()
    ;(find(PublishView(props({ proxy: 'edge', connection: 'sqlserver', onPublish })), 'build-publish-button')!.props.onClick as () => void)()
    expect(onPublish).toHaveBeenCalledTimes(1)
  })

  it('reports a created data source and an updated one', () => {
    expect(html({ done: { proxy: 'edge', data_source: 'erp', created: true, version: 'abc' } })).toContain('is now on')
    const updated = html({ done: { proxy: 'edge', data_source: 'erp', created: false, version: 'def' } })
    expect(updated).toContain('is updated on')
    expect(updated).not.toContain('(new)')
  })

  it('shows a refusal in the proxy\'s own words, and for one on a hand-made data source says an administrator can adopt it, naming the client', () => {
    const text = html({ failure: handMade })
    expect(text).toContain('data-testid="build-publish-failed"')
    expect(text).toContain('not made by a publisher')
    expect(text).toContain('data-testid="build-publish-adopt"')
    expect(text).toContain('POST /api/v1/data-sources/erp/adopt')
    expect(text).toContain('build-1')
    expect(text).toContain('cannot do that for itself')
  })

  it('offers no adoption for a refusal that has nothing to adopt', () => {
    expect(html({ failure: refusal(409, 'The connection \'x\' is fabric; a proxy serves sqlserver, odbc, duckdb, postgres and flightsql connections, not fabric.') })).not.toContain('build-publish-adopt')
    expect(html({ failure: refusal(403, 'This needs the approver role.') })).not.toContain('build-publish-adopt')
    expect(adoptHintOf(new Error('x'))).toBeNull()
    expect(adoptHintOf(refusal(403, 'x', { adopt: { client: 1 } }))).toBeNull()
  })

  it('says a missing approver role in a sentence, passes a proxy\'s words on and has a plain sentence for what it did not say', () => {
    expect(publishFailure(refusal(403, 'This needs the approver role.'))).toBe('Publishing needs the approver role on this server.')
    expect(publishFailure(refusal(502, 'The proxy \'edge\' did not answer (HttpRequestException).'))).toContain('did not answer')
    expect(publishFailure(refusal(409, 'No read login is stored for \'sqlserver\'.'))).toContain('No read login')
    expect(publishFailure(new TypeError('failed to fetch'))).toBe('The server did not answer. Try again.')
  })

  it('gives an administrator a button that hands the data source to this server, naming the client and the data source of the offer', () => {
    const onAdopt = vi.fn()
    const button = find(PublishView(props({ failure: handMade, onAdopt })), 'build-publish-adopt-button')!
    expect(button.props.disabled).toBe(false)
    ;(button.props.onClick as () => void)()
    expect(onAdopt).toHaveBeenCalledWith({ client: 'build-1', data_source: 'erp', call: 'POST /api/v1/data-sources/erp/adopt', body: { client: 'build-1' } })
    expect(find(PublishView(props({ failure: handMade, adopting: true })), 'build-publish-adopt-button')!.props.disabled).toBe(true)
  })

  it('shows no button to a person who is not an administrator, and no button when there is nothing to adopt', () => {
    const text = html({ failure: handMade, canAdopt: false })
    expect(text).toContain('data-testid="build-publish-adopt"')
    expect(text).not.toContain('build-publish-adopt-button')
    expect(text).toContain('Ask an administrator')
    expect(html({ failure: refusal(403, 'This needs the approver role.') })).not.toContain('build-publish-adopt-button')
  })

  it('says that the data source is this server\'s now, so the person publishes again, and gives the proxy\'s words when the hand-over was refused', () => {
    expect(html({ adopted: { name: 'erp', managed_by: 'build-1', adopted: true } })).toContain('<span class="mono">erp</span> is managed by this server now. Publish again.')
    const refused = html({ adoptFailure: refusal(409, 'The data source carries a serving policy (extensions.proxy).') })
    expect(refused).toContain('data-testid="build-publish-adopt-failed"')
    expect(refused).toContain('serving policy')
    expect(adoptFailureText(refusal(403, 'This call needs the admin role.'))).toBe('The proxy refused: This call needs the admin role.')
    expect(adoptFailureText(new TypeError('failed to fetch'))).toBe('The proxy did not answer. Try again.')
  })
})
