import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { noFilter, type AuditEntry } from '../audit'
import { AuditView, type AuditViewProps } from './AuditPage'

const entry = (over: Partial<AuditEntry> = {}): AuditEntry => ({ id: 1, at: '2026-11-11T10:00:00Z', actor: 'u1', actorName: 'Ada', verb: 'server.forget', outcome: 'ok', targetKind: 'server', targetId: 's1', targetName: null, detail: { serverName: 'Build prod', status: 204 }, ...over })
const props = (over: Partial<AuditViewProps> = {}): AuditViewProps => ({ entries: [entry(), entry({ id: 2, actor: 'anonymous', actorName: null, verb: 'relay.write', outcome: 'refused', detail: { method: 'PUT', path: '/relay/s1/api/x', status: 403, serverName: 'Build prod' } })], filter: noFilter, loading: false, error: null, hasMore: true, loadingMore: false, onFilter: () => {}, onMore: () => {}, ...over })
const html = (over?: Partial<AuditViewProps>) => renderToStaticMarkup(AuditView(props(over)))

/** The element with this test id, found by expanding the view's own (hook-free) components, so a test can press a button without a browser. */
function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

describe('the audit page', () => {
  it('lists each entry with who, what in words and how it ended, and offers older ones', () => {
    const text = html()
    expect(text).toContain('Ada')
    expect(text).toContain('removed the server Build prod')
    expect(text).toContain('Nobody signed in')
    expect(text).toContain('PUT /api/x on Build prod')
    expect(text).toContain('Refused')
    expect(text).toContain('data-testid="audit-more"')
  })

  it('says nothing was recorded, or that nothing matches a filter, and has no table then', () => {
    expect(html({ entries: [], hasMore: false })).toContain('Nothing has been recorded yet.')
    const narrowed = html({ entries: [], hasMore: false, filter: { ...noFilter, outcome: 'failed' } })
    expect(narrowed).toContain('Nothing recorded matches the filter.')
    expect(narrowed).not.toContain('audit-rows')
  })

  it('narrows to one person when their name is pressed', () => {
    const onFilter = vi.fn()
    ;(find(AuditView(props({ onFilter })), 'audit-actor')!.props.onClick as () => void)()
    expect(onFilter).toHaveBeenCalledWith({ ...noFilter, actor: 'u1' })
  })
})
