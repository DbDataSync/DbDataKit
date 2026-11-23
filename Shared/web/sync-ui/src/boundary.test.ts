import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
import { describe, expect, it } from 'vitest'

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name)
    return statSync(path).isDirectory() ? sources(path) : /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name) ? [path] : []
  })
}

/** What only a screen that watches or runs a replication would use: the old live channel, and the routes of a Runner, a run or a hub. */
export const FORBIDDEN: { what: string; pattern: RegExp }[] = [
  { what: 'imports @microsoft/signalr', pattern: /from\s+['"]@microsoft\/signalr['"]|import\(\s*['"]@microsoft\/signalr['"]\s*\)/ },
  { what: 'calls a Runner route', pattern: /\/api\/v1\/(tasks|runs|channels|packages|checkpoints)\b|runner-runs|\/hubs\//i },
  { what: 'calls a server itself', pattern: /\bfetch\(|\bnew\s+WebSocket\b|\bEventSource\b|\brequest\s*[<(]|workspaceApi/ },
]

/** The modules of the package that break a rule, as `file: what`. */
export function offending(files: { file: string; text: string }[]): string[] {
  return files.flatMap(({ file, text }) => FORBIDDEN.filter((f) => f.pattern.test(text)).map((f) => `${file}: ${f.what}`))
}

describe('@dbdatakit/sync-ui', () => {
  const root = import.meta.dirname

  it('authors only: no module watches a replication, calls a Runner route or talks to a server other than through the frame\'s draft', () => {
    const files = sources(root).map((file) => ({ file: relative(root, file), text: readFileSync(file, 'utf8') }))
    expect(files.length).toBeGreaterThan(10)
    expect(offending(files)).toEqual([])
  })

  it('fails, naming the module, when one imports @microsoft/signalr or calls a Runner route', () => {
    expect(offending([
      { file: 'a.tsx', text: "import * as signalR from '@microsoft/signalr'" },
      { file: 'b.ts', text: "const url = `/api/v1/tasks/${p}/dev/load/run`" },
      { file: 'c.ts', text: 'export const ok = 1' },
    ])).toEqual(['a.tsx: imports @microsoft/signalr', 'b.ts: calls a Runner route'])
  })
})
