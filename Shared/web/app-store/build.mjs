#!/usr/bin/env node
// The vendored package store of the app build (plan 22a GP32, GP33; QG12): a folder `node_modules/` with the default library set and everything it needs at run time,
// and `store.json`, which lists every file with its SHA-256. `apps build --store <folder>` builds from it with no network and refuses a store whose files differ from the list.
//
//   node app-store/build.mjs --out <folder> [--include <package>[,<package>...]]
//
// Run it in Shared/web after `npm ci`. `--include` adds packages that are installed there but are not part of the default set (an administrator's allow-list in server.yaml,
// `apps.allowed_packages`, still decides whether an app may import them). Nothing here reaches a network and nothing is run from a package: files are copied and hashed.
import { createHash } from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

/** What an app may import with no allow-list entry: the suite's libraries for apps, and the framework they draw with. */
export const DEFAULT_LIBRARY = ['@dbdatakit/semantic-client', '@dbdatakit/store-ui', '@dbdatakit/view-app', '@dbdatakit/visuals', 'react', 'react-dom']

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

function parseArguments(argv) {
  const options = { out: null, include: [] }
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--out') options.out = argv[++i]
    else if (argv[i] === '--include') options.include = (argv[++i] ?? '').split(',').filter(Boolean)
    else throw new Error(`Unknown argument ${argv[i]}. Usage: node app-store/build.mjs --out <folder> [--include <package>[,<package>...]]`)
  }
  if (!options.out) throw new Error('Usage: node app-store/build.mjs --out <folder> [--include <package>[,<package>...]]')
  return options
}

const workspaces = JSON.parse(fs.readFileSync(path.join(webRoot, 'package.json'), 'utf8')).workspaces
  .map((w) => path.join(webRoot, w))
  .filter((dir) => fs.existsSync(path.join(dir, 'package.json')))
  .map((dir) => ({ dir, name: JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8')).name }))

/** Where a package's directory goes in the store: the same place under `node_modules`, with a workspace package under its own name. */
function storePath(realDirectory) {
  const real = fs.realpathSync(realDirectory)
  const nodeModules = path.join(webRoot, 'node_modules') + path.sep
  if (real.startsWith(nodeModules)) return 'node_modules/' + path.relative(path.join(webRoot, 'node_modules'), real).split(path.sep).join('/')
  const workspace = workspaces.find((w) => real === fs.realpathSync(w.dir) || real.startsWith(fs.realpathSync(w.dir) + path.sep))
  if (!workspace) throw new Error(`${real} is neither under node_modules nor a workspace package.`)
  return 'node_modules/' + workspace.name + (real === fs.realpathSync(workspace.dir) ? '' : '/' + path.relative(fs.realpathSync(workspace.dir), real).split(path.sep).join('/'))
}

function locate(name, fromDirectory) {
  for (let directory = fs.realpathSync(fromDirectory); ; directory = path.dirname(directory)) {
    const candidate = path.join(directory, 'node_modules', name)
    if (fs.existsSync(path.join(candidate, 'package.json'))) return candidate
    const parent = path.dirname(directory)
    if (parent === directory) return null
  }
}

const isWorkspace = (real) => workspaces.some((w) => fs.realpathSync(w.dir) === real)
const skippedInWorkspace = (relative) => /(^|\/)(__snapshots__|node_modules|dist|coverage)(\/|$)|\.test\.[a-z]+$/.test(relative)

/** The files of a package that are copied: a workspace package's source, schemas and manifest (no tests); a third-party package as published (its own nested packages are walked on their own). */
function filesOf(directory, workspace) {
  const found = []
  const walk = (current) => {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const full = path.join(current, entry.name)
      const relative = path.relative(directory, full).split(path.sep).join('/')
      if (entry.isSymbolicLink()) continue
      if (entry.isDirectory()) {
        if (entry.name === 'node_modules') continue
        if (workspace && (skippedInWorkspace(relative + '/') || !['src', 'schemas'].includes(relative.split('/')[0]))) continue
        walk(full)
      } else if (entry.isFile()) {
        if (workspace && (skippedInWorkspace(relative) || (!relative.includes('/') && relative !== 'package.json'))) continue
        found.push(relative)
      }
    }
  }
  walk(directory)
  return found.sort()
}

function build({ out, include }) {
  const roots = [...DEFAULT_LIBRARY, ...include]
  const queue = roots.map((name) => ({ name, from: webRoot, root: true }))
  const packages = new Map()   // store path -> { name, version, directory }
  while (queue.length > 0) {
    const { name, from, root } = queue.shift()
    const directory = locate(name, from)
    if (!directory) {
      if (root) throw new Error(`The package ${name} is not installed in ${webRoot} (run npm ci there${include.includes(name) ? ', and npm install the package you include' : ''}).`)
      continue   // a peer or optional dependency that is not installed
    }
    const target = storePath(directory)
    if (packages.has(target)) continue
    const manifest = JSON.parse(fs.readFileSync(path.join(directory, 'package.json'), 'utf8'))
    packages.set(target, { name: manifest.name, version: manifest.version, directory: fs.realpathSync(directory), workspace: isWorkspace(fs.realpathSync(directory)) })
    const wanted = { ...(manifest.dependencies ?? {}), ...(packages.get(target).workspace ? {} : manifest.peerDependencies ?? {}) }
    for (const dependency of Object.keys(wanted)) queue.push({ name: dependency, from: directory, root: false })
  }

  if (fs.existsSync(out) && fs.readdirSync(out).length > 0 && !fs.existsSync(path.join(out, 'store.json')))
    throw new Error(`${out} is not empty and holds no store.json, so it is not an earlier store; name a new or an empty folder.`)
  fs.rmSync(out, { recursive: true, force: true })
  const files = {}
  for (const [target, info] of [...packages].sort(([a], [b]) => (a < b ? -1 : 1))) {
    for (const relative of filesOf(info.directory, info.workspace)) {
      const destination = path.join(out, target, relative)
      fs.mkdirSync(path.dirname(destination), { recursive: true })
      fs.copyFileSync(path.join(info.directory, relative), destination)
      files[`${target}/${relative}`] = createHash('sha256').update(fs.readFileSync(destination)).digest('hex')
    }
  }
  const store = {
    schemaVersion: 1,
    library: DEFAULT_LIBRARY,
    packages: Object.fromEntries([...packages].sort(([a], [b]) => (a < b ? -1 : 1)).map(([target, info]) => [target, { name: info.name, version: info.version }])),
    files,
  }
  fs.writeFileSync(path.join(out, 'store.json'), JSON.stringify(store, null, 2) + '\n')
  return { packages: packages.size, files: Object.keys(files).length }
}

if (process.argv[1] && fs.realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const options = parseArguments(process.argv.slice(2))
    const result = build({ out: path.resolve(options.out), include: options.include })
    console.log(`Wrote ${options.out}: ${result.packages} packages, ${result.files} files; default library: ${DEFAULT_LIBRARY.join(', ')}.`)
  } catch (e) {
    console.error(e instanceof Error ? e.message : String(e))
    process.exit(1)
  }
}
