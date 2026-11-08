import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import {
  distinctContent, emptyPublishForm, hashFiles, manifestIn, packageFiles, packagePath, publishBlock, publishRequest, PublishFailure, readManifest, sendPackage, sha256Of, sizeText, stepText,
  type FileLike, type HashedFile, type Prepared, type PublishApi, type PublishForm,
} from './publishFiles'

const file = (path: string, text: string, folder = 'pkg'): FileLike => {
  const bytes = new TextEncoder().encode(text)
  return { name: path.split('/').at(-1)!, size: bytes.length, webkitRelativePath: `${folder}/${path}`, arrayBuffer: async () => bytes.buffer.slice(0) as ArrayBuffer, text: async () => text }
}
const manifestText = 'schema: package/1\nname: orders\nlaunch: { assembly: app.dll }\ntasks:\n  - name: load\n'

describe('the files of a folder', () => {
  it('drops the folder\'s own name from each path and sorts them', () => {
    expect(packagePath({ name: 'a.dll', webkitRelativePath: 'pkg/bin/a.dll' })).toBe('bin/a.dll')
    expect(packagePath({ name: 'a.dll' })).toBe('a.dll')
    expect(packageFiles([file('z.txt', ''), file('bin/a.dll', ''), file('package.yaml', '')]).map((f) => f.path)).toEqual(['bin/a.dll', 'package.yaml', 'z.txt'])
  })
  it('finds a package.yaml at the top of the folder, and not one deeper', () => {
    expect(manifestIn(packageFiles([file('package.yaml', 'x'), file('a.dll', '')]))?.path).toBe('package.yaml')
    expect(manifestIn(packageFiles([file('sub/package.yaml', 'x')]))).toBeUndefined()
  })
})

describe('the manifest, as far as the browser can tell', () => {
  it('reads the package name from a package/1 manifest', () => {
    expect(readManifest(manifestText)).toEqual({ ok: true, name: 'orders' })
    expect(readManifest('schema: "package/1"\nname: "orders-2"\n')).toEqual({ ok: true, name: 'orders-2' })
  })
  it('says what is missing in words', () => {
    expect(readManifest('  ')).toEqual({ ok: false, problem: 'The manifest is empty.' })
    expect(readManifest('name: orders\n')).toMatchObject({ ok: false, problem: expect.stringContaining('no schema line') })
    expect(readManifest('schema: build/1\nname: orders\n')).toMatchObject({ ok: false, problem: expect.stringContaining('not package/1') })
    expect(readManifest('schema: package/1\n')).toMatchObject({ ok: false, problem: expect.stringContaining('does not name the package') })
    expect(readManifest('schema: package/1\nname: Orders\n')).toMatchObject({ ok: false, problem: expect.stringContaining('not a package name') })
  })
})

describe('hashing', () => {
  it('gives the SHA-256 of the bytes as lower case hexadecimal', async () => {
    expect(await sha256Of(file('a', 'hello'))).toBe('2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824')
    expect(await sha256Of(file('e', ''))).toBe('e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855')
  })
  it('hashes the files in order, telling how far it is, and finds each content once', async () => {
    const seen: number[] = []
    const hashed = await hashFiles(packageFiles([file('a.txt', 'same'), file('b.txt', 'same'), file('c.txt', 'other')]), (n) => seen.push(n))
    expect(seen).toEqual([1, 2, 3])
    expect(hashed.map((h) => h.size)).toEqual([4, 4, 5])
    expect(distinctContent(hashed).map((h) => h.path)).toEqual(['a.txt', 'c.txt'])
  })
})

describe('what stops Publish, said in words', () => {
  const folder = { count: 2, hashing: false }
  const ok = { ok: true as const, name: 'orders' }
  const prepared: Prepared = { manifest: manifestText, name: 'orders', files: [] }
  const form = (over: Partial<PublishForm> = {}): PublishForm => ({ ...emptyPublishForm, channel: 'dev', ...over })
  it('asks for what is missing in order', () => {
    expect(publishBlock(false, form(), folder, ok, prepared)).toContain('admin role')
    expect(publishBlock(true, form(), null, null, null)).toBe('Choose the folder of the package.')
    expect(publishBlock(true, form(), { count: 0, hashing: false }, null, null)).toBe('Choose the folder of the package.')
    expect(publishBlock(true, form(), folder, null, null)).toContain('no package.yaml')
    expect(publishBlock(true, form(), folder, { ok: false, problem: 'The manifest is empty.' }, null)).toBe('The manifest is empty.')
    expect(publishBlock(true, form(), { count: 2, hashing: true }, ok, null)).toBe('Reading the files...')
    expect(publishBlock(true, form({ channel: ' ' }), folder, ok, prepared)).toBe('Name the channel.')
    expect(publishBlock(true, form({ channel: 'Prod' }), folder, ok, prepared)).toContain('not a channel name')
    expect(publishBlock(true, form(), folder, ok, prepared)).toBeNull()
  })
  it('says the size in words', () => {
    expect(sizeText(12)).toBe('12 bytes'); expect(sizeText(2048)).toBe('2.0 KB'); expect(sizeText(3 * 1024 * 1024)).toBe('3.0 MB')
  })
})

describe('the publish request', () => {
  const files: HashedFile[] = [{ path: 'app.dll', file: file('app.dll', 'x'), sha256: 'a'.repeat(64), size: 1 }]
  const prepared: Prepared = { manifest: manifestText, name: 'orders', files }
  it('names every file by its hash, stages by default and carries a note only when there is one', () => {
    expect(publishRequest(prepared, { channel: ' dev ', serve: false, note: '' })).toEqual({ manifest: manifestText, channel: 'dev', mode: 'stage', artifacts: [{ path: 'app.dll', sha256: 'a'.repeat(64), size: 1 }] })
    expect(publishRequest(prepared, { channel: 'dev', serve: true, note: ' first ' })).toMatchObject({ mode: 'serve', note: 'first' })
  })
})

describe('sending a package', () => {
  const make = async () => {
    const hashed = await hashFiles(packageFiles([file('app.dll', 'dll'), file('copy.dll', 'dll'), file('notes.txt', 'notes')]))
    return { manifest: manifestText, name: 'orders', files: hashed } satisfies Prepared
  }
  const fake = (have: string[], over: Partial<PublishApi> = {}) => {
    const calls: string[] = []
    const api: PublishApi = {
      has: async (sha) => { calls.push('has ' + sha.slice(0, 4)); return have.includes(sha) },
      put: async (sha) => { calls.push('put ' + sha.slice(0, 4)) },
      publish: async (name, body) => { calls.push(`publish ${name} ${body.mode} ${body.artifacts.length}`); return { kind: 'integration', name, channel: body.channel, version: 1, changed: true, mode: body.mode, signature: 'unsigned', tasks: ['load'] } },
      ...over,
    }
    return { api, calls }
  }
  it('asks once per content, sends only what the runner lacks, then publishes all the files', async () => {
    const prepared = await make()
    const known = prepared.files.find((f) => f.path === 'notes.txt')!.sha256
    const { api, calls } = fake([known])
    const steps: string[] = []
    const published = await sendPackage(api, prepared, { channel: 'dev', serve: false, note: '' }, (s) => steps.push(s.kind))
    expect(calls.filter((c) => c.startsWith('has'))).toHaveLength(2)          // app.dll and copy.dll are one content
    expect(calls.filter((c) => c.startsWith('put'))).toHaveLength(1)          // notes.txt is on the runner already
    expect(calls.at(-1)).toBe('publish orders stage 3')
    expect(published).toMatchObject({ version: 1, uploaded: 1, skipped: 2, tasks: ['load'] })
    expect(steps).toContain('sending'); expect(steps.at(-1)).toBe('publishing')
  })
  it('stops at a refused file, naming it and the runner\'s words, and publishes nothing', async () => {
    const prepared = await make()
    const { api, calls } = fake([], { put: async () => { throw new ApiError(400, 'The bytes of app.dll hash to 1234, not 5678.') } })
    const failure = await sendPackage(api, prepared, emptyPublishForm, undefined).catch((e: unknown) => e)
    expect(failure).toBeInstanceOf(PublishFailure)
    expect((failure as PublishFailure).message).toBe('The runner refused the file app.dll (400): The bytes of app.dll hash to 1234, not 5678.')
    expect(calls.some((c) => c.startsWith('publish'))).toBe(false)
  })
  it('shows the runner\'s refusal of the publish itself, and says a runner that did not answer did not', async () => {
    const prepared = await make()
    const refused = await sendPackage(fake([]).api, prepared, emptyPublishForm).then(() => null, (e: unknown) => e)
    expect(refused).toBeNull()
    const stale = fake([], { publish: async () => { throw new ApiError(403, "This runner requires signed versions (require_signatures is on) and the integration 'orders' has no valid signature.") } })
    await expect(sendPackage(stale.api, prepared, emptyPublishForm)).rejects.toThrow("The runner refused the package orders (403): This runner requires signed versions")
    const down = fake([], { has: async () => { throw new TypeError('fetch failed') } })
    await expect(sendPackage(down.api, prepared, emptyPublishForm)).rejects.toThrow('could not be reached while to say whether it has app.dll')
  })
  it('says the step it is at', () => {
    expect(stepText({ kind: 'checking', done: 1, total: 3 })).toContain('(1 of 3)')
    expect(stepText({ kind: 'sending', done: 0, total: 2, path: 'a.dll' })).toBe('Sending a.dll (1 of 2)...')
    expect(stepText({ kind: 'publishing' })).toContain('Publishing')
  })
})
