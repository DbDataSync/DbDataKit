import type { APIRequestContext } from '@playwright/test'
import { createHash } from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'

/** What the runner answers to a publish (`PUT /api/v1/definitions/integration/{name}`). */
export interface RunnerPublished { kind: string; name: string; channel: string; version: number; changed: boolean; mode: string; signature: string; tasks: string[] }

/** Every file under `folder`, as the path the runner keeps it under (relative, with `/`), in a stable order. */
function filesOf(folder: string, under = ''): string[] {
  return fs.readdirSync(path.join(folder, under), { withFileTypes: true })
    .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))
    .flatMap((entry) => {
      const relative = under === '' ? entry.name : `${under}/${entry.name}`
      return entry.isDirectory() ? filesOf(folder, relative) : [relative]
    })
}

/**
 * Publishes a package folder to a runner the console manages, through the console's relay as the signed-in person: the calls "Publish a package from files" on the Runner's Packages screen makes. For each file, `HEAD
 * /api/v1/artifacts/{sha256}` asks whether the runner has it and `PUT` sends the ones it does not; then `PUT /api/v1/definitions/integration/{name}` publishes the version with the manifest, in the channel, served at once or staged.
 * A refusal throws with the status and the runner's words.
 */
export async function publishThroughConsole(request: APIRequestContext, server: string, manifestPath: string, folder: string, channel: string, mode: 'serve' | 'stage'): Promise<RunnerPublished> {
  const manifest = fs.readFileSync(manifestPath, 'utf8')
  const name = /^name:\s*(\S+)\s*$/m.exec(manifest)![1]
  const relay = `/relay/${server}/api/v1`
  const artifacts: { path: string; sha256: string; size: number }[] = []
  for (const file of filesOf(folder)) {
    const bytes = fs.readFileSync(path.join(folder, file))
    const sha256 = createHash('sha256').update(bytes).digest('hex')
    artifacts.push({ path: file, sha256, size: bytes.length })
    const has = await request.fetch(`${relay}/artifacts/${sha256}`, { method: 'HEAD' })
    if (has.status() === 200) continue
    if (has.status() !== 404) throw new Error(`The runner did not say whether it has ${file}: ${has.status()}`)
    const sent = await request.put(`${relay}/artifacts/${sha256}`, { data: bytes, headers: { 'Content-Type': 'application/octet-stream' } })
    if (!sent.ok()) throw new Error(`The runner refused the file ${file}: ${sent.status()} ${await sent.text()}`)
  }

  const published = await request.put(`${relay}/definitions/integration/${encodeURIComponent(name)}`, { data: { manifest, channel, mode, artifacts } })
  if (!published.ok()) throw new Error(`The runner refused the package ${name}: ${published.status()} ${await published.text()}`)
  return (await published.json()) as RunnerPublished
}
