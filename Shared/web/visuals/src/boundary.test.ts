import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name)
    return statSync(path).isDirectory() ? sources(path) : /\.(ts|tsx)$/.test(name) && !name.endsWith('.test.ts') && !name.endsWith('.test.tsx') ? [path] : []
  })
}

/** The shared visuals are used by the console, apps and assistants alike, so they may not depend on any product or on the console (plan 16 section 2a). */
describe('@dbdatakit/visuals', () => {
  it('imports no other workspace', () => {
    const offending = sources(import.meta.dirname).flatMap((file) =>
      [...readFileSync(file, 'utf8').matchAll(/from\s+['"]([^'"]+)['"]/g)]
        .map((m) => m[1]!)
        .filter((spec) => spec.startsWith('@dbdatakit/') || /(^|\/)(sync|proxy|build|console|shell|admin)(\/|$)/i.test(spec))
        .map((spec) => `${file}: ${spec}`))
    expect(offending).toEqual([])
  })
})
