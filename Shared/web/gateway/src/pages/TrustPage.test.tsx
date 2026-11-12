import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { TRUST_KINDS, groupModeText } from '../trust'
import { TrustView, type TrustViewProps } from './TrustPage'

const props = (over: Partial<TrustViewProps> = {}): TrustViewProps => ({ testGroup: false, canEdit: true, busy: false, error: null, onMode: () => {}, card: (kind) => <span data-testid="card" data-kind={kind} />, ...over })

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  return find(element.props.children as ReactNode, testId)
}

describe('the Publishers screen', () => {
  it('draws one card for every kind a publisher can send to the gateway', () => {
    const html = renderToStaticMarkup(TrustView(props()))
    for (const kind of TRUST_KINDS) expect(html).toContain(`data-kind="${kind}"`)
    expect(TRUST_KINDS).toEqual(expect.arrayContaining(['semantic-model', 'data-source', 'entity-model', 'app', 'app-instance', 'app-nav', 'app-approval', 'dashboard', 'report']))
  })

  it('says which mode the group is in and offers the other one to an administrator', () => {
    expect(renderToStaticMarkup(TrustView(props()))).toContain('This group is a production group')
    expect(find(TrustView(props()), 'trust-group-toggle')!.props.children).toBe('Mark it a test group')
    expect(find(TrustView(props({ testGroup: true })), 'trust-group-toggle')!.props.children).toBe('Mark it production')
    const onMode = vi.fn()
    ;(find(TrustView(props({ onMode })), 'trust-group-toggle')!.props.onClick as () => void)()
    expect(onMode).toHaveBeenCalledWith('test')
    expect(groupModeText(true)).toContain('optional')
    expect(groupModeText(false)).toContain('required')
  })

  it('shows a viewer the mode and no button, and nothing of the mode while it is not known', () => {
    expect(renderToStaticMarkup(TrustView(props({ canEdit: false })))).not.toContain('trust-group-toggle')
    expect(renderToStaticMarkup(TrustView(props({ testGroup: null })))).not.toContain('trust-group')
  })
})
