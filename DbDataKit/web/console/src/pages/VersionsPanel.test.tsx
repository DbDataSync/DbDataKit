import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import type { DocumentVersions } from '@dbdatakit/visuals'
import { VersionsView, type VersionsViewProps } from './VersionsPanel'

const versions: DocumentVersions = {
  kind: 'dashboard', name: 'sales', active: 2, previous: 1,
  versions: [
    { version: 2, publishedBy: 'Ada', publishedAt: '2026-11-08T10:00:00Z', note: 'second', active: true, dependsOn: [] },
    { version: 1, publishedBy: 'Bob', publishedAt: '2026-11-07T09:30:00Z', note: 'first', active: false, dependsOn: [] },
  ],
}

function props(over: Partial<VersionsViewProps> = {}): VersionsViewProps {
  return { kind: 'dashboard', name: 'sales', versions, allowed: true, busy: false, confirming: false, result: null, onSwitch: () => {}, onRollback: () => {}, onAskRetire: () => {}, onRetire: () => {}, onKeep: () => {}, ...over }
}

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

const tree = (over?: Partial<VersionsViewProps>) => VersionsView(props(over))

describe('the versions panel of a published document', () => {
  it('lists each version with who, when and why, and says which one is served', () => {
    const html = renderToStaticMarkup(tree())
    expect(html).toContain('data-testid="views-version-2"')
    expect(html).toContain('data-testid="views-version-1"')
    expect(html).toContain('Ada')
    expect(html).toContain('2026-11-07 09:30 UTC')
    expect(html).toContain('data-testid="views-version-served-2"')
    expect(html).not.toContain('data-testid="views-version-served-1"')
    expect(html).toContain('Version 2 is served to the group.')
  })

  it('serves an earlier version when its button is pressed, and not the served one', () => {
    const onSwitch = vi.fn()
    ;(find(tree({ onSwitch }), 'views-version-switch-1')!.props.onClick as () => void)()
    expect(onSwitch).toHaveBeenCalledWith(1)
    expect(find(tree(), 'views-version-switch-2')!.props.disabled).toBe(true)
    expect(find(tree(), 'views-version-switch-2')!.props.title).toContain('served now')
    expect(find(tree(), 'views-version-switch-1')!.props.disabled).toBe(false)
  })

  it('names the version a rollback returns to', () => {
    expect(renderToStaticMarkup(tree())).toContain('Roll back to version 1')
    expect(find(tree({ versions: { ...versions, previous: null } }), 'views-version-rollback')!.props.disabled).toBe(true)
  })

  it('disables every change for a person without the author role, saying why', () => {
    for (const id of ['views-version-switch-1', 'views-version-rollback', 'views-version-retire']) {
      const button = find(tree({ allowed: false }), id)!
      expect(button.props.disabled, id).toBe(true)
      expect(String(button.props.title), id).toContain('author role')
    }
  })

  it('asks before removing from the group, and keeps it when told to', () => {
    const asked = vi.fn()
    ;(find(tree({ onAskRetire: asked }), 'views-version-retire')!.props.onClick as () => void)()
    expect(asked).toHaveBeenCalled()
    const confirming = renderToStaticMarkup(tree({ confirming: true }))
    expect(confirming).toContain('views-version-retire-confirm')
    expect(confirming).not.toContain('data-testid="views-version-retire"')
    const retired = vi.fn()
    ;(find(tree({ confirming: true, onRetire: retired }), 'views-version-retire-confirm')!.props.onClick as () => void)()
    expect(retired).toHaveBeenCalled()
  })

  it('says a removed document is not served and has nothing to remove or roll back from', () => {
    const gone = { ...versions, active: null }
    expect(renderToStaticMarkup(tree({ versions: gone }))).toContain('Not served')
    expect(find(tree({ versions: gone }), 'views-version-retire')!.props.disabled).toBe(true)
    expect(find(tree({ versions: gone }), 'views-version-rollback')!.props.disabled).toBe(true)
    expect(find(tree({ versions: gone }), 'views-version-switch-2')!.props.disabled).toBe(false)
  })

  it('shows the server\'s refusal as an alert and a success as a status', () => {
    expect(renderToStaticMarkup(tree({ result: { tone: 'error', text: 'it changed since you looked' } }))).toContain('role="alert"')
    expect(renderToStaticMarkup(tree({ result: { tone: 'ok', text: 'Version 1 of sales is served now.' } }))).toContain('data-testid="views-versions-result"')
  })

  it('says so when no version is stored', () => {
    expect(renderToStaticMarkup(tree({ versions: { ...versions, versions: [], active: null, previous: null } }))).toContain('data-testid="views-versions-empty"')
  })

  it('disables the buttons while a call is on its way', () => {
    expect(find(tree({ busy: true }), 'views-version-switch-1')!.props.disabled).toBe(true)
    expect(find(tree({ busy: true }), 'views-version-rollback')!.props.disabled).toBe(true)
  })
})
