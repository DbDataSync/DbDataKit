import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { AddressVerify, DomainCheckView, InstanceEditorView, instanceSaveBlock, NavigationEditorView, navSaveBlock, PublishView, type InstanceEditorProps, type NavigationEditorProps, type PublishViewProps } from './AppEditors'
import { emptyInstanceForm, emptyNavForm, formFromRow, navFormFromSet, prepareManifest, type InstanceForm } from '../appEditors'
import { ApprovalsView, AppsView, AboutView, InstancesView, NavigationView, VersionsView, type ApprovalsViewProps, type InstancesViewProps, type NavigationViewProps, type VersionsViewProps } from './AppsPage'
import type { AppAbout, AppApprovalRow, AppCheck, AppDomainCheck, NavigationSetRow, AppDetail, AppInstanceRow, AppsCaller, AppSummary, AppVersionRow } from '../types'

const viewer: AppsCaller = { can_publish: false, can_approve: false, you: 'Ada' }
const admin: AppsCaller = { can_publish: true, can_approve: false, you: 'Ada' }
const approver: AppsCaller = { can_publish: false, can_approve: true, you: 'Grace' }

const acme: AppInstanceRow = {
  name: 'acme', app: 'notes', title: 'Acme notes', pin: null, follows: true, instance_version: 1, published_by: 'ada', published_at: '2026-11-01T10:00:00Z', path: '/apps/acme/', address: 'https://apps.example:8818/apps/acme/',
  bindings: {}, settings: {}, navigation: ['main'], mounted_by: ['front'], app_version: 2, serving_version: 2, manifest_version: '1.1.0', kind: 'app', trust: 'sandbox', runs_as_asked: true, approval_id: null,
}
const pinned: AppInstanceRow = { ...acme, name: 'pinned', follows: false, pin: 1, app_version: 1, mounted_by: [], navigation: [], address: null }

function instancesProps(over: Partial<InstancesViewProps> = {}): InstancesViewProps {
  return { rows: [acme, pinned], versionsOf: () => [1, 2], caller: admin, appsAddress: 'https://apps.example:8818/', chosen: {}, confirming: null, busy: false, onChoose: () => {}, onPin: () => {}, onAskRemove: () => {}, onRemove: () => {}, onNew: () => {}, onEdit: () => {}, ...over }
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

  it('says there is no apps host when the Gateway has none, and shows the empty state', () => {
    expect(html(InstancesView(instancesProps({ rows: [pinned], appsAddress: null })))).toContain('this Gateway serves no apps host')
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
  return { detail: detail(approver), by: 'Grace', note: '', approving: null, showing: null, busy: false, onServe: () => {}, onShow: () => {}, onAskApprove: () => {}, onNote: () => {}, onApprove: () => {}, egress: [], onToggleEgress: () => {}, ...over }
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

const mainSet: NavigationSetRow = { name: 'main', title: 'Main menu', entries: [{ instance: 'acme', label: 'Acme', icon: null }, { instance: 'pinned', label: null, icon: null }] }

function navigationProps(over: Partial<NavigationViewProps> = {}): NavigationViewProps {
  return { sets: [mainSet], caller: admin, confirming: null, busy: false, onNew: () => {}, onEdit: () => {}, onAskRemove: () => {}, onRemove: () => {}, ...over }
}

describe('the navigation sets', () => {
  it('lists each set with its members in order, and the empty state', () => {
    const text = html(NavigationView(navigationProps()))
    expect(text).toContain('acme (Acme) → pinned')
    expect(html(NavigationView(navigationProps({ sets: [] })))).toContain('data-testid="navigation-empty"')
  })

  it('has New, Edit and Remove (after a second click), off with the reason for a person without the role', () => {
    const onNew = vi.fn(); const onEdit = vi.fn(); const onAskRemove = vi.fn(); const onRemove = vi.fn()
    expect(find(NavigationView(navigationProps()), 'nav-new')!.props.disabled).toBe(false)
    ;(find(NavigationView(navigationProps({ onNew })), 'nav-new')!.props.onClick as () => void)()
    ;(find(NavigationView(navigationProps({ onEdit })), 'nav-edit-main')!.props.onClick as () => void)()
    ;(find(NavigationView(navigationProps({ onAskRemove })), 'nav-remove-main')!.props.onClick as () => void)()
    ;(find(NavigationView(navigationProps({ confirming: 'main', onRemove })), 'nav-remove-confirm-main')!.props.onClick as () => void)()
    expect(onNew).toHaveBeenCalled()
    expect(onEdit).toHaveBeenCalledWith(mainSet)
    expect(onAskRemove).toHaveBeenCalledWith('main')
    expect(onRemove).toHaveBeenCalledWith('main')
    const off = NavigationView(navigationProps({ caller: viewer }))
    for (const id of ['nav-new', 'nav-edit-main', 'nav-remove-main']) expect(find(off, id)!.props.disabled).toBe(true)
    expect(html(off)).toContain('data-testid="nav-new-why"')
    expect(html(off)).toContain('needs the publisher or administrator role')
  })
})

// ---- UE03: the navigation set editor ----

const instanceNames = [{ name: 'acme', title: 'Acme notes' }, { name: 'pinned', title: null }, { name: 'sales', title: 'Sales' }]

function navEditorProps(over: Partial<NavigationEditorProps> = {}): NavigationEditorProps {
  return { form: navFormFromSet(mainSet), existing: ['main', 'other'], instances: instanceNames, caller: admin, busy: false, onChange: () => {}, onSave: () => {}, onCancel: () => {}, ...over }
}

describe('the navigation set editor', () => {
  it('lists the members in order with their labels, and offers only the instances that are not listed yet', () => {
    const view = NavigationEditorView(navEditorProps())
    const text = html(view)
    expect(text.indexOf('nav-editor-member-acme')).toBeLessThan(text.indexOf('nav-editor-member-pinned'))
    expect(find(view, 'nav-editor-label-acme')!.props.value).toBe('Acme')
    expect(text).toContain('data-mode="edit"')
    const add = find(view, 'nav-editor-add')!
    const options = (add.props.children as ReactElement<{ value: string }>[][]).flat().map((o) => o.props.value)
    expect(options).toEqual(['', 'sales'])
    expect(html(NavigationEditorView(navEditorProps({ form: { ...navFormFromSet(mainSet), entries: [{ instance: 'acme', label: '', icon: '' }, { instance: 'pinned', label: '', icon: '' }, { instance: 'sales', label: '', icon: '' }] } })))).toContain('Every instance is already listed')
    expect(html(NavigationEditorView(navEditorProps({ instances: [] })))).toContain('No instance exists yet')
  })

  it('adds a member at the end, moves one up or down (an end cannot move further) and removes one', () => {
    const onChange = vi.fn()
    const view = NavigationEditorView(navEditorProps({ onChange }))
    ;(find(view, 'nav-editor-add')!.props.onChange as (e: { target: { value: string } }) => void)({ target: { value: 'sales' } })
    expect(onChange.mock.calls[0]![0].entries.map((e: { instance: string }) => e.instance)).toEqual(['acme', 'pinned', 'sales'])
    ;(find(view, 'nav-editor-down-acme')!.props.onClick as () => void)()
    expect(onChange.mock.calls[1]![0].entries.map((e: { instance: string }) => e.instance)).toEqual(['pinned', 'acme'])
    ;(find(view, 'nav-editor-remove-acme')!.props.onClick as () => void)()
    expect(onChange.mock.calls[2]![0].entries.map((e: { instance: string }) => e.instance)).toEqual(['pinned'])
    expect(find(view, 'nav-editor-up-acme')!.props.disabled).toBe(true)
    expect(find(view, 'nav-editor-down-pinned')!.props.disabled).toBe(true)
    expect(find(view, 'nav-editor-up-pinned')!.props.disabled).toBe(false)
  })

  it('says why Save is off: no role, no name, a name that is taken, no title, no member', () => {
    const why = (over: Partial<NavigationEditorProps>) => navSaveBlock({ ...navEditorProps(), ...over })
    expect(why({ caller: viewer })).toContain('publisher or administrator role')
    expect(why({ form: { ...emptyNavForm(), title: 'T', entries: navFormFromSet(mainSet).entries } })).toContain('Name the set')
    expect(why({ form: { ...emptyNavForm(), name: 'other', title: 'T', entries: navFormFromSet(mainSet).entries } })).toContain('already a navigation set named other')
    expect(why({ form: { ...navFormFromSet(mainSet), title: ' ' } })).toContain('Give the set a title')
    expect(why({ form: { ...navFormFromSet(mainSet), entries: [] } })).toContain('at least one instance')
    expect(why({})).toBeNull()
    const off = NavigationEditorView(navEditorProps({ form: { ...navFormFromSet(mainSet), entries: [] } }))
    expect(find(off, 'nav-editor-save')!.props.disabled).toBe(true)
    expect(html(off)).toContain('data-testid="nav-editor-why"')
  })

  it('renames a set that exists by saving it under a new name, and says so; a new set may not take a name that is used', () => {
    const renamed = { ...navFormFromSet(mainSet), name: 'crew' }
    const view = NavigationEditorView(navEditorProps({ form: renamed }))
    expect(html(view)).toContain('Saving makes the set under the name crew and stops serving main')
    expect(html(view)).toContain('Save under the new name')
    expect(navSaveBlock({ ...navEditorProps(), form: renamed })).toBeNull()
    expect(navSaveBlock({ ...navEditorProps(), form: { ...renamed, name: 'other' } })).toContain('already a navigation set named other')   // a rename may not take another set's name
    expect(html(NavigationEditorView(navEditorProps()))).not.toContain('data-testid="nav-editor-rename"')
    expect(html(NavigationEditorView(navEditorProps({ form: { ...emptyNavForm(), name: 'x', title: 'X' } })))).toContain('data-mode="new"')
  })
})

// ---- UE01: the instance form ----

const territoriesApp: AppSummary = { ...notes, versions: [1, 2] }

function editorProps(over: Partial<InstanceEditorProps> = {}): InstanceEditorProps {
  return {
    form: { ...emptyInstanceForm('notes'), name: 'sales' }, isNew: true, apps: [territoriesApp, { ...notes, name: 'vault', versions: [1] }], versionsOf: (app) => (app === 'notes' ? [1, 2] : [1]),
    models: ['territories', 'people'], existing: ['acme', 'pinned'], caller: admin, busy: false, onChange: () => {}, onSave: () => {}, onCancel: () => {}, ...over,
  }
}

describe('the addresses section of the instance form (plan 26 AP08)', () => {
  it('says there is no address, and adds a subdomain row named after the instance', () => {
    const text = html(InstanceEditorView(editorProps()))
    expect(text).toContain('data-testid="instance-editor-addresses-none"')
    let changed: InstanceForm | null = null
    const view = InstanceEditorView(editorProps({ onChange: (f) => { changed = f } }))
    ;(find(view, 'instance-editor-address-add')!.props.onClick as () => void)()
    expect(changed!.addresses).toEqual([{ host: 'sales.', kind: 'subdomain', path: '/', primary: false, certificate: '', certificatePassword: '' }])
  })

  it('shows the certificate secrets only for a custom domain, and blocks saving a custom domain with none', () => {
    const custom = { host: 'sales.contoso.com', kind: 'custom' as const, path: '/', primary: false, certificate: '', certificatePassword: '' }
    const form = { ...emptyInstanceForm('notes'), name: 'sales', addresses: [custom, { ...custom, host: 'sales.apps.example.com', kind: 'subdomain' as const }] }
    const text = html(InstanceEditorView(editorProps({ form })))
    expect(text).toContain('data-testid="instance-editor-address-certificate-0"')
    expect(text).not.toContain('data-testid="instance-editor-address-certificate-1"')
    const view = InstanceEditorView(editorProps({ form }))
    expect(find(view, 'instance-editor-save')!.props.disabled).toBe(true)
    expect(html(view)).toContain('needs its certificate')
    const filled = InstanceEditorView(editorProps({ form: { ...form, addresses: [{ ...custom, certificate: 'secret:cert-sales' }] } }))
    expect(find(filled, 'instance-editor-save')!.props.disabled).toBe(false)
  })

  it('offers Verify for a custom domain only when the screen can ask, and draws what the Gateway answered with the record to create (AP08, DNS challenge)', () => {
    const custom = { host: 'sales.contoso.com', kind: 'custom' as const, path: '/', primary: false, certificate: 'secret:c', certificatePassword: '' }
    const form = { ...emptyInstanceForm('notes'), name: 'sales', addresses: [custom, { ...custom, host: 'sales.apps.example.com', kind: 'subdomain' as const }] }
    const verify = vi.fn(() => Promise.reject(new Error('no')))
    const view = InstanceEditorView(editorProps({ form, verifyAddress: verify }))
    const rendered = JSON.stringify(view, (_, v) => (typeof v === 'function' ? 'fn' : v))
    expect(rendered).toContain('"index":0')                       // one AddressVerify, for the custom domain
    expect(rendered).not.toContain('"index":1')
    expect(html(InstanceEditorView(editorProps({ form })))).not.toContain('instance-editor-address-verify-0')   // no asking: no button

    const idle = renderToStaticMarkup(<AddressVerify index={0} host="sales.contoso.com" canPublish verify={verify} />)
    expect(idle).toContain('data-testid="instance-editor-address-verify-0"')
    expect(idle).not.toContain('disabled')
    expect(renderToStaticMarkup(<AddressVerify index={0} host=" " canPublish verify={verify} />)).toContain('disabled')           // nothing typed yet
    expect(renderToStaticMarkup(<AddressVerify index={0} host="x.test" canPublish={false} verify={verify} />)).toContain('disabled')   // the role is the Gateway's
    expect(verify).not.toHaveBeenCalled()

    const record = { name: '_dbdatakit-challenge.sales.contoso.com', type: 'TXT', value: 'dbdatakit-domain-verification=abc123' }
    const no: AppDomainCheck = { host: 'sales.contoso.com', verified: false, via: null, reason: 'The name does not resolve.', record }
    const unverified = renderToStaticMarkup(<DomainCheckView index={0} check={no} />)
    expect(unverified).toContain('data-verified="false"')
    expect(unverified).toContain('The name does not resolve.')
    expect(unverified).toContain('_dbdatakit-challenge.sales.contoso.com')
    expect(unverified).toContain('dbdatakit-domain-verification=abc123')
    const yes = renderToStaticMarkup(<DomainCheckView index={0} check={{ ...no, verified: true, via: 'challenge', reason: null }} />)
    expect(yes).toContain('data-verified="true"')
    expect(yes).toContain('the TXT record holds the value')
    expect(renderToStaticMarkup(<DomainCheckView index={0} check={{ ...no, verified: true, via: 'address', reason: null }} />)).toContain('resolves to this host')
    expect(renderToStaticMarkup(<DomainCheckView index={0} check="The Gateway said no." />)).toContain('data-verified="error"')
  })

  it('makes one address the primary one at a time and removes a row', () => {
    const a = { host: 'a.contoso.com', kind: 'custom' as const, path: '/', primary: true, certificate: 'secret:a', certificatePassword: '' }
    let changed: InstanceForm | null = null
    const form = { ...emptyInstanceForm('notes'), name: 'sales', addresses: [a, { ...a, host: 'b.contoso.com', primary: false }] }
    const view = InstanceEditorView(editorProps({ form, onChange: (f) => { changed = f } }))
    ;(find(view, 'instance-editor-address-primary-1')!.props.onChange as (e: unknown) => void)({ target: { checked: true } })
    expect(changed!.addresses.map((x) => x.primary)).toEqual([false, true])
    ;(find(view, 'instance-editor-address-remove-0')!.props.onClick as () => void)()
    expect(changed!.addresses.map((x) => x.host)).toEqual(['b.contoso.com'])
  })
})

describe('the new instance form', () => {
  it('offers a binding for each model the manifest lists and for no other', () => {
    const text = html(InstanceEditorView(editorProps()))
    expect(text).toContain('data-testid="instance-editor-binding-territories"')
    expect(text).toContain('data-testid="instance-editor-binding-people"')
    expect(text).not.toContain('instance-editor-binding-orders')
    expect(html(InstanceEditorView(editorProps({ models: [] })))).toContain('data-testid="instance-editor-bindings-none"')
  })

  it('shows a binding the version no longer has a model for, and says that saving removes it', () => {
    const form = { ...formFromRow({ ...acme, bindings: { territories: 'default', orders: 'live' } }) }
    const text = html(InstanceEditorView(editorProps({ form, isNew: false, models: ['territories'] })))
    expect(text).toContain('data-testid="instance-editor-binding-stale-orders"')
    expect(text).toContain('saving removes this binding')
    expect(text).not.toContain('instance-editor-binding-stale-territories')
  })

  it('waits for the models before saving, so that no binding is dropped by mistake', () => {
    const view = InstanceEditorView(editorProps({ models: null }))
    expect(find(view, 'instance-editor-save')!.props.disabled).toBe(true)
    expect(html(view)).toContain('data-testid="instance-editor-bindings-loading"')
    expect(html(view)).toContain('saving waits')
  })

  it('says why Save is off: no role, no name, a name already taken, no app, an empty or repeated setting', () => {
    const why = (over: Partial<InstanceEditorProps>) => instanceSaveBlock({ ...editorProps(), ...over })
    expect(why({ caller: viewer })).toContain('publisher or administrator role')
    expect(why({ form: { ...emptyInstanceForm('notes'), name: ' ' } })).toContain('Name the instance')
    expect(why({ form: { ...emptyInstanceForm('notes'), name: 'acme' } })).toContain('already an instance named acme')
    expect(why({ form: { ...emptyInstanceForm(''), name: 'sales' } })).toContain('Choose the app')
    expect(why({ form: { ...emptyInstanceForm('notes'), name: 'sales', settings: [{ key: ' ', value: 'x' }] } })).toContain('needs a name')
    expect(why({ form: { ...emptyInstanceForm('notes'), name: 'sales', settings: [{ key: 'a', value: '1' }, { key: 'a', value: '2' }] } })).toContain('listed twice')
    expect(why({})).toBeNull()
    const off = InstanceEditorView(editorProps({ caller: viewer }))
    expect(find(off, 'instance-editor-save')!.props.disabled).toBe(true)
    expect(html(off)).toContain('data-testid="instance-editor-why"')
  })

  it('saves when the form is complete, and changing the app of a new instance clears the pin and the bindings', () => {
    const onSave = vi.fn()
    const view = InstanceEditorView(editorProps({ onSave }))
    expect(find(view, 'instance-editor-save')!.props.disabled).toBe(false)
    ;(find(view, 'instance-editor-save')!.props.onClick as () => void)()
    expect(onSave).toHaveBeenCalled()
    const onChange = vi.fn()
    const changed = InstanceEditorView(editorProps({ form: { ...emptyInstanceForm('notes'), name: 'sales', pin: '1', bindings: { territories: 'default' } }, onChange }))
    ;(find(changed, 'instance-editor-app')!.props.onChange as (e: { target: { value: string } }) => void)({ target: { value: 'vault' } })
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ app: 'vault', pin: '', bindings: {} }))
  })

  it('fixes the name and the app of an instance that exists, and lists its settings with a hint for one the host reads', () => {
    const form = formFromRow({ ...acme, settings: { 'embed.allowed_origins': 'https://portal.example.com' } })
    const view = InstanceEditorView(editorProps({ form, isNew: false }))
    expect(find(view, 'instance-editor-name')!.props.disabled).toBe(true)
    expect(find(view, 'instance-editor-app')!.props.disabled).toBe(true)
    const text = html(view)
    expect(text).toContain('data-testid="instance-editor-setting-key-0"')
    expect(text).toContain('Origins that may frame this instance')
    expect(text).toContain('data-mode="edit"')
    expect(html(InstanceEditorView(editorProps()))).toContain('data-testid="instance-editor-settings-none"')
  })

  it('has New instance and Edit on the list, off with the reason for a person without the role', () => {
    expect(find(InstancesView(instancesProps()), 'instance-new')!.props.disabled).toBe(false)
    const onEdit = vi.fn()
    ;(find(InstancesView(instancesProps({ onEdit })), 'instance-edit-acme')!.props.onClick as () => void)()
    expect(onEdit).toHaveBeenCalledWith(acme)
    const off = InstancesView(instancesProps({ caller: viewer }))
    expect(find(off, 'instance-new')!.props.disabled).toBe(true)
    expect(find(off, 'instance-edit-acme')!.props.disabled).toBe(true)
    expect(html(off)).toContain('data-testid="instance-new-why"')
  })

  it('shows an instance\'s settings and bindings in its row', () => {
    const text = html(InstancesView(instancesProps({ rows: [{ ...acme, settings: { portal: 'front' }, bindings: { territories: 'default' } }] })))
    expect(text).toContain('portal=front, territories → default')
  })
})

// ---- UE02: the publish panel ----

const manifestOnly = prepareManifest('schemaVersion: 1\nname: notes\nversion: 1.0.0\n')
const sandboxCheck: AppCheck = { ok: true, diagnostics: [], files: 0, name: 'notes', manifest_version: '1.0.0', trust: 'sandbox', hash: 'ab'.repeat(32), approval_id: null, next_version: 3, about: null }
const trustedCheck: AppCheck = { ...sandboxCheck, trust: 'trusted', about: { ...about, egress: [{ directive: 'connect', origins: ['https://api.example.com'] }, { directive: 'img', origins: ['https://cdn.example.com'] }] } }

function publishProps(over: Partial<PublishViewProps> = {}): PublishViewProps {
  return {
    caller: { can_publish: true, can_approve: true, you: 'Ada' }, mode: 'folder', prepared: manifestOnly, pasted: '', note: '', check: sandboxCheck, checking: false, checkFailure: null, refusal: null, busy: false,
    by: 'Grace', approveNote: '', chosenEgress: [], onMode: () => {}, onPickFiles: () => {}, onPaste: () => {}, onNote: () => {}, onPublish: () => {}, onBy: () => {}, onApproveNote: () => {}, onToggleEgress: () => {}, onApprove: () => {}, onClear: () => {},
    ...over,
  }
}

describe('the publish panel', () => {
  it('takes a folder through a directory input, or a manifest in a text area', () => {
    const folder = html(PublishView(publishProps({ prepared: null, check: null })))
    expect(folder).toContain('data-testid="publish-folder"')
    expect(folder).toContain('webkitdirectory')
    expect(folder).not.toContain('data-testid="publish-manifest"')
    const manifest = html(PublishView(publishProps({ mode: 'manifest', prepared: null, check: null })))
    expect(manifest).toContain('data-testid="publish-manifest"')
    const onMode = vi.fn()
    ;(find(PublishView(publishProps({ onMode })), 'publish-mode-manifest')!.props.onClick as () => void)()
    expect(onMode).toHaveBeenCalledWith('manifest')
  })

  it('says what was chosen, what the Gateway makes of it (the version it would be, the trust, the hash) and each finding, then publishes', () => {
    const onPublish = vi.fn()
    const view = PublishView(publishProps({ onPublish, check: { ...sandboxCheck, diagnostics: [{ code: 'DBD-APP-010', severity: 'warning', file: 'app.yaml', line: 4, column: 1, message: 'The bundle refers to https://x.example.', fix: 'Remove it.' }] } }))
    const text = html(view)
    expect(text).toContain('the pasted manifest')
    expect(text).toContain('notes 1.0.0')
    expect(text).toContain('would be version 3, sandboxed')
    expect(text).toContain('abababababab')
    expect(text).toContain('app.yaml:4: warning DBD-APP-010: The bundle refers to https://x.example. (Remove it.)')
    expect(find(view, 'publish-send')!.props.disabled).toBe(false)
    expect(html(view)).toContain('Publish notes as version 3')
    ;(find(view, 'publish-send')!.props.onClick as () => void)()
    expect(onPublish).toHaveBeenCalled()
    expect(text).not.toContain('data-testid="publish-why"')
    expect(text).not.toContain('data-testid="publish-approve"')           // a sandboxed app has nothing to approve
  })

  it('shows why a folder cannot be sent, and says when nothing was found to fix', () => {
    const problem = html(PublishView(publishProps({ prepared: { ok: false, problem: 'There is no app.yaml in notes: an app\'s folder holds its manifest at the top.' }, check: null })))
    expect(problem).toContain('data-testid="publish-problem"')
    expect(problem).toContain('There is no app.yaml in notes')
    expect(html(PublishView(publishProps()))).toContain('data-testid="publish-diagnostics-none"')
  })

  it('keeps Publish off with the reason: no role, checking, errors, a trusted app with no approval', () => {
    const off = (over: Partial<PublishViewProps>) => PublishView(publishProps(over))
    expect(find(off({ caller: { can_publish: false, can_approve: false, you: 'Bob' } }), 'publish-send')!.props.disabled).toBe(true)
    expect(html(off({ caller: { can_publish: false, can_approve: false, you: 'Bob' } }))).toContain('publisher or administrator role')
    expect(find(off({ check: null, checking: true }), 'publish-send')!.props.disabled).toBe(true)
    expect(html(off({ check: null, checking: true }))).toContain('data-testid="publish-checking"')
    const errors = off({ check: { ...sandboxCheck, ok: false, diagnostics: [{ code: 'DBD-APP-006', severity: 'error', file: 'app.yaml', line: 1, column: 1, message: 'The manifest names `app.html`, which is not in the app\'s folder.', fix: null }] } })
    expect(find(errors, 'publish-send')!.props.disabled).toBe(true)
    expect(html(errors)).toContain('which is not in the app&#x27;s folder')
    expect(find(off({ check: trustedCheck }), 'publish-send')!.props.disabled).toBe(true)
    expect(html(off({ check: trustedCheck }))).toContain('asks to be trusted and no approval stands')
  })

  it('shows the Gateway\'s refusal and the failure of the check in its words', () => {
    expect(html(PublishView(publishProps({ refusal: "'notes' 1.0.0 asks to be trusted and has no approval for these exact files and manifest (hash abc)." })))).toContain('data-testid="publish-refusal"')
    expect(html(PublishView(publishProps({ check: null, checkFailure: "The file 'index.html' is not base64." })))).toContain('The file &#x27;index.html&#x27; is not base64.')
  })

  it('offers the approval of a trusted app\'s exact files with a choice of each origin the manifest asks for, none ticked until chosen', () => {
    const view = PublishView(publishProps({ check: trustedCheck }))
    const text = html(view)
    expect(text).toContain('data-testid="publish-approve"')
    expect(text).toContain('data-testid="publish-egress-connect=https://api.example.com"')
    expect(text).toContain('data-testid="publish-egress-img=https://cdn.example.com"')
    expect(text).not.toContain('checked=""')
    const onToggle = vi.fn()
    ;(find(PublishView(publishProps({ check: trustedCheck, onToggleEgress: onToggle })), 'publish-egress-img=https://cdn.example.com')!.props.onChange as () => void)()
    expect(onToggle).toHaveBeenCalledWith('img=https://cdn.example.com')
    expect(html(PublishView(publishProps({ check: trustedCheck, chosenEgress: ['connect=https://api.example.com'] })))).toContain('checked=""')
    expect(html(PublishView(publishProps({ check: { ...trustedCheck, about: { ...about, egress: [] } } })))).toContain('data-testid="publish-egress-none"')
  })

  it('approves as the named approver, off with the reason when that is the person publishing or the role is missing', () => {
    const onApprove = vi.fn()
    const view = PublishView(publishProps({ check: trustedCheck, onApprove }))
    expect(find(view, 'publish-approve-send')!.props.disabled).toBe(false)
    ;(find(view, 'publish-approve-send')!.props.onClick as () => void)()
    expect(onApprove).toHaveBeenCalled()
    const own = PublishView(publishProps({ check: trustedCheck, by: 'ada' }))
    expect(find(own, 'publish-approve-send')!.props.disabled).toBe(true)
    expect(html(own)).toContain('two people')
    const noRole = PublishView(publishProps({ check: trustedCheck, caller: { can_publish: true, can_approve: false, you: 'Ada' } }))
    expect(find(noRole, 'publish-approve-send')!.props.disabled).toBe(true)
    expect(html(noRole)).toContain('Approver role on this server')
    expect(html(PublishView(publishProps({ check: { ...trustedCheck, approval_id: 'apr_4' } })))).not.toContain('data-testid="publish-approve"')
    expect(find(PublishView(publishProps({ check: { ...trustedCheck, approval_id: 'apr_4' } })), 'publish-send')!.props.disabled).toBe(false)
  })

  it('offers the origins in the approval of a published version too, for the version\'s own manifest', () => {
    const v = version(2, { about: { ...about, egress: [{ directive: 'connect', origins: ['https://api.example.com'] }] } })
    const props = versionsProps({ detail: { name: 'notes', serving: 2, versions: [v], caller: approver }, approving: 2, egress: ['connect=https://api.example.com'] })
    const text = html(VersionsView(props))
    expect(text).toContain('data-testid="approve-egress-connect=https://api.example.com"')
    expect(text).toContain('checked=""')
    const none = version(2, { about: { ...about, egress: [] } })
    expect(html(VersionsView(versionsProps({ detail: { name: 'notes', serving: 2, versions: [none], caller: approver }, approving: 2 })))).toContain('data-testid="approve-egress-none"')
  })
})
