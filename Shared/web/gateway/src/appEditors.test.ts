import { describe, expect, it } from 'vitest'
import {
  bindingRows, configurationLabel, egressChoices, egressParam, emptyInstanceForm, formFromRow, instanceFormProblem, instanceRequest, MAX_PACKAGE_BYTES, modelsOf, packageApproveBlock, pinRequest, prepareFolder, prepareManifest, publishBlock, publishBody, sizeLabel, toBase64, toggled,
  availableMembers, emptyNavForm, entriesLabel, isRename, moveEntry, navFormFromSet, navFormProblem, navRequest,
  type PickedFile,
} from './appEditors'
import type { AppAbout, AppCheck, AppDetail, AppInstanceRow, AppsCaller, AppVersionRow, NavigationSetRow } from './types'

const row = { name: 'acme', app: 'notes', pin: null, title: 'Acme notes', settings: { portal: 'front' }, bindings: { territories: 'default' } } satisfies Pick<AppInstanceRow, 'name' | 'app' | 'pin' | 'title' | 'settings' | 'bindings'>

const about = (models: string[]): AppAbout => ({
  title: null, description: null, kind: 'app', trust: 'sandbox', roles: [], models: models.flatMap((m) => [{ model: m, entity: 'a', operations: 'read' }, { model: m, entity: 'b', operations: 'read' }]),
  semantic_models: [], egress: [], storage: null, published_by: 'ada', published_at: '2026-11-01T10:00:00Z', approval: null,
})
const version = (n: number, models: string[] | null): AppVersionRow => ({
  version: n, manifest_version: `1.${n}.0`, trust: 'sandbox', published_by: 'ada', published_at: '2026-11-01T10:00:00Z', note: null, serving: false, hash: 'ab'.repeat(32), approval_id: null, files: 1, about: models ? about(models) : null,
})
const detail: Pick<AppDetail, 'serving' | 'versions'> = { serving: 2, versions: [version(2, ['territories', 'people']), version(1, ['territories']), version(3, null)] }

describe('the models of the version an instance would run', () => {
  it('are the serving version\'s when the instance follows, the pinned one\'s when it is pinned, each model once in manifest order', () => {
    expect(modelsOf(detail, '')).toEqual(['territories', 'people'])
    expect(modelsOf(detail, '1')).toEqual(['territories'])
  })

  it('are unknown (null) while the detail is not read, for a version that is not there and for a manifest that does not read', () => {
    expect(modelsOf(undefined, '')).toBeNull()
    expect(modelsOf(detail, '9')).toBeNull()
    expect(modelsOf(detail, '3')).toBeNull()
  })
})

describe('the binding rows', () => {
  it('has a row per model of the version, filled from the bindings, and none for a model the manifest does not list', () => {
    expect(bindingRows(['territories', 'people'], { territories: 'default' })).toEqual([
      { model: 'territories', value: 'default', used: true },
      { model: 'people', value: '', used: true },
    ])
  })

  it('keeps a binding of a model the version does not use as an unused row, so the screen can say that saving drops it', () => {
    expect(bindingRows(['territories'], { territories: 'default', orders: 'live', archive: 'x' }).filter((r) => !r.used).map((r) => r.model)).toEqual(['archive', 'orders'])
  })
})

describe('the instance form', () => {
  it('starts empty for a new instance and from the row for an existing one', () => {
    expect(emptyInstanceForm('notes')).toEqual({ name: '', app: 'notes', pin: '', title: '', settings: [], bindings: {}, addresses: [] })
    expect(formFromRow(row)).toEqual({ name: 'acme', app: 'notes', pin: '', title: 'Acme notes', settings: [{ key: 'portal', value: 'front' }], bindings: { territories: 'default' }, addresses: [] })
    expect(formFromRow({ ...row, pin: 2, title: null }).pin).toBe('2')
    expect(formFromRow({ ...row, pin: 2, title: null }).title).toBe('')
  })

  it('has a problem only where the call would mean nothing; names, limits and values are the Gateway\'s to refuse', () => {
    const ok = { ...emptyInstanceForm('notes'), name: 'Sales Team' }                  // not a valid name: the Gateway says so
    expect(instanceFormProblem(ok, true, [])).toBeNull()
    expect(instanceFormProblem({ ...ok, name: '' }, true, [])).toContain('Name the instance')
    expect(instanceFormProblem(ok, true, ['Sales Team'])).toContain('already an instance')
    expect(instanceFormProblem(ok, false, ['Sales Team'])).toBeNull()                // editing the one that exists
    expect(instanceFormProblem({ ...ok, app: '' }, true, [])).toContain('Choose the app')
    expect(instanceFormProblem({ ...ok, settings: [{ key: 'x', value: '' }, { key: 'x ', value: 'y' }] }, true, [])).toContain('listed twice')
  })
})

describe('the addresses of an instance (plan 26 AP08)', () => {
  const address = { host: 'sales.contoso.com', kind: 'custom' as const, path: '/', primary: false, certificate: 'secret:cert-sales', certificate_password: null }

  it('reads the instance row into the form and writes the form back as the request, with no certificate for a subdomain', () => {
    const form = formFromRow({ ...row, addresses: [address, { host: 'acme.apps.example.com', kind: 'subdomain', path: '/', primary: true, certificate: null, certificate_password: null }] })
    expect(form.addresses).toEqual([
      { host: 'sales.contoso.com', kind: 'custom', path: '/', primary: false, certificate: 'secret:cert-sales', certificatePassword: '' },
      { host: 'acme.apps.example.com', kind: 'subdomain', path: '/', primary: true, certificate: '', certificatePassword: '' },
    ])
    expect(instanceRequest(form, []).addresses).toEqual([address, { host: 'acme.apps.example.com', kind: 'subdomain', path: '/', primary: true, certificate: null, certificate_password: null }])
    // a blank path is the root, and a password reference travels with its certificate
    expect(instanceRequest({ ...form, addresses: [{ ...form.addresses[0]!, path: ' ', certificatePassword: ' secret:pw ' }] }, []).addresses).toEqual([{ ...address, certificate_password: 'secret:pw' }])
  })

  it('has a problem only for what makes the call meaningless: no name, a custom domain with no certificate, one name twice, two primaries', () => {
    const ok = { ...emptyInstanceForm('notes'), name: 'sales' }
    const mine = formFromRow({ ...row, addresses: [address] }).addresses[0]!
    expect(instanceFormProblem({ ...ok, addresses: [mine] }, true, [])).toBeNull()
    expect(instanceFormProblem({ ...ok, addresses: [{ ...mine, host: ' ' }] }, true, [])).toContain('needs a name')
    expect(instanceFormProblem({ ...ok, addresses: [{ ...mine, certificate: '' }] }, true, [])).toContain('needs its certificate')
    expect(instanceFormProblem({ ...ok, addresses: [{ ...mine, kind: 'subdomain', certificate: '' }] }, true, [])).toBeNull()
    expect(instanceFormProblem({ ...ok, addresses: [mine, { ...mine, host: 'SALES.contoso.com' }] }, true, [])).toContain('listed twice')
    expect(instanceFormProblem({ ...ok, addresses: [{ ...mine, primary: true }, { ...mine, host: 'b.contoso.com', primary: true }] }, true, [])).toContain('one address is the primary')
  })

  it('moving the pin leaves the addresses out, so the Gateway keeps them', () => {
    expect('addresses' in pinRequest({ ...row, addresses: [address] } as typeof row, 2)).toBe(false)
  })
})

describe('the request of the form', () => {
  it('sends the app, the pin as a number or null, a blank title as null, settings as given and only the bindings that are filled for a model the version uses', () => {
    const form = {
      name: 'sales', app: 'notes', pin: '1', title: '  ', settings: [{ key: ' embed.allowed_origins ', value: 'https://portal.example.com' }, { key: 'theme', value: '' }],
      bindings: { territories: ' default ', people: '', orders: 'live' }, addresses: [],
    }
    expect(instanceRequest(form, ['territories', 'people'])).toEqual({
      app: 'notes', pin: 1, title: null, settings: { 'embed.allowed_origins': 'https://portal.example.com', theme: '' }, bindings: { territories: 'default' }, addresses: [],
    })
    expect(instanceRequest({ ...form, pin: '', title: ' Sales ' }, []).pin).toBeNull()
    expect(instanceRequest({ ...form, pin: '', title: ' Sales ' }, []).title).toBe('Sales')
  })

  it('moving the pin sends the title, the settings and the bindings back unchanged (the Gateway replaces the whole instance)', () => {
    expect(pinRequest(row, 2)).toEqual({ app: 'notes', pin: 2, title: 'Acme notes', settings: { portal: 'front' }, bindings: { territories: 'default' } })
    expect(pinRequest({ ...row, title: null }, null)).toEqual({ app: 'notes', pin: null, title: null, settings: { portal: 'front' }, bindings: { territories: 'default' } })
    expect(pinRequest({ app: 'notes', title: null, settings: undefined as unknown as Record<string, string>, bindings: {} }, 1).settings).toEqual({})
  })
})

describe('the configuration line of the list', () => {
  it('names the settings, then the bindings, and nothing for an instance with neither', () => {
    expect(configurationLabel(row)).toBe('portal=front, territories → default')
    expect(configurationLabel({ settings: {}, bindings: {} })).toBe('')
  })
})

// ---- UE02 ----

const bytesOf = (text: string) => new TextEncoder().encode(text)
const fromBase64 = (text: string) => Uint8Array.from(atob(text), (c) => c.charCodeAt(0))
const textOf = (text: string) => new TextDecoder().decode(fromBase64(text))
const file = (path: string, text: string | Uint8Array): PickedFile => ({ path, bytes: typeof text === 'string' ? bytesOf(text) : text })
const manifestText = 'schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\n'

describe('base64', () => {
  it('is the same as the platform\'s for bytes of every value, across the chunk the encoder works in', () => {
    const bytes = Uint8Array.from({ length: 0x8000 * 2 + 17 }, (_, i) => (i * 7) % 256)
    expect([...fromBase64(toBase64(bytes))]).toEqual([...bytes])
    expect(toBase64(bytesOf('Many hands make light work.'))).toBe('TWFueSBoYW5kcyBtYWtlIGxpZ2h0IHdvcmsu')
    expect(toBase64(new Uint8Array(0))).toBe('')
  })
})

describe('the package a folder becomes', () => {
  it('is the manifest as text and every other file under its path inside the folder as base64, as apps publish --dir sends them', () => {
    const prepared = prepareFolder([
      file('notes/app.yaml', manifestText), file('notes/index.html', '<html>one</html>'), file('notes/src/main.js', 'run()'), file('notes/views/home.yaml', 'a: 1'),
    ])
    expect(prepared.ok).toBe(true)
    if (!prepared.ok) return
    expect(prepared.package.source).toBe('notes')
    expect(prepared.package.manifest).toBe(manifestText)
    expect(Object.keys(prepared.package.files).sort()).toEqual(['index.html', 'src/main.js', 'views/home.yaml'])           // no app.yaml among the files; folders kept
    expect(textOf(prepared.package.files['index.html']!)).toBe('<html>one</html>')
    expect(prepared.package.fileCount).toBe(3)
    expect(prepared.package.bytes).toBe('<html>one</html>'.length + 'run()'.length + 'a: 1'.length)
  })

  it('keeps binary files byte for byte and drops a byte order mark from the manifest, as reading the file as text does', () => {
    const png = Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x00, 0xff, 0xfe, 0x80])
    const withBom = Uint8Array.from([0xef, 0xbb, 0xbf, ...bytesOf(manifestText)])
    const prepared = prepareFolder([file('n/app.yaml', withBom), file('n/logo.png', png)])
    expect(prepared.ok && prepared.package.manifest).toBe(manifestText)
    expect(prepared.ok && [...fromBase64(prepared.package.files['logo.png']!)]).toEqual([...png])
  })

  it('names the folder when it has no app.yaml at its top, and the one to choose when the manifest is a level down', () => {
    const none = prepareFolder([file('notes/index.html', 'x')])
    expect(none).toEqual({ ok: false, problem: expect.stringContaining('There is no app.yaml in notes') })
    const deeper = prepareFolder([file('projects/notes/app.yaml', manifestText), file('projects/notes/index.html', 'x')])
    expect(deeper.ok).toBe(false)
    expect(!deeper.ok && deeper.problem).toContain('There is no app.yaml at the top of projects')
    expect(!deeper.ok && deeper.problem).toContain('choose that folder instead')
    expect(!deeper.ok && deeper.problem).toContain('projects/notes')
    expect(prepareFolder([])).toEqual({ ok: false, problem: expect.stringContaining('Choose the folder') })
  })

  it('refuses more than the Gateway accepts, naming the size, before anything is sent', () => {
    const big = prepareFolder([file('n/app.yaml', manifestText), file('n/data.bin', new Uint8Array(MAX_PACKAGE_BYTES + 1))])
    expect(big.ok).toBe(false)
    expect(!big.ok && big.problem).toContain('17 MB')
    expect(!big.ok && big.problem).toContain('at most 16 MB')
    expect(prepareFolder([file('n/app.yaml', manifestText), file('n/data.bin', new Uint8Array(MAX_PACKAGE_BYTES))]).ok).toBe(true)
  })

  it('takes files that are not in a folder as they are (a single file picked, the manifest among them)', () => {
    const prepared = prepareFolder([file('app.yaml', manifestText), file('index.html', 'x')])
    expect(prepared.ok && Object.keys(prepared.package.files)).toEqual(['index.html'])
    expect(prepared.ok && prepared.package.source).toBe('the chosen files')
  })

  it('makes a package of a pasted manifest alone, and says when nothing is pasted', () => {
    expect(prepareManifest('  ')).toEqual({ ok: false, problem: expect.stringContaining('Paste the manifest') })
    const pasted = prepareManifest(manifestText)
    expect(pasted.ok && pasted.package).toMatchObject({ manifest: manifestText, files: {}, fileCount: 0 })
  })

  it('sends the note only when there is one', () => {
    const prepared = prepareManifest(manifestText)
    if (!prepared.ok) throw new Error('unreachable')
    expect(publishBody(prepared.package, '  ')).toEqual({ manifest: manifestText, files: {} })
    expect(publishBody(prepared.package, ' second build ')).toEqual({ manifest: manifestText, files: {}, note: 'second build' })
  })

  it('says sizes in words', () => {
    expect([sizeLabel(12), sizeLabel(2048), sizeLabel(5 * 1024 * 1024)]).toEqual(['12 bytes', '2.0 KB', '5.0 MB'])
  })
})

const caller = (over: Partial<AppsCaller> = {}): AppsCaller => ({ can_publish: true, can_approve: true, you: 'Ada', ...over })
const okPrepared = prepareManifest(manifestText)
const aboutOf = (egress: AppAbout['egress']): AppAbout => ({ ...about(['territories']), trust: 'trusted', egress })
const check = (over: Partial<AppCheck> = {}): AppCheck => ({
  ok: true, diagnostics: [], files: 1, name: 'notes', manifest_version: '1.0.0', trust: 'sandbox', hash: 'cd'.repeat(32), approval_id: null, next_version: 3, about: null, ...over,
})

describe('why Publish is off', () => {
  it('needs the role, a chosen folder that is a package, the Gateway\'s check, no errors, and for a trusted app an approval of these exact files', () => {
    expect(publishBlock(caller({ can_publish: false }), okPrepared, check(), false)).toContain('publisher or administrator role')
    expect(publishBlock(caller(), null, null, false)).toContain('Choose the folder')
    expect(publishBlock(caller(), { ok: false, problem: 'There is no app.yaml in notes.' }, null, false)).toBe('There is no app.yaml in notes.')
    expect(publishBlock(caller(), okPrepared, null, true)).toContain('Checking')
    expect(publishBlock(caller(), okPrepared, null, false)).toContain('Checking')
    expect(publishBlock(caller(), okPrepared, check({ ok: false }), false)).toContain('errors in these files')
    expect(publishBlock(caller(), okPrepared, check({ trust: 'trusted' }), false)).toContain('asks to be trusted and no approval stands')
    expect(publishBlock(caller(), okPrepared, check({ trust: 'trusted' }), false)).toContain('cdcdcdcdcdcd')
    expect(publishBlock(caller(), okPrepared, check({ trust: 'trusted', approval_id: 'apr_1' }), false)).toBeNull()
    expect(publishBlock(caller(), okPrepared, check(), false)).toBeNull()
  })
})

describe('approving exact files', () => {
  const trusted = check({ trust: 'trusted' })
  it('needs the Approver role, a trusted app with no approval, a named approver, and one who is not the person publishing', () => {
    expect(packageApproveBlock(caller({ can_approve: false }), trusted, 'Grace')).toContain('Approver role')
    expect(packageApproveBlock(caller(), null, 'Grace')).toContain('Check the files first')
    expect(packageApproveBlock(caller(), check(), 'Grace')).toContain('sandboxed')
    expect(packageApproveBlock(caller(), check({ trust: 'trusted', approval_id: 'apr_9' }), 'Grace')).toContain('apr_9')
    expect(packageApproveBlock(caller(), trusted, ' ')).toContain('Name the approver')
    expect(packageApproveBlock(caller(), trusted, 'ada')).toContain('two people')
    expect(packageApproveBlock(caller(), trusted, 'Grace Hopper')).toBeNull()
  })
})

describe('the outside origins an approver chooses', () => {
  const egress = [{ directive: 'connect', origins: ['https://api.example.com', 'wss://live.example.com'] }, { directive: 'img', origins: ['https://cdn.example.com'] }]
  it('are the origins the manifest asks for, each one a choice, and none when it asks for none', () => {
    expect(egressChoices(aboutOf(egress)).map((c) => c.id)).toEqual(['connect=https://api.example.com', 'connect=wss://live.example.com', 'img=https://cdn.example.com'])
    expect(egressChoices(aboutOf([]))).toEqual([])
    expect(egressChoices(null)).toEqual([])
  })

  it('make the approval\'s egress list, which is empty (allows none) until one is ticked', () => {
    expect(egressParam([])).toBeUndefined()
    expect(egressParam(['connect=https://api.example.com', 'img=https://cdn.example.com'])).toBe('connect=https://api.example.com,img=https://cdn.example.com')
    expect(toggled([], 'a')).toEqual(['a'])
    expect(toggled(['a', 'b'], 'a')).toEqual(['b'])
  })
})

// ---- UE03 ----

const set: NavigationSetRow = { name: 'main', title: 'Main menu', entries: [{ instance: 'acme', label: 'Acme', icon: null }, { instance: 'pinned', label: null, icon: 'star' }] }
const entry = (instance: string) => ({ instance, label: '', icon: '' })

describe('the navigation set form', () => {
  it('starts empty for a new set and from the set for one that exists, blank labels and icons for none', () => {
    expect(emptyNavForm()).toEqual({ name: '', title: '', entries: [], original: null })
    expect(navFormFromSet(set)).toEqual({ name: 'main', title: 'Main menu', entries: [{ instance: 'acme', label: 'Acme', icon: '' }, { instance: 'pinned', label: '', icon: 'star' }], original: 'main' })
  })

  it('moves a member one place, and leaves the ends where they are', () => {
    const three = ['a', 'b', 'c'].map(entry)
    expect(moveEntry(three, 0, 1).map((e) => e.instance)).toEqual(['b', 'a', 'c'])
    expect(moveEntry(three, 2, -1).map((e) => e.instance)).toEqual(['a', 'c', 'b'])
    expect(moveEntry(three, 0, -1)).toBe(three)
    expect(moveEntry(three, 2, 1)).toBe(three)
    expect(moveEntry(three, 5, 1)).toBe(three)
    expect(three.map((e) => e.instance)).toEqual(['a', 'b', 'c'])             // the list given is never changed
  })

  it('offers each instance that exists and is not listed, once, by name', () => {
    expect(availableMembers(['sales', 'acme', 'pinned'], [entry('acme')])).toEqual(['pinned', 'sales'])
    expect(availableMembers([], [entry('acme')])).toEqual([])
    expect(availableMembers(['acme'], [entry('acme')])).toEqual([])
  })

  it('has a problem only where saving means nothing, or would replace another set unasked', () => {
    const form = { ...navFormFromSet(set), name: ' crew ' }
    expect(navFormProblem(form, ['main'])).toBeNull()
    expect(navFormProblem({ ...form, name: '  ' }, [])).toContain('Name the set')
    expect(navFormProblem({ ...emptyNavForm(), name: 'main', title: 'T', entries: [entry('a')] }, ['main'])).toContain('already a navigation set named main')
    expect(navFormProblem(navFormFromSet(set), ['main'])).toBeNull()                       // changing the set that exists
    expect(navFormProblem({ ...form, name: 'main' }, ['main', 'crew'])).toBeNull()           // renamed back to its own name
    expect(navFormProblem({ ...form, name: 'other' }, ['main', 'other'])).toContain('already a navigation set named other')
    expect(navFormProblem({ ...form, title: ' ' }, [])).toContain('Give the set a title')
    expect(navFormProblem({ ...form, entries: [] }, [])).toContain('at least one instance')
  })

  it('sends the title, the members in order with the label and icon or null, and rename_from only when the name changed', () => {
    const form = navFormFromSet(set)
    expect(navRequest(form)).toEqual({ title: 'Main menu', entries: [{ instance: 'acme', label: 'Acme', icon: null }, { instance: 'pinned', label: null, icon: 'star' }] })
    expect(isRename(form)).toBe(false)
    const renamed = { ...form, name: ' crew ', title: ' Crew ', entries: [{ instance: 'pinned', label: '  ', icon: ' ' }, entry('acme')] }
    expect(isRename(renamed)).toBe(true)
    expect(navRequest(renamed)).toEqual({ title: 'Crew', entries: [{ instance: 'pinned', label: null, icon: null }, { instance: 'acme', label: null, icon: null }], rename_from: 'main' })
    expect(navRequest({ ...emptyNavForm(), name: 'x', title: 'X', entries: [entry('a')] })).not.toHaveProperty('rename_from')
  })

  it('says the members as the list shows them', () => {
    expect(entriesLabel(set)).toBe('acme (Acme) → pinned')
  })
})
