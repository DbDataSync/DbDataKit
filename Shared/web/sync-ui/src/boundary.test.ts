import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative, sep } from 'node:path'
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
  { what: 'calls a server itself', pattern: /\bfetch\(|\bnew\s+WebSocket\b|\bEventSource\b|\brequest\s*[<(]|workspaceApi|deployApi/ },
]

/** The one module that asks the Workspace server: it may call `request`, and only the draft's sync routes. */
export const CLIENT = 'api/syncApi.ts'

/** The routes a module names (`/api/...` up to a quote, a space or a template's end). */
const routes = (text: string) => [...text.matchAll(/\/api\/[^'"`\s]*/g)].map((m) => m[0])

/** The modules of the package that break a rule, as `file: what`. */
export function offending(files: { file: string; text: string }[]): string[] {
  return files.flatMap(({ file, text }) => {
    if (file === CLIENT) {
      const found = FORBIDDEN.filter((f) => f.what !== 'calls a server itself' && f.pattern.test(text)).map((f) => `${file}: ${f.what}`)
      if (/\bfetch\(|\bnew\s+WebSocket\b|\bEventSource\b|workspaceApi|deployApi/.test(text)) found.push(`${file}: calls a server other than with request`)
      for (const route of routes(text)) if (!/^\/api\/v1\/projects\/.*\/workspaces\/.*\/sync/.test(route)) found.push(`${file}: names ${route}, not a sync route of a draft`)
      return found
    }
    return FORBIDDEN.filter((f) => f.pattern.test(text)).map((f) => `${file}: ${f.what}`)
  })
}

describe('@dbdatakit/sync-ui', () => {
  const root = import.meta.dirname

  it('authors only: no module watches a replication or calls a Runner route, and only the client asks the Workspace, its sync routes only', () => {
    const files = sources(root).map((file) => ({ file: relative(root, file).split(sep).join('/'), text: readFileSync(file, 'utf8') }))
    expect(files.length).toBeGreaterThan(30)
    expect(files.some((f) => f.file === CLIENT)).toBe(true)
    expect(offending(files)).toEqual([])
  })

  it('fails, naming the module, when one imports @microsoft/signalr, calls a Runner route or asks a server outside the client', () => {
    expect(offending([
      { file: 'a.tsx', text: "import * as signalR from '@microsoft/signalr'" },
      { file: 'b.ts', text: "const url = `/api/v1/tasks/${p}/dev/load/run`" },
      { file: 'c.ts', text: 'export const ok = 1' },
      { file: 'd.ts', text: "request<X>('/api/v1/projects/p/workspaces/w/sync/capabilities')" },
    ])).toEqual(['a.tsx: imports @microsoft/signalr', 'b.ts: calls a Runner route', 'd.ts: calls a server itself'])
  })

  it('fails when the client names a route other than a sync route of a draft, or a Runner\'s', () => {
    expect(offending([{ file: CLIENT, text: "request(`/api/v1/projects/${p}/workspaces/${w}/sync/script-slots`)" }])).toEqual([])
    expect(offending([{ file: CLIENT, text: "request(`/api/v1/projects/${p}/workspaces/${w}/commit`)" }])).toEqual([`${CLIENT}: names /api/v1/projects/\${p}/workspaces/\${w}/commit, not a sync route of a draft`])
    expect(offending([{ file: CLIENT, text: "request('/api/v1/runs')" }])).toEqual([`${CLIENT}: calls a Runner route`, `${CLIENT}: names /api/v1/runs, not a sync route of a draft`])
  })
})
