import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { checkAnswers, describeLink, resolveAction, type LinkDoc, type LinkInfo, type ResolvedAction } from '../index'
import { validateViewsDocument } from './document'

const root = join(import.meta.dirname, '../../../../conformance/views')
const read = (relative: string) => JSON.parse(readFileSync(join(root, relative), 'utf8')) as Record<string, any>
const golden = read('actions.json') as { document: string; cases: { name: string; link: string; row?: Record<string, unknown>; params?: Record<string, any>; answers?: Record<string, string>; expect: ResolvedAction }[] }
const doc = read(golden.document)

describe('the good document of actions', () => {
  it('is accepted', () => expect(validateViewsDocument(doc, { kind: 'dashboard' })).toEqual([]))
  it('reads every link as an action with the target `model.entity` or `package.task`', () => {
    const links = Object.fromEntries(Object.entries(doc.links as Record<string, LinkDoc>).map(([n, l]) => [n, describeLink(n, l)]))
    expect(Object.values(links).map((l: LinkInfo) => [l.targetKind, l.target, l.action?.operation, l.hasAddress])).toEqual([
      ['change', 'sales.orders', 'edit', false], ['change', 'sales.orders', 'edit', false], ['change', 'sales.orders', 'insert', false], ['change', 'sales.orders', 'delete', false], ['task', 'nightly.load', 'run', false],
      ['change', 'sales.orders', 'edit', false], ['change', 'sales.orders', 'edit', false], ['change', 'sales.orders', 'edit', false],
    ])
    expect(Object.values(links).map((l: LinkInfo) => [l.action!.for, l.action!.stage, l.action!.confirm.skip === true])).toEqual([
      ['row', false, false], ['row', false, false], ['row', false, false], ['row', false, false], ['row', false, false], ['row', false, true], ['row', true, false], ['selection', true, false],
    ])
    expect(links['close-order']!.thenKind).toBe('refresh')
    expect(links['set-status']!.thenKind).toBe('widgets')
    expect(links['set-status']!.action!.set.status).toMatchObject({ kind: 'ask', text: 'status' })
  })
})

describe('what the popup says (shared with .NET)', () => {
  for (const c of golden.cases)
    it(c.name, () => {
      const link = describeLink(c.link, (doc.links as Record<string, LinkDoc>)[c.link]!)
      expect(JSON.parse(JSON.stringify(resolveAction(c.link, { action: link.action!, label: link.label }, c.row ?? {}, c.params ?? {}, c.answers ?? {})))).toEqual(c.expect)
    })
})

describe('the answers a person gives', () => {
  const asks = describeLink('set-status', (doc.links as Record<string, LinkDoc>)['set-status']!).action!.ask
  it('are checked against the field: required, choices, type', () => {
    const list = Object.values(asks)
    expect(checkAnswers(list, { status: 'Held' })).toEqual({})
    expect(checkAnswers(list, {})).toEqual({ status: 'New status is needed.' })
    expect(checkAnswers(list, { status: 'Lost' })).toEqual({ status: 'New status is one of: Open, Held, Closed.' })
    expect(checkAnswers([{ name: 'n', label: 'N', type: 'integer', choices: [], required: true }], { n: '1.5' })).toEqual({ n: 'N is a whole number.' })
    expect(checkAnswers([{ name: 'd', label: 'D', type: 'date', choices: [], required: false }], { d: '2026-02-30x' })).toEqual({ d: 'D is a date, yyyy-MM-dd.' })
  })
})
