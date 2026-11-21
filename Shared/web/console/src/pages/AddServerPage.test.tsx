import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import type { JoinTokenMade, WaitingJoinToken } from '../api'
import { emptyInviteForm, endsIn, inviteProblems, inviteRequest, isConsoleAddress, toggleRole, type InviteForm } from '../joinTokens'
import { InviteFormView, MadeCommandView, tokenFailure, WaitingTokensView, type InviteFormViewProps, type WaitingTokensViewProps } from './AddServerPage'

const NOW = Date.parse('2026-11-21T09:00:00Z')
const filled: InviteForm = { ...emptyInviteForm('https://console.example.com:7800'), product: 'runner' }

/** The element with this test id, found by expanding the view's own (hook-free) components, so a test can press a button without a browser. */
function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

function formProps(over: Partial<InviteFormViewProps> = {}): InviteFormViewProps {
  return { form: filled, tried: false, making: false, failure: null, onChange: () => {}, onMake: () => {}, ...over }
}
const formHtml = (over?: Partial<InviteFormViewProps>) => renderToStaticMarkup(InviteFormView(formProps(over)))

describe('the Add a server form', () => {
  it('opens with no server chosen, the admin ceiling, 15 minutes and the address this browser reached the console at', () => {
    const form = emptyInviteForm('https://console.example.com:7800')
    expect(form).toEqual({ product: '', roles: ['admin'], group: '', minutes: '15', url: 'https://console.example.com:7800' })
    expect(Object.keys(inviteProblems(form))).toEqual(['product'])
  })

  it('names each problem beside its field once Make was pressed, and not before', () => {
    const bad: InviteForm = { product: 'store', roles: [], group: 'no spaces', minutes: '2000', url: 'http://console:7800' }
    expect(inviteProblems(bad)).toEqual({
      product: 'Choose the server that joins.',
      roles: 'Choose at least one role.',
      group: 'A group is letters, digits and . _ -, up to 64.',
      minutes: 'A whole number of minutes from 1 to 1440.',
      url: 'An https address with a host and a port, such as https://console.example.com:7800.',
    })
    expect(formHtml({ form: bad })).not.toContain('add-server-problem-')
    const tried = formHtml({ form: bad, tried: true })
    for (const field of ['product', 'roles', 'group', 'minutes', 'url']) expect(tried).toContain(`data-testid="add-server-problem-${field}"`)
    expect(tried).toMatch(/data-testid="add-server-make-button"[^>]*disabled|disabled[^>]*data-testid="add-server-make-button"/)
  })

  it('accepts an https address with a host and a port only', () => {
    expect(isConsoleAddress('https://console.example.com:7800')).toBe(true)
    expect(isConsoleAddress('https://console.example.com:7800/')).toBe(true)
    expect(isConsoleAddress('https://localhost:8481')).toBe(true)
    expect(isConsoleAddress('http://console:7800')).toBe(false)
    expect(isConsoleAddress('https://console:7800/console')).toBe(false)
    expect(isConsoleAddress('https://console:7800/?a=1')).toBe(false)
    expect(isConsoleAddress('console:7800')).toBe(false)
    expect(isConsoleAddress('')).toBe(false)
  })

  it('sends the roles in the form\'s order, an empty group as none, the minutes as a number and the address without a trailing slash', () => {
    expect(inviteRequest({ product: 'workspace', roles: ['admin', 'viewer', 'author'], group: '  ', minutes: ' 30 ', url: 'https://console.example.com:7800/' }))
      .toEqual({ product: 'workspace', roles: ['viewer', 'author', 'admin'], group: null, minutes: 30, url: 'https://console.example.com:7800' })
    expect(inviteRequest({ ...filled, group: ' night ' }).group).toBe('night')
  })

  it('turns a role on and off without doubling it', () => {
    expect(toggleRole(['admin'], 'viewer', true)).toEqual(['admin', 'viewer'])
    expect(toggleRole(['admin'], 'admin', true)).toEqual(['admin'])
    expect(toggleRole(['admin', 'viewer'], 'admin', false)).toEqual(['viewer'])
  })

  it('changes the form through its controls and makes the token with Make', () => {
    const onChange = vi.fn()
    const onMake = vi.fn()
    const view = InviteFormView(formProps({ form: emptyInviteForm('https://c:7800'), onChange, onMake }))
    ;(find(view, 'add-server-product-gateway')!.props.onChange as () => void)()
    expect(onChange).toHaveBeenLastCalledWith(expect.objectContaining({ product: 'gateway' }))
    ;(find(view, 'add-server-role-operator')!.props.onChange as (e: unknown) => void)({ target: { checked: true } })
    expect(onChange).toHaveBeenLastCalledWith(expect.objectContaining({ roles: ['admin', 'operator'] }))
    ;(find(view, 'add-server-group-input')!.props.onChange as (e: unknown) => void)({ target: { value: 'sales' } })
    expect(onChange).toHaveBeenLastCalledWith(expect.objectContaining({ group: 'sales' }))
    ;(find(view, 'add-server-make-button')!.props.onClick as () => void)()
    expect(onMake).toHaveBeenCalledOnce()
  })

  it('says what the console answered when a token could not be made', () => {
    expect(formHtml({ failure: 'The join token cannot be made: the group is letters, digits and . _ -, up to 64.' })).toContain('data-testid="add-server-error"')
    expect(tokenFailure(new ApiError(403, 'Forbidden'))).toBe('Only administrators can add a server.')
    expect(tokenFailure(new ApiError(400, 'Choose the server that joins: a Gateway, a Runner or a Workspace.'))).toBe('Choose the server that joins: a Gateway, a Runner or a Workspace.')
    expect(tokenFailure(new TypeError('fetch failed'))).toBe('The console did not answer. Try again.')
    expect(tokenFailure(null)).toBeNull()
  })
})

describe('the command just made', () => {
  const made: JoinTokenMade = {
    command: 'dbdatarunner pair --host https://localhost:7800 --token k7d2mq4x5vbn6r3ta2wq4zj7pa --console-thumbprint sha256:3f0c',
    tokenId: 'jt-0123456789abcdef', product: 'runner', roles: ['admin'], group: null, expires: '2026-11-21T09:15:00Z', loopback: true,
  }

  it('shows the command to copy once, when it ends, and a note when it names a loopback address', () => {
    const html = renderToStaticMarkup(<MemoryRouter><MadeCommandView made={made} now={NOW} onCopy={() => {}} /></MemoryRouter>)
    expect(html).toContain("Run this on the runner&#x27;s machine")
    expect(html).toContain(made.command)
    expect(html).toContain('in 15 min')
    expect(html).toContain('data-testid="add-server-loopback"')
    expect(renderToStaticMarkup(<MemoryRouter><MadeCommandView made={{ ...made, loopback: false }} now={NOW} onCopy={() => {}} /></MemoryRouter>)).not.toContain('add-server-loopback')
  })

  it('copies the whole command', () => {
    const onCopy = vi.fn()
    ;(find(MadeCommandView({ made, now: NOW, onCopy }), 'add-server-copy-button')!.props.onClick as () => void)()
    expect(onCopy).toHaveBeenCalledWith(made.command)
  })
})

describe('the tokens waiting to be used', () => {
  const tokens: WaitingJoinToken[] = [
    { id: 'jt-a', product: 'runner', roles: ['viewer', 'operator'], group: 'night', madeBy: 'Ada', madeAt: '2026-11-21T08:58:00Z', expires: '2026-11-21T09:13:00Z', tries: 0 },
    { id: 'jt-b', product: 'gateway', roles: ['admin'], group: null, madeBy: 'ops (command line)', madeAt: '2026-11-21T08:00:00Z', expires: '2026-11-21T12:00:00Z', tries: 2 },
  ]
  const props = (over: Partial<WaitingTokensViewProps> = {}): WaitingTokensViewProps => ({ tokens, loading: false, listError: null, busy: null, failure: null, now: NOW, onCancel: () => {}, ...over })

  it('lists each with its server, ceiling, group, who made it and when it ends, and never a token', () => {
    const html = renderToStaticMarkup(WaitingTokensView(props()))
    expect(html).toContain('data-testid="waiting-token-jt-a"')
    expect(html).toContain('Runner')
    expect(html).toContain('viewer, operator')
    expect(html).toContain('night')
    expect(html).toContain('ops (command line)')
    expect(html).toContain('in 13 min')
    expect(html).toContain('in 3 h')
    expect(html).toContain('2 refused')
    expect(html).not.toContain('--token')
    expect(renderToStaticMarkup(WaitingTokensView(props({ tokens: [] })))).toContain('No token is waiting.')
  })

  it('cancels the token whose button is pressed, and holds that button while it is cancelled', () => {
    const onCancel = vi.fn()
    ;(find(WaitingTokensView(props({ onCancel })), 'waiting-token-cancel-button')!.props.onClick as () => void)()
    expect(onCancel).toHaveBeenCalledWith('jt-a')
    expect(renderToStaticMarkup(WaitingTokensView(props({ busy: 'jt-b' })))).toContain('Cancelling…')
  })

  it('says how long a token still works', () => {
    expect(endsIn('2026-11-21T09:00:40Z', NOW)).toBe('in 40 s')
    expect(endsIn('2026-11-21T09:30:00Z', NOW)).toBe('in 30 min')
    expect(endsIn('2026-11-21T08:59:00Z', NOW)).toBe('ended')
    expect(endsIn('not a time', NOW)).toBe('unknown')
  })
})
