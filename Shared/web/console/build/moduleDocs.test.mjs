import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { describe, expect, it } from 'vitest'
import { resolveLink } from '../../shell/src/components/markdownLinks.ts'
import { discoverModules, generateAllDocs, generateModuleDocs, readOrder } from './moduleDocs.mjs'

const WEB = path.resolve(import.meta.dirname, '../..')

/** A throwaway module: a docs folder with the given pages, its ordering file, and optional pictures next to it. */
function module(pages, order, pictures = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'moduledocs-'))
  const dir = path.join(root, 'docs')
  fs.mkdirSync(dir)
  for (const [slug, text] of Object.entries(pages)) fs.writeFileSync(path.join(dir, `${slug}.md`), text)
  if (order !== null) fs.writeFileSync(path.join(dir, 'order.txt'), order)
  for (const [file, bytes] of Object.entries(pictures)) {
    fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true })
    fs.writeFileSync(path.join(root, file), bytes)
  }
  return { id: 'fake', title: 'Fake product', dir }
}

describe('the ordering file', () => {
  it('reads one slug and title a line, skipping comments and blanks', () => {
    expect(readOrder('# a note\n\nb: Second page\na: First: with a colon\nc\n')).toEqual([
      { slug: 'b', title: 'Second page' }, { slug: 'a', title: 'First: with a colon' }, { slug: 'c', title: 'c' },
    ])
  })
})

describe('a module\'s generated docs', () => {
  it('lists the pages in the order of the ordering file, and carries each page and the pictures it shows', () => {
    const generated = generateModuleDocs(module(
      { one: '# One\n![a picture](../screenshots/shots/a.png)\n', two: '# Two\n[one](one.md)\n' },
      'two: Page two\none: Page one\n',
      { 'screenshots/shots/a.png': Buffer.from([1, 2, 3]) }))

    expect(generated.problems).toEqual([])
    expect(JSON.parse(generated.files.get('fake/manifest.json'))).toEqual({ id: 'fake', title: 'Fake product', pages: [{ slug: 'two', title: 'Page two' }, { slug: 'one', title: 'Page one' }] })
    expect(generated.files.get('fake/one.md')).toContain('# One')
    expect([...generated.files.get('fake/assets/screenshots/shots/a.png')]).toEqual([1, 2, 3])
  })

  it('fails, naming the module, for a page nobody listed and a line with no page', () => {
    const { problems } = generateModuleDocs(module({ a: '# A', b: '# B' }, 'a: A\nghost: Ghost\n'))

    expect(problems).toContain('fake: b.md is not listed in order.txt, so nobody could reach it')
    expect(problems).toContain('fake: order.txt lists ghost, which has no ghost.md')
  })

  it('fails for a page listed twice, a missing ordering file and a picture that is not there', () => {
    expect(generateModuleDocs(module({ a: '# A' }, 'a: A\na: Again\n')).problems).toContain('fake: a is listed twice in order.txt')
    expect(generateModuleDocs(module({ a: '# A' }, null)).problems[0]).toContain('order.txt is missing')
    expect(generateModuleDocs(module({ a: '![x](../screenshots/shots/gone.png)' }, 'a: A\n')).problems).toContain('fake: a page shows ../screenshots/shots/gone.png, which is not there')
  })

  it('names a picture written a way the viewer could not show', () => {
    const { problems } = generateModuleDocs(module({ a: '![x](pictures/a.png)' }, 'a: A\n'))

    expect(problems).toContain('fake: a.md shows pictures/a.png, which is not a picture under ../screenshots/')
  })
})

describe('the modules of the web workspace', () => {
  it('are found by what their package.json registers, and every id is used once', () => {
    const modules = discoverModules(WEB)

    expect(modules.map((m) => m.id)).toContain('sync')
    expect(new Set(modules.map((m) => m.id)).size).toBe(modules.length)
  })

  it('have docs that are sound: every page listed, every picture there', () => {
    const { problems, files } = generateAllDocs(WEB)

    expect(problems).toEqual([])
    expect(JSON.parse(files.get('index.json'))).toEqual(expect.arrayContaining([{ id: 'sync', title: 'DbDataSync' }]))
  })
})

describe('the docs of Sync as the console will serve them', () => {
  const sync = discoverModules(WEB).find((m) => m.id === 'sync')
  const generated = generateModuleDocs(sync)
  const slugs = generated.pages.map((p) => p.slug)
  const pages = slugs.map((slug) => [slug, generated.files.get(`sync/${slug}.md`)])

  it('has every page of docs/', () => {
    const onDisk = fs.readdirSync(sync.dir).filter((n) => n.endsWith('.md')).map((n) => n.slice(0, -3)).sort()

    expect([...slugs].sort()).toEqual(onDisk)
    for (const page of generated.pages) expect(page.title.trim()).not.toBe('')
  })

  // Every relative link a page makes to a `.md` must resolve to a page that ships: otherwise the viewer shows it as plain text, and this is where a renamed or removed page is caught rather than by a reader.
  it.each(pages)('every page link in %s.md resolves', (_slug, text) => {
    const links = [...text.matchAll(/\]\(([^)\s]+)\)/g)].map((m) => m[1]).filter((href) => /\.md(#|$)/.test(href) && !/^[a-z][a-z0-9+.-]*:/i.test(href))

    for (const href of links) expect(resolveLink(href, slugs).kind, href).toBe('doc')
  })

  it('shows no picture by an address that goes stale', () => {
    for (const [slug, text] of pages)
      for (const m of text.matchAll(/!\[[^\]]*\]\(([^)\s]+)\)/g)) expect(m[1], `${slug}.md`).not.toMatch(/^[a-z][a-z0-9+.-]*:|^\/\//i)
  })
})
