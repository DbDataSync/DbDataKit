import { describe, expect, it } from 'vitest'
import { bindingRows, configurationLabel, emptyInstanceForm, formFromRow, instanceFormProblem, instanceRequest, modelsOf, pinRequest } from './appEditors'
import type { AppAbout, AppDetail, AppInstanceRow, AppVersionRow } from './types'

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
    expect(emptyInstanceForm('notes')).toEqual({ name: '', app: 'notes', pin: '', title: '', settings: [], bindings: {} })
    expect(formFromRow(row)).toEqual({ name: 'acme', app: 'notes', pin: '', title: 'Acme notes', settings: [{ key: 'portal', value: 'front' }], bindings: { territories: 'default' } })
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

describe('the request of the form', () => {
  it('sends the app, the pin as a number or null, a blank title as null, settings as given and only the bindings that are filled for a model the version uses', () => {
    const form = {
      name: 'sales', app: 'notes', pin: '1', title: '  ', settings: [{ key: ' embed.allowed_origins ', value: 'https://portal.example.com' }, { key: 'theme', value: '' }],
      bindings: { territories: ' default ', people: '', orders: 'live' },
    }
    expect(instanceRequest(form, ['territories', 'people'])).toEqual({
      app: 'notes', pin: 1, title: null, settings: { 'embed.allowed_origins': 'https://portal.example.com', theme: '' }, bindings: { territories: 'default' },
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
