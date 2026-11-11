import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import type { PendingServer } from '../api'
import { failureText, PendingServersView, since, type PendingServersViewProps } from './PendingServers'

const NOW = Date.parse('2026-10-30T12:00:00Z')
const build: PendingServer = { id: 'dbdatabuild-1', name: 'build', product: 'dbdatabuild', url: 'https://build:8470/', lastSeen: '2026-10-30T11:59:48Z' }
const sync: PendingServer = { id: 'dbdatasync-2', name: 'sync', product: 'dbdatasync', url: 'https://sync:8483/', lastSeen: '2026-10-30T11:55:00Z' }

function props(over: Partial<PendingServersViewProps> = {}): PendingServersViewProps {
  return { canManage: true, servers: [build, sync], loading: false, listError: null, busy: null, failures: {}, now: NOW, onConfirm: () => {}, onReject: () => {}, ...over }
}

const html = (over?: Partial<PendingServersViewProps>) => renderToStaticMarkup(PendingServersView(props(over)))

/** The element with this test id, found by expanding the view's own (hook-free) components, so a test can press a button without a browser. */
function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}
const press = (view: ReactNode, testId: string) => (find(view, testId)!.props.onClick as () => void)()

describe('the servers waiting for confirmation', () => {
  it('lists each server with its name, product, address and how long ago it announced, and never a code', () => {
    const text = html()

    expect(text).toContain('data-testid="pending-row-build"')
    expect(text).toContain('data-testid="pending-row-sync"')
    expect(text).toContain('https://build:8470/')
    expect(text).toContain('12 s ago')
    expect(text).toContain('5 min ago')
    expect(text.toLowerCase()).not.toContain('code')
    expect(text).toContain('Confirm')
    expect(text).toContain('Reject')
  })

  it('says so when nobody is waiting, and has no table or buttons then', () => {
    const text = html({ servers: [] })

    expect(text).toContain('data-testid="pending-empty"')
    expect(text).not.toContain('pending-confirm-button')
    expect(text).not.toContain('grid-head')
  })

  it('shows loading and not the empty message while the list is on its way', () => {
    const text = html({ servers: undefined, loading: true })

    expect(text).toContain('Loading')
    expect(text).not.toContain('pending-empty')
  })

  it('is nothing at all for anybody who is not an administrator: no list, no buttons', () => {
    expect(PendingServersView(props({ canManage: false }))).toBeNull()
    expect(html({ canManage: false })).toBe('')
  })

  it('confirms and rejects the server whose button was pressed', () => {
    const onConfirm = vi.fn()
    const onReject = vi.fn()
    const view = PendingServersView(props({ servers: [build], onConfirm, onReject }))

    press(view, 'pending-confirm-button')
    press(view, 'pending-reject-button')

    expect(onConfirm).toHaveBeenCalledExactlyOnceWith('dbdatabuild-1')
    expect(onReject).toHaveBeenCalledExactlyOnceWith('dbdatabuild-1')
  })

  it('makes only the server with a call under way wait', () => {
    const text = html({ busy: 'dbdatabuild-1' })

    expect(text.match(/disabled=""/g)).toHaveLength(2)
    expect(text).toContain('Working…')
  })

  it('shows a failed confirmation under its server, in words, and keeps the server listed', () => {
    const text = html({ failures: { 'dbdatabuild-1': 'The pairing code has expired or was already used.' } })

    expect(text).toContain('data-testid="pending-error"')
    expect(text).toContain('The pairing code has expired or was already used.')
    expect(text).toContain('data-testid="pending-row-build"')
    expect(text.match(/data-testid="pending-error"/g)).toHaveLength(1)
  })

  it('says why the list could not be read, and does not claim nobody is waiting', () => {
    const text = html({ servers: undefined, listError: new ApiError(500, 'The console failed.') })

    expect(text).toContain('The console failed.')
    expect(text).not.toContain('pending-empty')
  })
})

describe('what a failure says', () => {
  it('uses the console\'s own message for a refused confirmation', () => {
    expect(failureText(new ApiError(400, 'build did not answer.'))).toBe('build did not answer.')
  })

  it('says an administrator is needed when the console refuses the person', () => {
    expect(failureText(new ApiError(403, 'Forbidden'))).toContain('administrators')
    expect(failureText(new ApiError(401, 'Unauthorized'))).toContain('administrators')
  })

  it('says a server that stopped waiting is gone, not that something failed', () => {
    expect(failureText(new ApiError(404, 'No such server is waiting.'))).toContain('no longer waiting')
  })

  it('does not show a stack or a network error\'s own text', () => {
    expect(failureText(new TypeError('Failed to fetch'))).toBe('The console did not answer. Try again.')
  })
})

describe('how long ago', () => {
  it('counts seconds, minutes and hours', () => {
    expect(since('2026-10-30T11:59:50Z', NOW)).toBe('10 s ago')
    expect(since('2026-10-30T11:50:00Z', NOW)).toBe('10 min ago')
    expect(since('2026-10-30T09:00:00Z', NOW)).toBe('3 h ago')
  })

  it('never reads as in the future, and says unknown for a date it cannot read', () => {
    expect(since('2026-10-30T12:00:30Z', NOW)).toBe('0 s ago')
    expect(since('not a date', NOW)).toBe('unknown')
  })
})
