import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { ApprovalsView, AppsView, AboutView, InstancesView, NavigationView, VersionsView, type ApprovalsViewProps, type InstancesViewProps, type VersionsViewProps } from './AppsPage'
import type { AppAbout, AppApprovalRow, AppDetail, AppInstanceRow, AppsCaller, AppSummary, AppVersionRow } from '../types'

const viewer: AppsCaller = { can_publish: false, can_approve: false, you: 'Ada' }
const admin: AppsCaller = { can_publish: true, can_approve: false, you: 'Ada' }
const approver: AppsCaller = { can_publish: false, can_approve: true, you: 'Grace' }

const acme: AppInstanceRow = {
  name: 'acme', app: 'notes', title: 'Acme notes', pin: null, follows: true, instance_version: 1, published_by: 'ada', published_at: '2026-11-01T10:00:00Z', path: '/apps/acme/', address: 'https://apps.example:8818/apps/acme/',
  bindings: {}, navigation: ['main'], mounted_by: ['front'], app_version: 2, serving_version: 2, manifest_version: '1.1.0', kind: 'app', trust: 'sandbox', runs_as_asked: true, approval_id: null,
}
const pinned: AppInstanceRow = { ...acme, name: 'pinned', follows: false, pin: 1, app_version: 1, mounted_by: [], navigation: [], address: null }

function instancesProps(over: Partial<InstancesViewProps> = {}): InstancesViewProps {
  return { rows: [acme, pinned], versionsOf: () => [1, 2], caller: admin, appsAddress: 'https://apps.example:8818/', chosen: {}, confirming: null, busy: false, onChoose: () => {}, onPin: () => {}, onAskRemove: () => {}, onRemove: () => {}, ...over }
}

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') { const rendered = (element.type as (p: unknown) => ReactNode)(element.props); const inner = find(rendered, testId); if (inner) return inner }
  return find(element.props.children as ReactNode, testId)
}
const html = (node: ReactNode) => renderToStaticMarkup(<>{node}</>)

describe('the instances', () => {
  it('lists each with the app, the version it runs (follow or pin), its trust and a link to its address', () => {
    const text = html(InstancesView(instancesProps()))
    expect(text).toContain('data-testid="instance-row-acme"')
    expect(text).toContain('follows the app: version 2')
    expect(text).toContain('pinned to version 1 (the app serves 2)')
    expect(text).toContain('href="https://apps.example:8818/apps/acme/"')
    expect(text).toContain('data-testid="instance-open-acme"')
    expect(text).toContain('in the menu main')
  })

  it('says there is no apps host when the Proxy has none, and shows the empty state', () => {
    expect(html(InstancesView(instancesProps({ rows: [pinned], appsAddress: null })))).toContain('this Proxy serves no apps host')
    expect(html(InstancesView(instancesProps({ rows: [] })))).toContain('data-testid="instances-empty"')
  })

  it('says why an instance cannot be removed: a portal mounts it, or the person has no role; and an instance whose app is gone says so', () => {
    const text = html(InstancesView(instancesProps()))
    expect(text).toContain('The portal front mounts acme')
    expect(find(InstancesView(instancesProps()), 'instance-remove-acme')!.props.disabled).toBe(true)
    expect(find(InstancesView(instancesProps()), 'instance-remove-pinned')!.props.disabled).toBe(false)
    expect(html(InstancesView(instancesProps({ caller: viewer })))).toContain('needs the publisher or administrator role')
    expect(find(InstancesView(instancesProps({ caller: viewer })), 'instance-remove-pinned')!.props.disabled).toBe(true)
    expect(html(InstancesView(instancesProps({ rows: [{ ...acme, problem: "The app 'notes' is not published", app_version: undefined }] })))).toContain('data-testid="instance-problem"')
  })

  it('applies a pin only when the choice differs, and removes after a second click', () => {
    expect(find(InstancesView(instancesProps()), 'instance-pin-save-acme')!.props.disabled).toBe(true)
    expect(find(InstancesView(instancesProps({ chosen: { acme: '1' } })), 'instance-pin-save-acme')!.props.disabled).toBe(false)
    const onPin = vi.fn()
    ;(find(InstancesView(instancesProps({ chosen: { acme: '1' }, onPin })), 'instance-pin-save-acme')!.props.onClick as () => void)()
    expect(onPin).toHaveBeenCalledWith(acme)
    const onAskRemove = vi.fn()
    ;(find(InstancesView(instancesProps({ onAskRemove })), 'instance-remove-pinned')!.props.onClick as () => void)()
    expect(onAskRemove).toHaveBeenCalledWith('pinned')
    const onRemove = vi.fn()
    ;(find(InstancesView(instancesProps({ confirming: 'pinned', onRemove })), 'instance-remove-confirm-pinned')!.props.onClick as () => void)()
    expect(onRemove).toHaveBeenCalledWith('pinned')
  })
})

const notes: AppSummary = { name: 'notes', version: 2, versions: [1, 2], title: 'Notes', manifest_version: '1.1.0', description: null, kind: 'app', trust: 'sandbox', published_by: 'ada-laptop', published_at: '2026-11-01T10:00:00Z', instances: ['acme', 'pinned'] }

describe('the apps', () => {
  const props = (over = {}) => ({ apps: [notes, { ...notes, name: 'idle', instances: [] }], selected: null, caller: admin, confirming: null, busy: false, onSelect: () => {}, onAskRemove: () => {}, onRemove: () => {}, ...over })

  it('lists apps with the version served, the publisher and the instances; an app with an instance cannot be removed', () => {
    const text = html(AppsView(props()))
    expect(text).toContain('data-testid="app-row-notes"')
    expect(text).toContain('version 2 (1.1.0)')
    expect(text).toContain('acme, pinned')
    expect(find(AppsView(props()), 'app-remove-notes')!.props.disabled).toBe(true)
    expect(find(AppsView(props()), 'app-remove-idle')!.props.disabled).toBe(false)
    expect(text).toContain('still has instances (acme, pinned)')
    expect(html(AppsView(props({ apps: [] })))).toContain('data-testid="apps-empty"')
  })
})

const about: AppAbout = {
  title: 'Notes', description: null, kind: 'app', trust: 'trusted', roles: ['viewer'], models: [{ model: 'territories', entity: 'territory', operations: 'read and write' }], semantic_models: [], egress: [{ directive: 'connect', origins: ['https://api.example.com'] }],
  storage: { user: true, instance: false, quota_mb: 5 }, published_by: 'ada-laptop', published_at: '2026-11-01T10:00:00Z', approval: null,
}
const version = (n: number, over: Partial<AppVersionRow> = {}): AppVersionRow => ({ version: n, manifest_version: `1.${n}.0`, trust: 'trusted', published_by: 'ada-laptop', published_at: '2026-11-01T10:00:00Z', note: null, serving: n === 2, hash: 'ab'.repeat(32), approval_id: null, files: 3, about, ...over })
const detail = (caller: AppsCaller): AppDetail => ({ name: 'notes', serving: 2, versions: [version(2), version(1, { trust: 'sandbox', approval_id: null })], caller })

function versionsProps(over: Partial<VersionsViewProps> = {}): VersionsViewProps {
  return { detail: detail(approver), by: 'Grace', note: '', approving: null, showing: null, busy: false, onServe: () => {}, onShow: () => {}, onAskApprove: () => {}, onNote: () => {}, onApprove: () => {}, ...over }
}

describe('the versions of an app', () => {
  it('lists the versions newest first with the publisher, the approval and what serves', () => {
    const text = html(VersionsView(versionsProps()))
    expect(text.indexOf('version-row-2')).toBeLessThan(text.indexOf('version-row-1'))
    expect(text).toContain('2 (serving)')
    expect(text).toContain('ada-laptop')
    expect(text).toContain('abababababab')
  })

  it('serves an older version from the screen for a publisher, and says why not for anyone else', () => {
    const onServe = vi.fn()
    ;(find(VersionsView(versionsProps({ detail: detail(admin), onServe })), 'version-serve-1')!.props.onClick as () => void)()
    expect(onServe).toHaveBeenCalledWith(1)
    expect(find(VersionsView(versionsProps({ detail: detail(admin) })), 'version-serve-2')!.props.disabled).toBe(true)
    expect(html(VersionsView(versionsProps({ detail: detail(viewer) })))).toContain('needs the publisher or administrator role')
  })

  it('offers approval of a trusted version, and the button says why it is off: no role, or the approver is the author', () => {
    expect(find(VersionsView(versionsProps({ detail: detail(approver) })), 'version-approve-1')).toBeUndefined()      // sandboxed: nothing to approve
    expect(find(VersionsView(versionsProps()), 'version-approve-2')!.props.disabled).toBe(false)
    const withoutRole = html(VersionsView(versionsProps({ detail: detail(admin) })))
    expect(withoutRole).toContain('data-testid="version-approve-why-2"')
    expect(withoutRole).toContain('Approver role on this server')
    expect(find(VersionsView(versionsProps({ detail: detail(admin) })), 'version-approve-2')!.props.disabled).toBe(true)

    const form = (by: string) => VersionsView(versionsProps({ approving: 2, by }))
    expect(find(form('Grace'), 'approve-confirm-2')!.props.disabled).toBe(false)
    expect(find(form('ada-laptop'), 'approve-confirm-2')!.props.disabled).toBe(true)
    expect(html(form('ada-laptop'))).toContain('two people')
    expect(html(form('Grace'))).toContain('these exact files')
  })

  it('shows what a version can reach when asked: the models and what it does with each, outside addresses, the publisher and the missing approval', () => {
    const text = html(AboutView({ about, version: 2 }))
    expect(text).toContain('territories.territory: read and write')
    expect(text).toContain('connect: https://api.example.com')
    expect(text).toContain('ada-laptop')
    expect(text).toContain('none for these files')
    expect(html(AboutView({ about: { ...about, models: [], egress: [], approval: null, trust: 'sandbox' }, version: 1 }))).toContain('data-testid="about-models-none"')
    expect(html(VersionsView(versionsProps({ showing: 2 })))).toContain('data-testid="about-2"')
  })
})

const approval: AppApprovalRow = { id: 'apr_1', app: 'vault', version: '1.0.0', hash: 'cd'.repeat(32), approved_by: 'Grace Hopper', approved_at: '2026-11-02T09:00:00Z', note: 'reviewed', published_version: 1, revoked: false, revoked_at: null, operations: [{ model: 'territories', entity: 'territory', operations: 'read' }], egress: [] }

function approvalsProps(over: Partial<ApprovalsViewProps> = {}): ApprovalsViewProps {
  return { rows: [approval, { ...approval, id: 'apr_0', revoked: true, revoked_at: '2026-11-03T09:00:00Z' }], caller: approver, by: 'Grace Hopper', confirming: null, busy: false, onAskRevoke: () => {}, onRevoke: () => {}, ...over }
}

describe('the approvals', () => {
  it('shows who approved which hash, for what, when, and whether it still stands', () => {
    const text = html(ApprovalsView(approvalsProps()))
    expect(text).toContain('Grace Hopper')
    expect(text).toContain('cdcdcdcdcdcd')
    expect(text).toContain('territories.territory read')
    expect(text).toContain('reviewed')
    expect(text).toContain('standing')
    expect(text).toContain('revoked ')
    expect(text).toContain('published as 1')
    expect(html(ApprovalsView(approvalsProps({ rows: [] })))).toContain('data-testid="approvals-empty"')
  })

  it('revokes after a second click, as the named approver; only a standing approval can be revoked', () => {
    expect(find(ApprovalsView(approvalsProps()), 'approval-revoke-apr_0')).toBeUndefined()
    const onAskRevoke = vi.fn()
    ;(find(ApprovalsView(approvalsProps({ onAskRevoke })), 'approval-revoke-apr_1')!.props.onClick as () => void)()
    expect(onAskRevoke).toHaveBeenCalledWith('apr_1')
    const onRevoke = vi.fn()
    ;(find(ApprovalsView(approvalsProps({ confirming: 'apr_1', onRevoke })), 'approval-revoke-confirm-apr_1')!.props.onClick as () => void)()
    expect(onRevoke).toHaveBeenCalledWith('apr_1')
    expect(find(ApprovalsView(approvalsProps({ confirming: 'apr_1', by: '' })), 'approval-revoke-confirm-apr_1')!.props.disabled).toBe(true)
  })

  it('says why Revoke is off without the Approver role', () => {
    const view = ApprovalsView(approvalsProps({ caller: admin }))
    expect(find(view, 'approval-revoke-apr_1')!.props.disabled).toBe(true)
    expect(html(view)).toContain('Approver role on this server')
  })
})

describe('the navigation sets', () => {
  it('lists each set with its members in order, and the empty state', () => {
    const text = html(NavigationView({ sets: [{ name: 'main', title: 'Main menu', entries: [{ instance: 'acme', label: 'Acme', icon: null }, { instance: 'pinned', label: null, icon: null }] }] }))
    expect(text).toContain('acme (Acme) → pinned')
    expect(html(NavigationView({ sets: [] }))).toContain('data-testid="navigation-empty"')
  })
})
