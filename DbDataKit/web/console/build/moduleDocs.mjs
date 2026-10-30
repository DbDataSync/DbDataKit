// The docs of every module the console hosts, generated for the console to serve (plan 13a, U11). A module registers its docs in its own package.json:
//
//   "dbdatakit": { "docs": { "id": "sync", "title": "DbDataSync", "dir": "../../../DbDataSync/docs" } }
//
// `dir` is the module's docs folder (relative to the package); the pages are its `*.md` files, in the order of `order.txt` there (one `slug: Title` a line), and the pictures are
// the ones the pages show, written `../screenshots/<folder>/<file>` next to the docs folder. Nothing is committed twice: the folder is the only copy, and this writes what the console serves:
//
//   docs/index.json                       every module: [{ id, title }]
//   docs/<id>/manifest.json               { id, title, pages: [{ slug, title }] } in order
//   docs/<id>/<slug>.md                   a page
//   docs/<id>/assets/screenshots/...      a picture a page shows
//
// A folder and its ordering file that disagree, a picture that is not there, or an id used twice fails the build with a message naming the module: a page nobody can reach, or a broken image
// nobody finds out about until a reader does, does not reach a release.
import fs from 'node:fs'
import path from 'node:path'

const PICTURE = /!\[[^\]]*\]\((\.\.\/screenshots\/[a-z0-9-]+\/[A-Za-z0-9._-]+\.(?:png|jpe?g|gif|webp))\)/gi
const ID = /^[a-z][a-z0-9-]*$/

/** The lines of an ordering file as pages; blank lines and `#` comments are skipped. A line without a title takes its slug as one. */
export function readOrder(text) {
  return text.split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line && !line.startsWith('#'))
    .map((line) => {
      const colon = line.indexOf(':')
      return colon < 0 ? { slug: line, title: line } : { slug: line.slice(0, colon).trim(), title: line.slice(colon + 1).trim() }
    })
}

/**
 * One module's generated files, as a map from path (below `docs/`) to text or bytes, and the problems found (empty when the module's docs are sound).
 * @param {{ id: string, title: string, dir: string, order?: string }} module `dir` absolute
 */
export function generateModuleDocs(module) {
  const problems = []
  const files = new Map()
  const { id, title, dir } = module
  if (!ID.test(id)) problems.push(`${id}: a module id is lower-case letters, digits and hyphens`)
  if (!fs.existsSync(dir)) return { files, problems: [...problems, `${id}: the docs folder ${dir} does not exist`], pages: [] }

  const onDisk = fs.readdirSync(dir).filter((name) => name.endsWith('.md')).map((name) => name.slice(0, -3)).sort()
  const orderFile = path.join(dir, module.order ?? 'order.txt')
  if (!fs.existsSync(orderFile)) return { files, problems: [...problems, `${id}: ${orderFile} is missing; it lists the pages in the order of the index, one "slug: Title" a line`], pages: [] }
  const pages = readOrder(fs.readFileSync(orderFile, 'utf8'))

  const slugs = pages.map((p) => p.slug)
  for (const slug of new Set(slugs.filter((s, i) => slugs.indexOf(s) !== i))) problems.push(`${id}: ${slug} is listed twice in ${path.basename(orderFile)}`)
  for (const slug of onDisk.filter((s) => !slugs.includes(s))) problems.push(`${id}: ${slug}.md is not listed in ${path.basename(orderFile)}, so nobody could reach it`)
  for (const slug of slugs.filter((s) => !onDisk.includes(s))) problems.push(`${id}: ${path.basename(orderFile)} lists ${slug}, which has no ${slug}.md`)
  for (const page of pages) if (!page.title) problems.push(`${id}: ${page.slug} has no title in ${path.basename(orderFile)}`)

  const pictures = new Set()
  for (const page of pages.filter((p) => onDisk.includes(p.slug))) {
    const text = fs.readFileSync(path.join(dir, `${page.slug}.md`), 'utf8')
    files.set(`${id}/${page.slug}.md`, text)
    for (const match of text.matchAll(PICTURE)) pictures.add(match[1])
    // A picture written any other way (an address, a path outside the screenshots folder) would show as nothing in the viewer, so it is named here.
    for (const match of text.matchAll(/!\[[^\]]*\]\(([^)\s]+)\)/g))
      if (!match[1].startsWith('../screenshots/') && !/^https?:/i.test(match[1])) problems.push(`${id}: ${page.slug}.md shows ${match[1]}, which is not a picture under ../screenshots/`)
  }
  for (const picture of [...pictures].sort()) {
    const file = path.resolve(dir, picture)
    if (!fs.existsSync(file)) problems.push(`${id}: a page shows ${picture}, which is not there`)
    else files.set(`${id}/assets/${picture.slice(3)}`, fs.readFileSync(file))
  }

  files.set(`${id}/manifest.json`, JSON.stringify({ id, title, pages }, null, 2) + '\n')
  return { files, problems, pages }
}

/** Every module under `webRoot` whose package.json registers docs: `{ id, title, dir }` with `dir` absolute. */
export function discoverModules(webRoot) {
  const modules = []
  for (const entry of fs.readdirSync(webRoot, { withFileTypes: true })) {
    if (!entry.isDirectory() || entry.name === 'node_modules') continue
    const manifest = path.join(webRoot, entry.name, 'package.json')
    if (!fs.existsSync(manifest)) continue
    const docs = JSON.parse(fs.readFileSync(manifest, 'utf8')).dbdatakit?.docs
    if (docs) modules.push({ id: docs.id, title: docs.title, dir: path.resolve(webRoot, entry.name, docs.dir), order: docs.order, package: entry.name })
  }
  return modules.sort((a, b) => a.id.localeCompare(b.id))
}

/** All the docs of the modules under `webRoot`, with every problem found. */
export function generateAllDocs(webRoot) {
  const files = new Map()
  const problems = []
  const modules = discoverModules(webRoot)
  const ids = modules.map((m) => m.id)
  for (const id of new Set(ids.filter((s, i) => ids.indexOf(s) !== i))) problems.push(`the docs id ${id} is registered by more than one package`)
  for (const module of modules) {
    const generated = generateModuleDocs(module)
    problems.push(...generated.problems)
    for (const [file, content] of generated.files) files.set(file, content)
  }
  files.set('index.json', JSON.stringify(modules.map(({ id, title }) => ({ id, title })), null, 2) + '\n')
  return { files, problems }
}
