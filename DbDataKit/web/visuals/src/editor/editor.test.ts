import { describe, expect, it } from 'vitest'
import { parse } from 'yaml'
import { fixtureClient } from '../client/fixtureClient'
import { ViewsClientError } from '../client/types'
import { sampleBundle } from '../testing/sampleBundle'
import type { ViewsDoc } from '../views/model'
import { canonicalText } from './canonical'
import { EditError } from './commands'
import { canRedo, canUndo, draftFromOpened, edit, editAll, editText, errorsOf, format, isDirty, newDraft, redo, undo } from './draft'
import { diagnosticsFor, formTarget, markersOf } from './diagnostics'
import { clientStore, localFileStore, reloadDraft, saveDraft } from './store'
import { newDocumentText } from './newDocument'
import { storedDocuments } from './storedDocuments'

const bundle = sampleBundle()
const TODAY = bundle.documents['dashboard/retail-sales-today']!
const MONTHLY = bundle.documents['report/retail-sales-monthly']!
const open = async (store = localFileStore(bundle.documents), kind: 'dashboard' | 'report' = 'dashboard', name = 'retail-sales-today') => ({ store, draft: draftFromOpened(await store.open(kind, name)) })
const must = (r: ReturnType<typeof edit>) => { expect(r.error?.message).toBeUndefined(); return r.draft }
const value = (text: string) => parse(text) as ViewsDoc

describe('the canonical text', () => {
  it.each([['dashboard', TODAY], ['report', MONTHLY]])('round-trips the sample %s: the same document, and the same text when written again', (_k, text) => {
    const once = canonicalText(parse(text))
    expect(parse(once)).toEqual(parse(text))
    expect(canonicalText(parse(once))).toBe(once)
  })

  it('puts keys in the order a person reads them, tiles on one line, and keeps a multi-line text as a block', () => {
    const canonical = canonicalText({ layouts: [{ tiles: [{ h: 2, w: 3, y: 1, x: 0, widget: 'net' }], type: 'reflow', grid: { columns: 12 }, name: 'desk', default: true }], widgets: { n: { query: { model: 'm' }, encodings: { value: 'a.b' }, type: 'metric' }, t: { type: 'text', text: 'one\ntwo\n' } }, title: 'T', kind: 'dashboard', schemaVersion: 1 })
    expect(canonical.indexOf('schemaVersion')).toBeLessThan(canonical.indexOf('kind'))
    expect(canonical.indexOf('title')).toBeLessThan(canonical.indexOf('widgets'))
    expect(canonical).toContain('- {widget: net, x: 0, y: 1, w: 3, h: 2}')
    expect(canonical).toMatch(/name: desk\n {4}type: reflow\n {4}default: true\n {4}grid: \{columns: 12\}/)
    expect(canonical).toMatch(/text: \|\n/)
  })
})

describe('the draft', () => {
  it('opens each sample document clean and dirty only after an edit; the text is untouched until then', async () => {
    for (const [kind, name, text] of [['dashboard', 'retail-sales-today', TODAY], ['report', 'retail-sales-monthly', MONTHLY]] as const) {
      const { draft } = await open(undefined, kind, name)
      expect(draft.text).toBe(text)
      expect(isDirty(draft)).toBe(false)
      expect(draft.validation.ok).toBe(true)
      expect(canUndo(draft)).toBe(false)
    }
  })

  it('an agent-written file (other key order, comments, quoting) opens and saved with no edit is byte for byte unchanged', async () => {
    const agent = `# written by an agent
title: "Agent board"
kind: dashboard
schemaVersion: 1
layouts:
  - {tiles: [{widget: a, x: 0, y: 0, w: 6, h: 2}], grid: {columns: 12}, type: reflow, default: true, name: desk}
widgets:
  a: {type: text, text: "hello"}   # a note
`
    const store = localFileStore({ 'dashboard/agent': agent })
    const draft = draftFromOpened(await store.open('dashboard', 'agent'))
    expect(draft.validation.ok).toBe(true)
    const saved = await saveDraft(store, draft)
    expect(saved.status).toBe('saved')
    expect((await store.open('dashboard', 'agent')).text).toBe(agent)
    // formatting is the one thing that reorders, and the document is the same
    const formatted = must(format(draft))
    expect(value(formatted.text)).toEqual(value(agent))
    expect(formatted.text).not.toContain('#')
    expect(formatted.text.indexOf('schemaVersion')).toBeLessThan(formatted.text.indexOf('title'))
  })

  it('an edit through a command keeps the comments, the order and the style of everything else', async () => {
    const { draft } = await open()
    const next = must(edit(draft, { type: 'setTitle', title: 'Today, retail' }))
    expect(next.text).toBe(TODAY.replace('title: Retail sales today', 'title: Today, retail'))
    expect(next.text).toContain('# A dashboard over the semantic model')
    expect(isDirty(next)).toBe(true)
    expect(next.value?.title).toBe('Today, retail')
  })

  it('undo restores the text and redo takes it again; a new edit clears redo; one group is one step', async () => {
    const { draft } = await open()
    let d = must(edit(draft, { type: 'setTitle', title: 'A' }))
    d = must(edit(d, { type: 'setTitle', title: 'B' }))
    expect(d.undoStack).toHaveLength(2)
    d = undo(d)
    expect(d.value?.title).toBe('A')
    expect(canRedo(d)).toBe(true)
    d = undo(d)
    expect(d.text).toBe(TODAY)
    expect(isDirty(d)).toBe(false)
    d = redo(d)
    expect(d.value?.title).toBe('A')
    d = must(edit(d, { type: 'setTitle', title: 'C' }))
    expect(canRedo(d)).toBe(false)
    // a drag is many commands in one group and one undo step
    let g = draft
    for (const x of [1, 2, 3, 4]) g = must(edit(g, { type: 'setTile', layout: 'desk', widget: 'net', changes: { x } }, 'drag:net'))
    expect(g.undoStack).toHaveLength(1)
    expect(undo(g).text).toBe(TODAY)
    // typing in the text view is one step per group too
    let t = editText(draft, TODAY + '\n# a', 'text')
    t = editText(t, TODAY + '\n# ab', 'text')
    expect(t.undoStack).toHaveLength(1)
    expect(editText(t, t.text, 'text')).toBe(t)
  })

  it('an invalid edit gives the diagnostic at the right path and line, mapped to a form path and a marker', async () => {
    const { draft } = await open()
    const broken = must(edit(draft, { type: 'setPath', path: ['layouts', 0, 'tiles', 1, 'x'], value: 11 }))
    expect(broken.validation.ok).toBe(false)
    const [error] = errorsOf(broken)
    expect(error!.path).toBe('layouts[0].tiles[1]')
    const lines = broken.text.split('\n')
    expect(lines[error!.line! - 1]).toContain('widget: net')
    expect(formTarget(error!.path)).toEqual({ area: 'layout', index: 0, tile: 1, field: '' })
    expect(diagnosticsFor(broken.validation.diagnostics, { area: 'layout', index: 0, tile: 1 })).toHaveLength(1)
    expect(diagnosticsFor(broken.validation.diagnostics, { area: 'layout', index: 1 })).toHaveLength(0)
    expect(markersOf([error!])[0]).toMatchObject({ line: error!.line, column: error!.column, severity: 'error' })
    // the draft keeps its value so a form can show the problem beside the field; undo repairs it
    expect(broken.value?.layouts).toHaveLength(4)
    expect(undo(broken).validation.ok).toBe(true)
  })

  it('maps every kind of path to the part of the form it belongs to', () => {
    expect(formTarget('title')).toEqual({ area: 'document', field: 'title' })
    expect(formTarget('widgets.net.encodings.value')).toEqual({ area: 'widget', id: 'net', field: 'encodings.value' })
    expect(formTarget('parameters[0].default')).toEqual({ area: 'parameter', index: 0, field: 'default' })
    expect(formTarget('layouts[2].when.width')).toEqual({ area: 'layout', index: 2, field: 'when.width' })
    expect(formTarget('')).toEqual({ area: 'document', field: '' })
  })

  it('text that is not YAML has a diagnostic with a place and no value; a form command is refused and nothing changes', async () => {
    const { draft } = await open()
    const bad = editText(draft, 'title: [unclosed\nwidgets: {')
    expect(bad.value).toBeUndefined()
    expect(bad.validation.ok).toBe(false)
    expect(bad.validation.diagnostics[0]!.line).toBeGreaterThan(0)
    const refused = edit(bad, { type: 'setTitle', title: 'x' })
    expect(refused.error?.message).toMatch(/YAML view/)
    expect(refused.draft).toBe(bad)
    expect(format(bad).error).toBeDefined()
  })

  it('a form change and a text change meet in one text: edit the title in the text, then a tile by command', async () => {
    const { draft } = await open()
    let d = editText(draft, TODAY.replace('title: Retail sales today', 'title: Typed'))
    d = must(edit(d, { type: 'setTile', layout: 'desk', widget: 'net', changes: { w: 4 } }))
    expect(d.value?.title).toBe('Typed')
    expect(d.text).toContain('{widget: net, x: 0, y: 1, w: 4, h: 2}')
  })
})

describe('the edit commands', () => {
  const run = async (...commands: Parameters<typeof edit>[1][]) => {
    const { draft } = await open()
    const result = editAll(draft, commands)
    return result
  }

  it('adds a widget, places it, and removes it with its tiles in every layout', async () => {
    const note = { type: 'text' as const, text: 'hi' }
    const placed = must(await run({ type: 'addWidget', id: 'hello', widget: note }, { type: 'placeTile', layout: 'desk', tile: { widget: 'hello', x: 0, y: 8, w: 12, h: 1 } }, { type: 'placeTile', layout: 'phone', tile: { widget: 'hello', x: 10, y: 890, w: 100, h: 5 } }))
    expect(placed.value?.widgets.hello).toEqual(note)
    expect((placed.value!.layouts[0] as { tiles: unknown[] }).tiles).toHaveLength(7)
    const removed = must(edit(placed, { type: 'removeWidget', id: 'hello' }))
    expect(value(removed.text)).toEqual(value(TODAY))
    // removing a widget the flow layout places removes the block and the cell too
    const gone = must(await run({ type: 'removeWidget', id: 'net' }))
    const print = gone.value!.layouts[3] as unknown as { sections: Array<{ blocks: Array<{ widget?: string; row?: Array<{ widget: string }> }> }> }
    expect(print.sections[0]!.blocks[0]!.row).toEqual([{ widget: 'margin', span: 6 }])
    expect(JSON.stringify(gone.value!.layouts.slice(0, 3))).not.toContain('"net"')
    expect(gone.validation.ok).toBe(true)
  })

  it('renames a widget everywhere it is named, keeping its place in the file', async () => {
    const d = must(await run({ type: 'renameWidget', id: 'by_region', to: 'sales_by_region' }))
    expect(d.validation.ok).toBe(true)
    expect(Object.keys(d.value!.widgets)).toEqual(['regions', 'net', 'margin', 'note', 'sales_by_region', 'top_products'])
    expect(d.text).not.toMatch(/(^|[^_])by_region/)
    expect(d.text.match(/sales_by_region/g)).toHaveLength(1 + 4)
  })

  it('refuses what it cannot do, with a sentence, and leaves the draft as it was', async () => {
    const { draft } = await open()
    const codes = (c: Parameters<typeof edit>[1]) => (edit(draft, c).error as EditError).code
    expect(codes({ type: 'addWidget', id: 'net', widget: { type: 'text', text: '' } })).toBe('name_taken')
    expect(codes({ type: 'addWidget', id: '1 bad', widget: { type: 'text', text: '' } })).toBe('bad_name')
    expect(codes({ type: 'removeWidget', id: 'nope' })).toBe('unknown_widget')
    expect(codes({ type: 'renameWidget', id: 'net', to: 'margin' })).toBe('name_taken')
    expect(codes({ type: 'placeTile', layout: 'desk', tile: { widget: 'net', x: 0, y: 0, w: 1, h: 1 } })).toBe('already_placed')
    expect(codes({ type: 'placeTile', layout: 'print', tile: { widget: 'net', x: 0, y: 0, w: 1, h: 1 } })).toBe('not_placeable')
    expect(codes({ type: 'setTile', layout: 'desk', widget: 'nope', changes: { x: 1 } })).toBe('not_placeable')
    expect(codes({ type: 'deleteLayout', name: 'nope' })).toBe('unknown_layout')
    expect(codes({ type: 'removeParameter', name: 'nope' })).toBe('unknown_parameter')
    expect((edit(draft, { type: 'removeWidget', id: 'nope' }).error as EditError).message).toMatch(/`regions`, `net`/)
    expect(edit(draft, { type: 'removeWidget', id: 'nope' }).draft).toBe(draft)
  })

  it('manages layouts: add, duplicate, rename, move, default, delete (the default passes on)', async () => {
    let d = must(await run({ type: 'duplicateLayout', name: 'desk', as: 'desk2' }))
    expect(d.value!.layouts.map((l) => l.name)).toEqual(['desk', 'desk2', 'wall', 'phone', 'print'])
    expect(d.value!.layouts[1]!.default).toBeUndefined()
    d = must(edit(d, { type: 'setDefaultLayout', name: 'desk2' }))
    expect(d.value!.layouts.filter((l) => l.default).map((l) => l.name)).toEqual(['desk2'])
    expect(d.validation.ok).toBe(true)
    d = must(edit(d, { type: 'renameLayout', name: 'desk2', to: 'main' }))
    d = must(edit(d, { type: 'moveLayout', name: 'main', to: 0 }))
    expect(d.value!.layouts.map((l) => l.name)).toEqual(['main', 'desk', 'wall', 'phone', 'print'])
    d = must(edit(d, { type: 'deleteLayout', name: 'main' }))
    expect(d.value!.layouts.filter((l) => l.default).map((l) => l.name)).toEqual(['desk'])
    expect(d.validation.ok).toBe(true)
    const only = newDraft('dashboard', 'x', newDocumentText('dashboard', 'X'))
    expect((edit(only, { type: 'deleteLayout', name: 'desk' }).error as EditError).code).toBe('last_layout')
    const added = must(edit(d, { type: 'addLayout', layout: { name: 'tv', type: 'canvas', canvas: { width: 1920, height: 1080 }, tiles: [] } }))
    expect(added.value!.layouts.at(-1)!.name).toBe('tv')
    expect(added.text).toContain('canvas: {width: 1920, height: 1080}')
  })

  it('manages parameters; the last one removed takes the key with it', async () => {
    let d = must(await run({ type: 'addParameter', parameter: { name: 'year', type: 'integer', default: 2026 } }))
    expect(d.value!.parameters!.map((p) => p.name)).toEqual(['region', 'year'])
    d = must(edit(d, { type: 'removeParameter', name: 'year' }))
    expect(value(d.text)).toEqual(value(TODAY))
    const { draft } = await open()
    const stripped = editAll(draft, [{ type: 'removeWidget', id: 'regions' }, { type: 'removePath', path: ['widgets', 'net', 'query', 'filters'] }, { type: 'removePath', path: ['widgets', 'margin', 'query', 'filters'] }, { type: 'removePath', path: ['widgets', 'top_products', 'query', 'filters'] }, { type: 'removeParameter', name: 'region' }])
    expect(stripped.error).toBeUndefined()
    expect(stripped.draft.value!.parameters).toBeUndefined()
  })

  it('a batch is one undo step and a failing batch changes nothing', async () => {
    const { draft } = await open()
    const batch = editAll(draft, [{ type: 'setTitle', title: 'A' }, { type: 'setTile', layout: 'desk', widget: 'net', changes: { w: 2 } }])
    expect(batch.draft.undoStack).toHaveLength(1)
    const bad = editAll(draft, [{ type: 'setTitle', title: 'A' }, { type: 'removeWidget', id: 'nope' }])
    expect(bad.error).toBeDefined()
    expect(bad.draft).toBe(draft)
  })

  it('a tile change sets, removes and skips unchanged keys', async () => {
    const d = must(await run({ type: 'setTile', layout: 'desk', widget: 'net', changes: { x: 0, w: 4, min: { w: 2 } } }))
    expect(d.text).toContain('{widget: net, x: 0, y: 1, w: 4, h: 2, min: {w: 2}}')
    const e = must(edit(d, { type: 'setTile', layout: 'desk', widget: 'net', changes: { min: undefined } }))
    expect(e.text).toContain('{widget: net, x: 0, y: 1, w: 4, h: 2}')
  })
})

describe('new documents', () => {
  it('a new dashboard starts with an empty desk reflow layout, dirty, with no version; it lacks a widget and a tile', () => {
    const d = newDraft('dashboard', 'sales', newDocumentText('dashboard', 'Sales'))
    expect(errorsOf(d).map((e) => e.path)).toEqual(['widgets', 'layouts[0].tiles'])
    expect(d.baseVersion).toBeUndefined()
    expect(isDirty(d)).toBe(true)
    expect(d.value!.layouts).toEqual([{ name: 'desk', type: 'reflow', default: true, grid: { columns: 12 }, tiles: [] }])
  })
  it('a new report starts with a flow layout and a section, and lacks a widget and a block', () => {
    const d = newDraft('report', 'monthly', newDocumentText('report', 'Monthly'))
    expect(errorsOf(d).map((e) => e.path)).toEqual(['widgets', 'layouts[0].sections[0].blocks'])
    expect(d.value!.layouts[0]!.type).toBe('flow')
  })
})

describe('saving', () => {
  it('saves a new document without a version, then a change with the version it was opened at', async () => {
    const store = localFileStore()
    let d = newDraft('dashboard', 'sales', newDocumentText('dashboard', 'Sales'))
    expect((await saveDraft(store, d)).status).toBe('invalid')
    d = must(editAll(d, [{ type: 'addWidget', id: 'hello', widget: { type: 'text', text: 'hi' } }, { type: 'placeTile', layout: 'desk', tile: { widget: 'hello', x: 0, y: 0, w: 12, h: 1 } }]))
    const first = await saveDraft(store, d)
    expect(first.status).toBe('saved')
    d = first.draft
    expect(isDirty(d)).toBe(false)
    expect(d.baseVersion).toBeDefined()
    d = must(edit(d, { type: 'setTitle', title: 'Sales 2' }))
    expect(isDirty(d)).toBe(true)
    const second = await saveDraft(store, d)
    expect(second.status).toBe('saved')
    expect((await store.open('dashboard', 'sales')).document?.title).toBe('Sales 2')
    // saving a new name over an existing one is a conflict, not an overwrite
    const again = await saveDraft(store, { ...newDraft('dashboard', 'sales', newDocumentText('dashboard', 'Other')), text: TODAY, validation: draftFromOpened({ name: 'x', kind: 'dashboard', text: TODAY, version: 'v', document: null, diagnostics: [] }).validation })
    expect(again.status).toBe('conflict')
  })

  it('a draft with errors is refused with the loader text and the store is not asked', async () => {
    const calls: string[] = []
    const store = { ...localFileStore(bundle.documents), save: async () => { calls.push('save'); return { name: 'x', version: 'v' } } }
    const { draft } = await open(store as never)
    const broken = must(edit(draft, { type: 'setPath', path: ['layouts', 0, 'tiles', 1, 'x'], value: 11 }))
    const outcome = await saveDraft(store, broken)
    expect(outcome.status).toBe('invalid')
    if (outcome.status === 'invalid') {
      expect(outcome.message).toContain('not saved')
      expect(outcome.diagnostics[0]!.line).toBeGreaterThan(0)
    }
    expect(calls).toEqual([])
  })

  it('a stale save is refused naming the stored version, and offers what is stored; reload gives it up', async () => {
    const store = localFileStore(bundle.documents)
    const a = draftFromOpened(await store.open('dashboard', 'retail-sales-today'))
    const b = draftFromOpened(await store.open('dashboard', 'retail-sales-today'))
    const savedA = await saveDraft(store, must(edit(a, { type: 'setTitle', title: 'From A' })))
    expect(savedA.status).toBe('saved')
    const mine = must(edit(b, { type: 'setTitle', title: 'From B' }))
    const outcome = await saveDraft(store, mine)
    expect(outcome.status).toBe('conflict')
    if (outcome.status !== 'conflict') return
    expect(outcome.storedVersion).toBe(savedA.draft.baseVersion)
    expect(outcome.message).toContain(outcome.storedVersion!)
    expect(outcome.stored?.document?.title).toBe('From A')
    expect(isDirty(outcome.draft)).toBe(true)
    const reloaded = await reloadDraft(store, mine)
    expect(reloaded.value?.title).toBe('From A')
    expect(isDirty(reloaded)).toBe(false)
  })

  it('works over the fixture client: save, reload, equal; duplicate and delete', async () => {
    const store = clientStore(fixtureClient(bundle))
    const draft = draftFromOpened(await store.open('dashboard', 'retail-sales-today'))
    const edited = must(edit(draft, { type: 'setTitle', title: 'Changed' }))
    const saved = await saveDraft(store, edited)
    expect(saved.status).toBe('saved')
    expect((await store.open('dashboard', 'retail-sales-today')).text).toBe(edited.text)
    await store.duplicate('dashboard', 'retail-sales-today', 'copy')
    expect((await store.open('dashboard', 'copy')).text).toBe(edited.text)
    expect((await store.list('dashboard')).map((d) => d.name)).toEqual(['copy', 'retail-sales-today'])
    await store.delete('dashboard', 'copy')
    await expect(store.open('dashboard', 'copy')).rejects.toBeInstanceOf(ViewsClientError)
    await expect(store.delete('dashboard', 'copy')).rejects.toMatchObject({ code: 'not_found' })
  })

  it('the local file store duplicates, refuses a taken name, and hands a file to download', async () => {
    const store = localFileStore(bundle.documents)
    await store.duplicate('dashboard', 'retail-sales-today', 'copy')
    await expect(store.duplicate('dashboard', 'retail-sales-today', 'copy')).rejects.toMatchObject({ code: 'version_conflict' })
    expect(store.fileOf('dashboard', 'copy')).toMatchObject({ filename: 'copy.yaml', text: TODAY })
    await store.delete('dashboard', 'copy')
    expect(() => store.fileOf('dashboard', 'copy')).toThrow(/no dashboard/)
  })
})

describe('stored documents (the console keeps saves across a reload)', () => {
  const fakeStorage = () => { const m = new Map<string, string>(); return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v) } } }
  it('overlays saves and deletes on the bundle, and a second reader sees what the first saved', async () => {
    const storage = fakeStorage()
    const a = fixtureClient(bundle, { documents: storedDocuments(bundle.documents, storage) })
    const b = fixtureClient(bundle, { documents: storedDocuments(bundle.documents, storage) })
    const opened = await a.open('dashboard', 'retail-sales-today')
    await a.save('dashboard', 'retail-sales-today', opened.text.replace('Retail sales today', 'Kept'), opened.version)
    expect((await b.open('dashboard', 'retail-sales-today')).document?.title).toBe('Kept')
    await expect(b.save('dashboard', 'retail-sales-today', opened.text, opened.version)).rejects.toMatchObject({ code: 'version_conflict' })
    await a.delete!('dashboard', 'retail-sales-today')
    expect((await b.list('dashboard')).map((d) => d.name)).toEqual([])
    // a fresh page load reads the same storage
    expect((await fixtureClient(bundle, { documents: storedDocuments(bundle.documents, storage) }).list('report')).map((d) => d.name)).toEqual(['retail-sales-monthly'])
  })
  it('with no storage, or a storage that throws, the visit still works from memory', async () => {
    const broken = { getItem: () => { throw new Error('blocked') }, setItem: () => { throw new Error('blocked') } }
    for (const storage of [null, broken]) {
      const c = fixtureClient(bundle, { documents: storedDocuments(bundle.documents, storage) })
      const opened = await c.open('dashboard', 'retail-sales-today')
      await c.save('dashboard', 'retail-sales-today', opened.text + '# x\n', opened.version)
      expect((await c.open('dashboard', 'retail-sales-today')).text).toContain('# x')
    }
  })
})

describe('style of edited text', () => {
  it('an empty widgets map and tile list written on one line become blocks when they grow, and keep short leaves on one line', async () => {
    const d = newDraft('dashboard', 'x', newDocumentText('dashboard', 'X'))
    expect(d.text).toContain('widgets: {}')
    const grown = must(editAll(d, [
      { type: 'addWidget', id: 'metric', widget: { type: 'metric', label: 'M', encodings: { value: 'a.b' }, query: { model: 'm' } } },
      { type: 'addWidget', id: 'note', widget: { type: 'text', text: 'hi' } },
      { type: 'placeTile', layout: 'desk', tile: { widget: 'metric', x: 0, y: 0, w: 4, h: 2 } },
      { type: 'placeTile', layout: 'desk', tile: { widget: 'note', x: 4, y: 0, w: 4, h: 2 } },
    ]))
    expect(grown.text).toMatch(/widgets:\n {2}metric:\n {4}type: metric\n/)
    expect(grown.text).toMatch(/tiles:\n {6}- \{widget: metric, x: 0, y: 0, w: 4, h: 2\}\n {6}- \{widget: note, x: 4, y: 0, w: 4, h: 2\}\n/)
    expect(grown.text).toContain('encodings: {value: a.b}')
    expect(canonicalText(parse(grown.text))).toBe(grown.text)
  })
})
