import { describe, expect, it } from 'vitest'
import { abandonState, buildBusy, buildButton, buildWord, commitState, diagnosticLine, notIsolated, pairProblem, publishButton, referenceProblem, conflictsOf, createState, describeEvent, fileTree, isName, languageOf, mergeState, nameProblem, pathProblem, saveState, statusWord } from './model'

describe('names', () => {
  it('accepts what the server accepts and nothing else', () => {
    for (const ok of ['a', 'new-orders', 'a_b', '0abc', 'a'.repeat(32)]) expect(isName(ok)).toBe(true)
    for (const bad of ['', 'A', '_a', '-a', 'a b', 'a.b', 'a'.repeat(33), '../x']) expect(isName(bad)).toBe(false)
  })
  it('says why a name would be refused', () => {
    expect(nameProblem('', 'workspace')).toBe('Give the workspace a name.')
    expect(nameProblem('a'.repeat(33), 'project')).toBe('At most 32 characters.')
    expect(nameProblem('Has Space', 'project')).toContain('Lower-case')
    expect(nameProblem('fine', 'project')).toBeNull()
  })
})

describe('create', () => {
  const ok = { name: 'draft', canWrite: true, busy: false, what: 'workspace' as const }
  it('is on for a good name by someone who may', () => {
    expect(createState(ok)).toEqual({ enabled: true, why: null })
  })
  it('is off while a call is out, with a bad name, a taken name, or without the role, and says why', () => {
    expect(createState({ ...ok, busy: true })).toEqual({ enabled: false, why: null })
    expect(createState({ ...ok, name: '' })).toEqual({ enabled: false, why: null })
    expect(createState({ ...ok, name: 'Bad' }).why).toContain('Lower-case')
    expect(createState({ ...ok, taken: ['draft'] }).why).toBe('There is a workspace called "draft" already.')
    expect(createState({ ...ok, canWrite: false }).why).toContain('author')
    expect(createState({ ...ok, canWrite: false, what: 'project' }).why).toContain('administrator')
  })
})

describe('abandon', () => {
  it('is the owner\'s, with the author role', () => {
    expect(abandonState({ mine: true, canWrite: true })).toEqual({ enabled: true, why: null })
    expect(abandonState({ mine: false, canWrite: true }).why).toBe('A workspace is abandoned by its owner.')
    expect(abandonState({ mine: true, canWrite: false }).why).toContain('author')
  })
})

describe('history', () => {
  it('reads as a sentence, with the detail when there is one', () => {
    expect(describeEvent({ kind: 'committed', actor: 'Ada', detail: 'Title the grid', at: '2026-11-09T10:00:00Z' })).toBe('Ada committed: Title the grid')
    expect(describeEvent({ kind: 'created', actor: 'Ada', detail: null, at: '' })).toBe('Ada made')
    expect(describeEvent({ kind: 'frobbed', actor: 'Ada', detail: null, at: '' })).toBe('Ada frobbed')
  })
})

describe('the file tree', () => {
  it('nests the paths, folders first, each level in name order', () => {
    const tree = fileTree([{ path: 'views/orders.yaml', size: 3 }, { path: 'app.yaml', size: 1 }, { path: 'views/a/b.yaml', size: 2 }, { path: 'README.md', size: 4 }])
    expect(tree.map((n) => [n.name, n.folder])).toEqual([['views', true], ['app.yaml', false], ['README.md', false]])
    const views = tree[0]!
    expect(views.children.map((n) => n.path)).toEqual(['views/a', 'views/orders.yaml'])
    expect(views.children[0]!.children[0]!).toMatchObject({ name: 'b.yaml', path: 'views/a/b.yaml', size: 2, folder: false })
  })
  it('is empty for no files', () => expect(fileTree([])).toEqual([]))
  it('colours a file by its name', () => {
    expect([languageOf('a.yaml'), languageOf('a.YML'), languageOf('q.sql'), languageOf('x.cs'), languageOf('README.md')]).toEqual(['yaml', 'yaml', 'sql', 'csharp', 'plaintext'])
  })
})

describe('paths as you type them', () => {
  it('accepts a path inside the workspace', () => {
    for (const ok of ['', 'a', 'views/orders.yaml', '.gitignore', '.gitattributes']) expect(pathProblem(ok)).toBeNull()
  })
  it('refuses what the server refuses', () => {
    for (const bad of ['/a', 'a//b', 'a/', '../x', 'a/./b', 'a\\b', 'C:/x', '.git/config', 'a/.GIT/x', '.git ', 'x.', 'a'.repeat(241)]) expect(pathProblem(bad), bad).not.toBeNull()
  })
})

describe('the buttons of a workspace', () => {
  it('save is the owners and needs a change', () => {
    expect(saveState({ mine: false, canWrite: true, dirty: true, busy: false })).toEqual({ enabled: false, why: 'A workspace is edited by its owner.' })
    expect(saveState({ mine: true, canWrite: false, dirty: true, busy: false }).why).toContain('author')
    expect(saveState({ mine: true, canWrite: true, dirty: false, busy: false })).toEqual({ enabled: false, why: null })
    expect(saveState({ mine: true, canWrite: true, dirty: true, busy: false })).toEqual({ enabled: true, why: null })
    expect(saveState({ mine: true, canWrite: true, dirty: true, busy: true }).enabled).toBe(false)
  })
  it('commit needs a message and something to take', () => {
    const ok = { mine: true, canWrite: true, message: 'Title the grid', uncommitted: true, busy: false }
    expect(commitState(ok)).toEqual({ enabled: true, why: null })
    expect(commitState({ ...ok, message: '  ' }).enabled).toBe(false)
    expect(commitState({ ...ok, uncommitted: false }).why).toBe('Nothing has changed since the last commit.')
    expect(commitState({ ...ok, mine: false }).enabled).toBe(false)
    expect(commitState({ ...ok, canWrite: false }).why).toContain('author')
  })
  it('merge needs a commit and nothing left uncommitted', () => {
    expect(mergeState({ commits: 0, uncommitted: false, busy: false }).enabled).toBe(false)
    expect(mergeState({ commits: 1, uncommitted: true, busy: false }).why).toContain('commit them')
    expect(mergeState({ commits: 1, uncommitted: false, busy: false })).toEqual({ enabled: true, why: null })
    expect(mergeState({ commits: 1, uncommitted: false, busy: true }).enabled).toBe(false)
  })
  it('reads the conflicts of a refused merge and the words of a change', () => {
    expect(conflictsOf({ error: 'x', conflicts: ['views/orders.yaml', 3] })).toEqual(['views/orders.yaml'])
    expect(conflictsOf(null)).toEqual([])
    expect(conflictsOf({ conflicts: 'no' })).toEqual([])
    expect([statusWord('added'), statusWord('modified'), statusWord('deleted'), statusWord('Renamed'), statusWord('copied')]).toEqual(['added', 'changed', 'deleted', 'renamed', 'copied'])
  })
})

describe('builds, preview and publish', () => {
  const ready = { canWrite: true, ready: true, problems: [], busy: false }
  it('a build needs the role, a server that can build and no build going', () => {
    expect(buildButton(ready)).toEqual({ enabled: true, why: null })
    expect(buildButton({ ...ready, canWrite: false }).why).toContain('author')
    // a server that cannot build says the first thing it lacks, in its own sentence
    expect(buildButton({ ...ready, ready: false, problems: ['No package store is set.', 'Bun is not installed.'] })).toEqual({ enabled: false, why: 'No package store is set.' })
    expect(buildButton({ ...ready, busy: true }).why).toContain('already going')
    expect(buildButton({ ...ready, closed: true }).why).toContain('merged')
  })
  it('knows a build that is still going, and says its status as a person does', () => {
    expect(['queued', 'running', 'succeeded', 'failed'].map(buildBusy)).toEqual([true, true, false, false])
    expect([buildWord('succeeded'), buildWord('queued'), buildWord('weird')]).toEqual(['built', 'waiting', 'weird'])
  })
  it('shows a diagnostic with its file and line when it has them', () => {
    expect(diagnosticLine({ file: 'src/main.tsx', line: 3, message: "imports 'lodash'" })).toBe("src/main.tsx:3: imports 'lodash'")
    expect(diagnosticLine({ message: 'The store does not match.' })).toBe('The store does not match.')
  })
  it('notices the note of a build that was not isolated', () => {
    expect(notIsolated(['The bundler ran without network isolation: this Linux machine could not make a network namespace.'])).toBe(true)
    expect(notIsolated(['The bundler ran in a network namespace with no route.'])).toBe(false)
  })
  it('publishing needs the role, a succeeded build and a key that can publish', () => {
    const targets = [{ name: 'edge', can_publish: true }, { name: 'viewing', can_publish: false }]
    const ok = { canWrite: true, status: 'succeeded', target: 'edge', targets, busy: false }
    expect(publishButton(ok)).toEqual({ enabled: true, why: null })
    expect(publishButton({ ...ok, canWrite: false }).why).toContain('approver')
    expect(publishButton({ ...ok, status: 'failed' }).why).toContain('succeeded')
    expect(publishButton({ ...ok, target: '', targets: [] }).why).toContain('Publisher keys')
    expect(publishButton({ ...ok, target: '' })).toEqual({ enabled: false, why: null })
    expect(publishButton({ ...ok, target: 'viewing' }).why).toContain('not paired with a Gateway that lets it publish')
    expect(publishButton({ ...ok, busy: true }).enabled).toBe(false)
  })
  it('refuses a key typed where a reference goes', () => {
    expect(referenceProblem('')).toBeNull()
    expect(referenceProblem('secret:dbdata:workspace:publisher-key:edge')).toBeNull()
    expect(referenceProblem('workspace-1a2b3c:c2VlZA')).toContain('"secret:"')
    expect(referenceProblem('secret:a b')).toContain('no spaces')
  })
  it('checks the pair form as it is typed', () => {
    expect(pairProblem({ name: '', url: '' })).toBeNull()
    expect(pairProblem({ name: 'Edge', url: '' })).toContain('lower-case')
    expect(pairProblem({ name: 'edge', url: 'http://gateway' })).toContain('https://')
    expect(pairProblem({ name: 'edge', url: 'https://gateway:8443' })).toBeNull()
  })
})
