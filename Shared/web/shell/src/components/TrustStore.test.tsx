import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/http'
import { policyText, TrustStoreView, validPublicKey, type TrustState, type TrustStoreViewProps } from './TrustStore'

const KEY = 'A'.repeat(43)
const state: TrustState = {
  kind: 'app',
  required: true,
  test_group: false,
  keys: [
    { key_id: '0123456789abcdef', label: 'ci', added_by: 'ada', added_at: '2026-11-01T10:00:00Z', revoked: false, revoked_by: null },
    { key_id: 'fedcba9876543210', label: 'old ci', added_by: 'ada', added_at: '2026-10-01T10:00:00Z', revoked: true, revoked_by: 'bob' },
  ],
}

const props = (over: Partial<TrustStoreViewProps> = {}): TrustStoreViewProps => ({
  kind: 'app', state, loading: false, error: null, canEdit: true, policyEditable: true, publicKey: '', label: '', revoking: null, busy: false,
  onPublicKey: () => {}, onLabel: () => {}, onAdd: () => {}, onAskRevoke: () => {}, onRevoke: () => {}, onToggleRequired: () => {}, ...over,
})

/** The first element with this test id in an element tree (the view is a plain function, so its tree can be walked and its handlers called). */
function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  return find(element.props.children as ReactNode, testId)
}

const html = (over?: Partial<TrustStoreViewProps>) => renderToStaticMarkup(TrustStoreView(props(over)))

describe('a public key as the add call reads it', () => {
  it('is 32 bytes in base64url, 43 characters, with or without padding', () => {
    expect(validPublicKey(KEY)).toBe(true)
    expect(validPublicKey(` ${KEY}= `)).toBe(true)
    expect(validPublicKey('A'.repeat(42))).toBe(false)
    expect(validPublicKey('A'.repeat(42) + '+')).toBe(false)
    expect(validPublicKey('')).toBe(false)
  })
})

describe('the signature policy in words', () => {
  it('says what a required, an optional and a test group mean', () => {
    expect(policyText('app', true, false)).toContain('is refused and the version that serves stays')
    expect(policyText('app', false, false)).toContain('turned the requirement off')
    expect(policyText('app', false, true)).toContain('test group')
  })
})

describe('the trust store of one kind', () => {
  it('lists the keys with their label and who added them, and marks a revoked one', () => {
    const text = html()
    expect(text).toContain('Publisher keys for app')
    expect(text).toContain('0123456789abcdef')
    expect(text).toContain('by ada, 2026-11-01')
    expect(text).toContain('revoked by bob')
    expect(text.match(/data-testid="trust-app-revoke"/g)).toHaveLength(1)   // only the key that is not revoked has the button
    expect(text).toContain('data-value="true"')
  })

  it('says that no key means every publish is refused where signatures are required', () => {
    expect(html({ state: { ...state, keys: [] } })).toContain('every publish of it is refused until one is added')
    expect(html({ state: { ...state, keys: [], required: false } })).not.toContain('refused until')
  })

  it('adds a key only when what is typed is a public key, and says why not otherwise', () => {
    const add = (over: Partial<TrustStoreViewProps>) => find(TrustStoreView(props(over)), 'trust-app-add')!.props.disabled
    expect(add({ publicKey: '' })).toBe(true)
    expect(add({ publicKey: 'short' })).toBe(true)
    expect(add({ publicKey: KEY })).toBe(false)
    expect(add({ publicKey: KEY, busy: true })).toBe(true)
    expect(html({ publicKey: 'short' })).toContain('trust-app-key-problem')
    expect(html({ publicKey: KEY })).not.toContain('trust-app-key-problem')
    const onAdd = vi.fn()
    ;(find(TrustStoreView(props({ publicKey: KEY, onAdd })), 'trust-app-add')!.props.onClick as () => void)()
    expect(onAdd).toHaveBeenCalledTimes(1)
  })

  it('asks before it revokes, and names what a revocation does', () => {
    const onAskRevoke = vi.fn()
    ;(find(TrustStoreView(props({ onAskRevoke })), 'trust-app-revoke')!.props.onClick as () => void)()
    expect(onAskRevoke).toHaveBeenCalledWith('0123456789abcdef')
    const asking = html({ revoking: '0123456789abcdef' })
    expect(asking).toContain('trust-app-revoke-ask')
    expect(asking).toContain('versions it signed stay')
    const onRevoke = vi.fn()
    ;(find(TrustStoreView(props({ revoking: '0123456789abcdef', onRevoke })), 'trust-app-revoke-go')!.props.onClick as () => void)()
    expect(onRevoke).toHaveBeenCalledWith('0123456789abcdef')
  })

  it('shows the requirement switch only where the server has one, and names the opposite action', () => {
    expect(find(TrustStoreView(props()), 'trust-app-toggle')!.props.children).toBe('Make signatures optional')
    expect(find(TrustStoreView(props({ state: { ...state, required: false } })), 'trust-app-toggle')!.props.children).toBe('Require signatures')
    expect(html({ policyEditable: false })).not.toContain('trust-app-toggle')
  })

  it('gives a viewer the keys and nothing to press', () => {
    const text = html({ canEdit: false })
    expect(text).toContain('trust-app-why')
    expect(text).not.toContain('trust-app-add')
    expect(text).not.toContain('trust-app-revoke"')
  })

  it('shows loading and the server\'s own refusal', () => {
    expect(html({ state: null, loading: true })).toContain('trust-app-loading')
    expect(html({ state: null, error: new ApiError(403, 'This needs the admin role.', null) })).toContain('This needs the admin role.')
  })
})
